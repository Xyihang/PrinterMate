using System.Text;
using PrinterMate.Core;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Elevation;
using PrinterMate.Security;

namespace PrinterMate.Elevated;

/// <summary>
/// 提权辅助进程。唯一职责:在管理员上下文中执行驱动/队列的安装步骤。
/// 由主程序通过 ShellExecute(verb=runas)拉起,执行完立即退出,不作为服务常驻。
/// </summary>
internal static class Program
{
    private const string RequestArgument = "--request";
    private const string ResultArgument = "--result";
    private static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(15);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var requestPath = ReadArgument(args, RequestArgument);
        var resultPath = ReadArgument(args, ResultArgument);

        if (string.IsNullOrWhiteSpace(requestPath) || string.IsNullOrWhiteSpace(resultPath))
        {
            Console.Error.WriteLine($"用法:PrinterMate.Elevated {RequestArgument} <请求文件> {ResultArgument} <结果文件>");
            return 2;
        }

        if (!IsTrustedRequestPath(requestPath) || !IsTrustedRequestPath(resultPath))
        {
            Console.Error.WriteLine("安全校验失败:请求文件与结果文件必须位于本机的临时工作目录中。");
            return 3;
        }

        var request = ElevatedJson.DeserializeFile<ElevatedInstallRequest>(requestPath);
        if (request is null)
        {
            Console.Error.WriteLine("安全校验失败:无法解析请求文件或请求内容为空。");
            return 3;
        }

        var response = Execute(request);
        WriteResponse(resultPath, response);

        Console.WriteLine(response.Summary);
        return response.Success ? 0 : 1;
    }

    private static ElevatedInstallResponse Execute(ElevatedInstallRequest request)
    {
        var steps = new List<ElevatedStepRecord>();
        var audit = new AuditLog();

        try
        {
            var validation = Validate(request);
            if (validation is not null)
            {
                audit.Write("提权安装-参数校验", request.QueueName, "拒绝", validation);
                return ElevatedInstallResponse.Failure(request.RequestId, validation, steps);
            }

            var stepsRunner = new PrinterInstallSteps();

            switch (request.Action)
            {
                case ElevatedActions.InstallPrinter:
                    return InstallPrinter(request, stepsRunner, steps, audit);

                case ElevatedActions.RemovePrinter:
                {
                    var step = stepsRunner.RemoveQueue(request.QueueName);
                    steps.Add(step);
                    audit.Write("提权卸载-删除队列", request.QueueName, step.State, step.Message);
                    return Summarize(request, steps);
                }

                case ElevatedActions.RemoveDriver:
                {
                    var step = stepsRunner.RemoveDriver(request.DriverName);
                    steps.Add(step);
                    audit.Write("提权卸载-删除驱动", request.DriverName, step.State, step.Message);
                    return Summarize(request, steps);
                }

                default:
                    return ElevatedInstallResponse.Failure(request.RequestId, $"不支持的动作:{request.Action}", steps);
            }
        }
        catch (Exception ex)
        {
            audit.Write("提权安装-异常", request.QueueName, "失败", ex.ToString());
            return ElevatedInstallResponse.Failure(request.RequestId, $"提权安装过程中发生异常:{ex.Message}", steps);
        }
    }

    private static ElevatedInstallResponse InstallPrinter(
        ElevatedInstallRequest request,
        PrinterInstallSteps stepsRunner,
        List<ElevatedStepRecord> steps,
        AuditLog audit)
    {
        var packageVerified = false;

        // Step 1:驱动包再次校验(纵深防御,不信任上游)
        if (!string.IsNullOrWhiteSpace(request.InfPath))
        {
            var verify = VerifyPackage(request, out var sha, out var signer);
            steps.Add(new ElevatedStepRecord(
                InstallStep.VerifyPackage.ToString(),
                verify is null ? InstallStepState.Succeeded.ToString() : InstallStepState.Failed.ToString(),
                verify ?? "驱动包哈希与数字签名校验通过。",
                "SHA256 + WinVerifyTrust",
                verify is null ? 0 : 1,
                $"SHA256={sha};签名主体={signer}",
                0));

            audit.Write("提权安装-驱动包校验", request.InfPath, verify is null ? "通过" : "拒绝", verify ?? "校验通过", request.SourceUrl ?? string.Empty, sha, signer);

            if (verify is not null)
            {
                return ElevatedInstallResponse.Failure(request.RequestId, verify, steps);
            }

            packageVerified = true;
        }

        if (packageVerified && !string.IsNullOrWhiteSpace(request.InfPath))
        {
            var add = stepsRunner.AddDriverToStore(request.InfPath);
            steps.Add(add);
            audit.Write("提权安装-pnputil", request.InfPath, add.State, add.Message);
            if (add.State == InstallStepState.Failed.ToString())
            {
                return Summarize(request, steps);
            }

            var register = stepsRunner.RegisterDriver(request.DriverName, request.InfPath);
            steps.Add(register);
            audit.Write("提权安装-注册驱动", request.DriverName, register.State, register.Message);
            if (register.State == InstallStepState.Failed.ToString())
            {
                return Summarize(request, steps);
            }
        }

        // 共享队列走连接方式,其余走本地端口
        if (request.IsRemoteShare && !string.IsNullOrWhiteSpace(request.Port.SharePath))
        {
            var connect = stepsRunner.CreateSharedConnection(request.Port.SharePath, request.QueueName);
            steps.Add(connect);
            audit.Write("提权安装-连接共享队列", request.Port.SharePath, connect.State, connect.Message);
            return Summarize(request, steps);
        }

        var port = stepsRunner.CreatePort(request.Port);
        steps.Add(port);
        audit.Write("提权安装-创建端口", request.Port.PortName, port.State, port.Message);
        if (port.State == InstallStepState.Failed.ToString())
        {
            return Summarize(request, steps);
        }

        var queue = stepsRunner.CreateQueue(request.QueueName, request.DriverName, request.Port.PortName);
        steps.Add(queue);
        audit.Write("提权安装-创建队列", request.QueueName, queue.State, queue.Message);
        if (queue.State == InstallStepState.Failed.ToString())
        {
            return Summarize(request, steps);
        }

        var verifyStep = stepsRunner.Verify(request.QueueName, request.DriverName);
        steps.Add(verifyStep);
        audit.Write("提权安装-回读校验", request.QueueName, verifyStep.State, verifyStep.Message);

        return Summarize(request, steps);
    }

    /// <summary>校验驱动包。返回 null 表示通过,否则返回中文失败原因。</summary>
    private static string? VerifyPackage(ElevatedInstallRequest request, out string sha256, out string signerSubject)
    {
        sha256 = string.Empty;
        signerSubject = string.Empty;

        var infPath = request.InfPath!;

        // 厂商签名的载体是驱动包内的 .cat 目录文件;此处再次独立校验,不信任上游结论。
        var verification = DriverPackageVerifier.Verify(infPath, request.ExpectedSha256, request.ExpectedSignerSubject);
        sha256 = verification.Sha256;
        signerSubject = verification.SignerSubject;

        return verification.Passed ? null : verification.Message;
    }

    private static string? Validate(ElevatedInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Action))
        {
            return "请求缺少动作字段。";
        }

        if (DateTimeOffset.Now - request.IssuedAt > MaxRequestAge)
        {
            return "请求已过期(超过 15 分钟),为安全起见拒绝执行,请重新发起安装。";
        }

        if (request.IssuedAt - DateTimeOffset.Now > TimeSpan.FromMinutes(1))
        {
            return "请求时间戳异常,拒绝执行。";
        }

        if (request.Action == ElevatedActions.InstallPrinter)
        {
            if (string.IsNullOrWhiteSpace(request.QueueName))
            {
                return "缺少打印队列名称。";
            }

            if (string.IsNullOrWhiteSpace(request.DriverName))
            {
                return "缺少驱动名称。";
            }

            if (request.Port is null)
            {
                return "缺少端口信息。";
            }

            foreach (var value in new[] { request.QueueName, request.DriverName, request.Port.PortName })
            {
                if (value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
                {
                    return "参数中包含非法字符,已拒绝执行。";
                }
            }
        }

        return null;
    }

    private static ElevatedInstallResponse Summarize(ElevatedInstallRequest request, List<ElevatedStepRecord> steps)
    {
        var failed = steps.FirstOrDefault(s => s.State == InstallStepState.Failed.ToString());
        if (failed is not null)
        {
            return new ElevatedInstallResponse(request.RequestId, false, failed.Message, steps, failed.Message);
        }

        return new ElevatedInstallResponse(
            request.RequestId,
            true,
            $"打印队列「{request.QueueName}」已就绪。",
            steps,
            string.Empty);
    }

    /// <summary>请求/结果文件必须位于 %TEMP%\PrinterMate 下,避免被其它进程诱导读取任意文件。</summary>
    private static bool IsTrustedRequestPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PrinterMate"));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteResponse(string resultPath, ElevatedInstallResponse response)
    {
        try
        {
            File.WriteAllText(resultPath, ElevatedJson.Serialize(response), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"写入结果文件失败:{ex.Message}");
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
