using PrinterMate.Core.Elevation;
using PrinterMate.Core.Infrastructure;

namespace PrinterMate.Core.Drivers;

/// <summary>
/// 驱动安装的原子步骤。每一步都捕获实际执行的命令、返回码与输出,
/// 既可以由提权辅助进程调用,也可以在已具备管理员权限时由主程序直接调用。
///
/// 约定:PowerShell 脚本使用 <c>$$"""</c> 原始插值字符串,
/// 因此 C# 插值写作 <c>{{expr}}</c>,PowerShell 的花括号直接写 <c>{</c> <c>}</c>。
/// </summary>
public sealed class PrinterInstallSteps
{
    /// <summary>pnputil 返回"驱动包未通过签名或完整性校验"。</summary>
    public const int PnputilSignatureFailure = unchecked((int)0xE0000247);

    /// <summary>操作成功但需要重启。</summary>
    public const int ErrorSuccessRebootRequired = 3010;

    private readonly PowerShellRunner _powerShell;

    public PrinterInstallSteps(PowerShellRunner? powerShell = null)
    {
        _powerShell = powerShell ?? new PowerShellRunner(TimeSpan.FromMinutes(3));
    }

    /// <summary>Step 2:pnputil /add-driver &lt;inf&gt; /install。</summary>
    public ElevatedStepRecord AddDriverToStore(string infPath)
    {
        var command = $"pnputil /add-driver \"{infPath}\" /install";

        if (!File.Exists(infPath))
        {
            return Record(InstallStep.AddDriverToStore, InstallStepState.Failed, $"驱动 INF 文件不存在:{infPath}", command, -1, string.Empty, TimeSpan.Zero);
        }

        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        var result = ProcessRunner.Run(pnputil, new[] { "/add-driver", infPath, "/install" }, TimeSpan.FromMinutes(3));

        if (result.Success)
        {
            return Record(InstallStep.AddDriverToStore, InstallStepState.Succeeded, "驱动已加入驱动仓库。", command, result.ExitCode, result.ShortError, result.Duration);
        }

        if (result.ExitCode == ErrorSuccessRebootRequired)
        {
            return Record(InstallStep.AddDriverToStore, InstallStepState.Succeeded, "驱动已加入驱动仓库,需要**重启系统**后才会生效(程序不会自动重启)。", command, result.ExitCode, result.ShortError, result.Duration);
        }

        var (message, suggestion) = DescribePnputilFailure(result);
        return Record(InstallStep.AddDriverToStore, InstallStepState.Failed, $"{message} {suggestion}", command, result.ExitCode, result.ShortError, result.Duration);
    }

    /// <summary>Step 3:Add-PrinterDriver -Name -InfPath,并在名称不符时按 INF 回读真实名称。</summary>
    public ElevatedStepRecord RegisterDriver(string driverName, string infPath)
    {
        var infFileName = Path.GetFileName(infPath);
        var script = $$"""
            $name = {{Q(driverName)}}
            $inf  = {{Q(infPath)}}
            $infName = {{Q(infFileName)}}

            $existing = Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue
            if ($existing) { Write-Output ('ALREADY:' + $existing.Name); exit 0 }

            $byInf = Get-PrinterDriver -ErrorAction SilentlyContinue |
                     Where-Object { $_.InfPath -and ($_.InfPath -like ('*' + $infName)) } |
                     Select-Object -First 1
            if ($byInf) { Write-Output ('ALREADY:' + $byInf.Name); exit 0 }

            try {
                Add-PrinterDriver -Name $name -InfPath $inf -ErrorAction Stop
                $actual = Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue
                if ($actual) { Write-Output ('ADDED:' + $actual.Name) } else { Write-Output ('ADDED:' + $name) }
            }
            catch {
                $fallback = Get-PrinterDriver -ErrorAction SilentlyContinue |
                            Where-Object { $_.InfPath -and ($_.InfPath -like ('*' + $infName)) } |
                            Select-Object -First 1
                if ($fallback) { Write-Output ('RECOVERED:' + $fallback.Name); exit 0 }
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;

        var result = _powerShell.RunScript(script, asJson: false);
        var command = $"Add-PrinterDriver -Name \"{driverName}\" -InfPath \"{infPath}\"";
        var output = result.StandardOutput.Trim();

        if (result.Success)
        {
            var message = output.StartsWith("ALREADY:", StringComparison.OrdinalIgnoreCase)
                ? $"驱动已注册过,直接复用:{output["ALREADY:".Length..]}"
                : output.StartsWith("RECOVERED:", StringComparison.OrdinalIgnoreCase)
                    ? $"名称与 INF 不一致,已按 INF 回读并复用真实驱动名:{output["RECOVERED:".Length..]}"
                    : "打印驱动注册成功。";

            return Record(InstallStep.RegisterDriver, InstallStepState.Succeeded, message, command, result.ExitCode, output, result.Duration);
        }

        return Record(InstallStep.RegisterDriver, InstallStepState.Failed,
            $"注册打印驱动失败:{result.StandardError.Trim()}", command, result.ExitCode, result.StandardError.Trim(), result.Duration);
    }

    /// <summary>Step 4:按连接方式创建端口。USB 端口由 PnP 自动创建,这里只做校验。</summary>
    public ElevatedStepRecord CreatePort(ElevatedPortRequest port)
    {
        if (IsKind(port, "Usb") || IsKind(port, "Existing"))
        {
            var exists = PortExists(port.PortName, out var probeDuration);
            var label = IsKind(port, "Usb") ? "USB 端口" : "现有端口";

            return exists
                ? Record(InstallStep.CreatePort, InstallStepState.Succeeded, $"{label} {port.PortName} 已存在,直接使用。", "Get-PrinterPort", 0, port.PortName, probeDuration)
                : Record(InstallStep.CreatePort, InstallStepState.Failed,
                    IsKind(port, "Usb")
                        ? $"未找到 USB 端口 {port.PortName},请确认打印机已开机、USB 线已连接并被系统识别。"
                        : $"端口 {port.PortName} 不存在。",
                    "Get-PrinterPort", 1, string.Empty, probeDuration);
        }

        if (IsKind(port, "Shared"))
        {
            return Record(InstallStep.CreatePort, InstallStepState.Skipped, "共享队列不需要创建本地端口。", string.Empty, 0, string.Empty, TimeSpan.Zero);
        }

        var lastOutput = string.Empty;
        var lastExit = -1;
        var lastCommand = string.Empty;
        var elapsed = TimeSpan.Zero;

        foreach (var (label, script) in BuildPortScripts(port))
        {
            var result = _powerShell.RunScript(script, asJson: false);
            elapsed += result.Duration;
            lastExit = result.ExitCode;
            lastOutput = (result.StandardOutput + result.StandardError).Trim();
            lastCommand = label;

            if (result.Success && result.StandardOutput.Contains("CREATED", StringComparison.OrdinalIgnoreCase))
            {
                return Record(InstallStep.CreatePort, InstallStepState.Succeeded, $"已创建{PortKindText(port.Kind)}({port.PortName})。", label, 0, lastOutput, elapsed);
            }

            if (result.Success && result.StandardOutput.Contains("EXISTS", StringComparison.OrdinalIgnoreCase))
            {
                return Record(InstallStep.CreatePort, InstallStepState.Succeeded, $"端口 {port.PortName} 已存在,直接复用。", label, 0, lastOutput, elapsed);
            }
        }

        return Record(InstallStep.CreatePort, InstallStepState.Failed,
            $"创建端口 {port.PortName} 失败:{lastOutput}",
            lastCommand, lastExit, lastOutput, elapsed);
    }

    /// <summary>Step 5:创建本机打印队列。</summary>
    public ElevatedStepRecord CreateQueue(string queueName, string driverName, string portName)
    {
        var script = $$"""
            if (Get-Printer -Name {{Q(queueName)}} -ErrorAction SilentlyContinue) { Write-Output 'EXISTS'; exit 0 }
            try {
                Add-Printer -Name {{Q(queueName)}} -DriverName {{Q(driverName)}} -PortName {{Q(portName)}} -ErrorAction Stop
                Write-Output 'CREATED'
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;

        var command = $"Add-Printer -Name \"{queueName}\" -DriverName \"{driverName}\" -PortName \"{portName}\"";
        var result = _powerShell.RunScript(script, asJson: false);
        var output = result.StandardOutput.Trim();

        if (result.Success && output.Contains("CREATED", StringComparison.OrdinalIgnoreCase))
        {
            return Record(InstallStep.CreateQueue, InstallStepState.Succeeded, $"打印队列「{queueName}」创建成功。", command, 0, output, result.Duration);
        }

        if (result.Success && output.Contains("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return Record(InstallStep.CreateQueue, InstallStepState.Succeeded, $"打印队列「{queueName}」已存在,直接复用。", command, 0, output, result.Duration);
        }

        return Record(InstallStep.CreateQueue, InstallStepState.Failed,
            $"创建打印队列失败:{result.StandardError.Trim()}", command, result.ExitCode, result.StandardError.Trim(), result.Duration);
    }

    /// <summary>共享队列:Add-Printer -ConnectionName \\host\printer。</summary>
    public ElevatedStepRecord CreateSharedConnection(string sharePath, string queueName)
    {
        var script = $$"""
            if (Get-Printer -Name {{Q(queueName)}} -ErrorAction SilentlyContinue) { Write-Output 'EXISTS'; exit 0 }
            try {
                Add-Printer -ConnectionName {{Q(sharePath)}} -ErrorAction Stop
                Write-Output 'CREATED'
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;

        var command = $"Add-Printer -ConnectionName \"{sharePath}\"";
        var result = _powerShell.RunScript(script, asJson: false);
        var output = result.StandardOutput.Trim();

        if (result.Success && (output.Contains("CREATED", StringComparison.OrdinalIgnoreCase) || output.Contains("EXISTS", StringComparison.OrdinalIgnoreCase)))
        {
            return Record(InstallStep.CreateQueue, InstallStepState.Succeeded, $"已连接共享打印机 {sharePath}。", command, 0, output, result.Duration);
        }

        return Record(InstallStep.CreateQueue, InstallStepState.Failed,
            $"连接共享打印机失败:{result.StandardError.Trim()}", command, result.ExitCode, result.StandardError.Trim(), result.Duration);
    }

    /// <summary>Step 6:回读校验队列与驱动是否真的就绪。</summary>
    public ElevatedStepRecord Verify(string queueName, string driverName)
    {
        var script = $$"""
            $p = Get-Printer -Name {{Q(queueName)}} -ErrorAction Stop
            $d = Get-PrinterDriver -Name $p.DriverName -ErrorAction SilentlyContinue
            Write-Output ('QUEUE=' + $p.Name + ';DRIVER=' + $p.DriverName + ';PORT=' + $p.PortName + ';DRIVER_OK=' + [bool]$d)
            """;

        var result = _powerShell.RunScript(script, asJson: false);
        var output = result.StandardOutput.Trim();

        if (result.Success && output.Contains("DRIVER_OK=True", StringComparison.OrdinalIgnoreCase))
        {
            return Record(InstallStep.Verify, InstallStepState.Succeeded, $"回读校验通过:{output}", "Get-Printer / Get-PrinterDriver", 0, output, result.Duration);
        }

        return Record(InstallStep.Verify, InstallStepState.Failed,
            $"回读校验失败,队列或驱动未就绪:{output} {result.StandardError.Trim()}",
            "Get-Printer / Get-PrinterDriver", result.ExitCode, output, result.Duration);
    }

    public ElevatedStepRecord RemoveQueue(string queueName)
    {
        var script = $$"""Remove-Printer -Name {{Q(queueName)}} -ErrorAction Stop; Write-Output 'REMOVED'""";
        var result = _powerShell.RunScript(script, asJson: false);
        var command = $"Remove-Printer -Name \"{queueName}\"";

        return result.Success
            ? Record(InstallStep.Done, InstallStepState.Succeeded, $"已删除打印队列「{queueName}」。", command, 0, result.StandardOutput.Trim(), result.Duration)
            : Record(InstallStep.Done, InstallStepState.Failed, $"删除打印队列失败:{result.StandardError.Trim()}", command, result.ExitCode, result.StandardError.Trim(), result.Duration);
    }

    public ElevatedStepRecord RemoveDriver(string driverName)
    {
        var script = $$"""Remove-PrinterDriver -Name {{Q(driverName)}} -ErrorAction Stop; Write-Output 'REMOVED'""";
        var result = _powerShell.RunScript(script, asJson: false);
        var command = $"Remove-PrinterDriver -Name \"{driverName}\"";

        return result.Success
            ? Record(InstallStep.Done, InstallStepState.Succeeded, $"已删除打印驱动「{driverName}」。", command, 0, result.StandardOutput.Trim(), result.Duration)
            : Record(InstallStep.Done, InstallStepState.Failed, $"删除打印驱动失败:{result.StandardError.Trim()}", command, result.ExitCode, result.StandardError.Trim(), result.Duration);
    }

    /// <summary>把 pnputil 的失败码翻译成中文原因与建议。</summary>
    public static (string Message, string Suggestion) DescribePnputilFailure(ProcessResult result)
    {
        if (result.TimedOut)
        {
            return ("pnputil 执行超时,驱动可能仍在后台安装。", "请稍后打开「驱动管理」查看驱动是否已入库,或重启后重试。");
        }

        var text = result.CombinedOutput;

        if (result.ExitCode == PnputilSignatureFailure || text.Contains("0xE0000247", StringComparison.OrdinalIgnoreCase))
        {
            return ("驱动包未通过数字签名校验,系统拒绝安装(0xE0000247)。",
                "请确认驱动文件来自厂商官方源且未被修改;若已开启 Windows Protected Print Mode,请改用免驱通道。");
        }

        if (text.Contains("already", StringComparison.OrdinalIgnoreCase) || text.Contains("已存在", StringComparison.Ordinal))
        {
            return ("该驱动包已存在于驱动仓库。", "可直接进入下一步注册驱动,无需重复添加。");
        }

        if (text.Contains("not compatible", StringComparison.OrdinalIgnoreCase) || text.Contains("不兼容", StringComparison.Ordinal))
        {
            return ("驱动包与当前系统架构或版本不兼容。", "请下载与当前系统架构(x64/ARM64)匹配的驱动包。");
        }

        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || result.ExitCode == 5)
        {
            return ("访问被拒绝,pnputil 需要管理员权限。", "请通过「以管理员身份安装」重新执行。");
        }

        return ($"pnputil 执行失败(返回码 {result.ExitCode}):{result.ShortError}", "请导出诊断报告并检查系统事件日志中的驱动安装记录。");
    }

    private bool PortExists(string portName, out TimeSpan duration)
    {
        var result = _powerShell.RunScript(
            $$"""if (Get-PrinterPort -Name {{Q(portName)}} -ErrorAction SilentlyContinue) { 'OK' } else { 'MISSING' }""",
            asJson: false);

        duration = result.Duration;
        return result.StandardOutput.Contains("OK", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Label, string Script)> BuildPortScripts(ElevatedPortRequest port)
    {
        var name = Q(port.PortName);
        var ip = port.HostAddress is null ? null : Q(port.HostAddress);
        var portNumber = port.PortNumber ?? 9100;

        if (IsKind(port, "Ipp") && ip is not null)
        {
            yield return ($"Add-PrinterPort -Name {port.PortName} -PrinterHostAddress {port.HostAddress} -PortNumber 631", $$"""
                if (Get-PrinterPort -Name {{name}} -ErrorAction SilentlyContinue) { Write-Output 'EXISTS'; exit 0 }
                Add-PrinterPort -Name {{name}} -PrinterHostAddress {{ip}} -PortNumber 631 -ErrorAction Stop
                Write-Output 'CREATED'
                """);
        }

        if (ip is not null)
        {
            var portArg = portNumber == 9100 ? string.Empty : $" -PortNumber {portNumber}";
            yield return ($"Add-PrinterPort -Name {port.PortName} -PrinterHostAddress {port.HostAddress}{portArg}", $$"""
                if (Get-PrinterPort -Name {{name}} -ErrorAction SilentlyContinue) { Write-Output 'EXISTS'; exit 0 }
                Add-PrinterPort -Name {{name}} -PrinterHostAddress {{ip}}{{portArg}} -ErrorAction Stop
                Write-Output 'CREATED'
                """);
        }

        yield return ($"Add-PrinterPort -Name {port.PortName}", $$"""
            if (Get-PrinterPort -Name {{name}} -ErrorAction SilentlyContinue) { Write-Output 'EXISTS'; exit 0 }
            Add-PrinterPort -Name {{name}} -ErrorAction Stop
            Write-Output 'CREATED'
            """);
    }

    private static bool IsKind(ElevatedPortRequest port, string kind)
        => string.Equals(port.Kind, kind, StringComparison.OrdinalIgnoreCase);

    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    private static string PortKindText(string kind) => kind switch
    {
        "Ipp" => " IPP 端口",
        "RawTcp" => " RAW 端口",
        "Lpd" => " LPD 端口",
        _ => " 端口",
    };

    private static ElevatedStepRecord Record(
        InstallStep step,
        InstallStepState state,
        string message,
        string command,
        int exitCode,
        string output,
        TimeSpan duration)
        => new(step.ToString(), state.ToString(), message, command, exitCode, output, duration.TotalMilliseconds);
}
