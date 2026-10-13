using PrinterMate.Catalog;
using PrinterMate.Core.Elevation;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Printing;
using PrinterMate.Security;

namespace PrinterMate.Core.Drivers;

/// <summary>
/// 驱动安装编排器:预检 → 校验 → 提权执行 → 回读 → 测试页。
/// 主程序自身不常驻管理员,所有需要提权的步骤都交给 PrinterMate.Elevated。
/// </summary>
public sealed class PrinterInstaller
{
    private readonly DriverLibrary _library;
    private readonly AuditLog _audit;
    private readonly WppModeDetector _wpp;
    private readonly ElevatedLauncher _launcher;

    public PrinterInstaller(
        DriverLibrary library,
        AuditLog? audit = null,
        WppModeDetector? wpp = null,
        ElevatedLauncher? launcher = null)
    {
        _library = library;
        _audit = audit ?? new AuditLog();
        _wpp = wpp ?? new WppModeDetector();
        _launcher = launcher ?? new ElevatedLauncher();
    }

    /// <summary>生成安装计划(供 UI 展示并二次确认)。</summary>
    public OperationResult<InstallPlan> BuildPlan(DiscoveredPrinter printer, DriverCandidate candidate, PortSpec port, string? queueName = null)
    {
        var warnings = new List<string>();
        var wpp = _wpp.Detect();

        if (wpp.Enabled && candidate.Source != DriverSource.ClassDriver)
        {
            return OperationResult<InstallPlan>.Fail(
                "系统已启用 Windows Protected Print Mode,禁止安装第三方打印驱动。",
                "请使用免驱通道(L0):该模式下仅系统内置类驱动可用。",
                OperationStatus.NotSupported);
        }

        var targetQueue = string.IsNullOrWhiteSpace(queueName) ? BuildDefaultQueueName(printer) : queueName!;
        var requiresElevation = !ElevatedLauncher.IsProcessElevated();

        if (candidate.Source == DriverSource.ClassDriver)
        {
            warnings.Add("使用系统内置类驱动,无需下载或安装驱动包。");
        }
        else if (candidate.Source == DriverSource.BuiltinUpd)
        {
            var entry = _library.FindByDriverName(candidate.DriverName);
            if (entry is null)
            {
                return OperationResult<InstallPlan>.Fail(
                    $"内置驱动库中没有「{candidate.DriverName}」。",
                    "请在驱动管理页重新匹配或改用手动选择。");
            }

            var availability = _library.CheckAvailability(entry, DriverMatchEngine.CurrentArchitecture, DriverMatchEngine.CurrentOsTag);
            if (!availability.Available)
            {
                return OperationResult<InstallPlan>.Fail(availability.Reason, "请改用在线补齐(L2)或导入该品牌的驱动包后重试。");
            }
        }

        if (!ElevatedLauncher.IsHelperAvailable && requiresElevation)
        {
            warnings.Add("未找到提权辅助进程,安装可能因权限不足失败。");
        }

        var risk = candidate.Source switch
        {
            DriverSource.ClassDriver =>
                $"即将创建打印队列「{targetQueue}」,使用系统内置的「{candidate.DriverName}」。\n不会安装任何第三方驱动,也不会修改系统打印安全策略。",
            DriverSource.BuiltinUpd =>
                $"即将在本机安装驱动「{candidate.DriverName}」并创建打印队列「{targetQueue}」。\n驱动来自 PrinterMate 内置驱动库,安装前会校验 SHA256 与数字签名。此操作需要管理员权限。",
            _ =>
                $"即将从官方源下载并安装驱动「{candidate.DriverName}」,然后创建打印队列「{targetQueue}」。\n下载来源经过白名单校验,安装前会校验 HTTPS 证书、SHA256 与数字签名。此操作需要管理员权限。",
        };

        var plan = new InstallPlan(
            targetQueue,
            candidate.DriverName,
            candidate.Source,
            candidate.InfPath,
            candidate.Sha256,
            _library.FindByDriverName(candidate.DriverName)?.SignerSubject,
            port,
            requiresElevation,
            candidate.Source == DriverSource.Online,
            warnings,
            risk);

        return OperationResult<InstallPlan>.Ok(plan, "安装计划已生成,请确认后执行。");
    }

    /// <summary>执行安装计划。</summary>
    public OperationResult<InstallReport> Install(InstallPlan plan, IProgress<InstallStepResult>? progress = null)
    {
        var started = DateTimeOffset.Now;
        var steps = new List<InstallStepResult>();

        void Report(InstallStepResult step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        try
        {
            // ── Step 0 预检 ─────────────────────────────
            var precheck = RunPrecheck(plan);
            Report(precheck);
            if (precheck.State == InstallStepState.Failed)
            {
                return Finish(plan, steps, started, precheck.Message);
            }

            // ── Step 1 驱动包校验(主程序侧先校验一遍) ────
            if (!string.IsNullOrWhiteSpace(plan.InfPath))
            {
                var verify = RunVerifyPackage(plan);
                Report(verify);
                if (verify.State == InstallStepState.Failed)
                {
                    _audit.Write("安装-驱动包校验", plan.InfPath!, "拒绝", verify.Message, string.Empty, plan.Sha256 ?? string.Empty, plan.ExpectedSignerSubject ?? string.Empty);
                    return Finish(plan, steps, started, verify.Message);
                }
            }

            // ── Step 2..6 提权执行 ─────────────────────
            var request = new ElevatedInstallRequest(
                Guid.NewGuid().ToString("N"),
                ElevatedActions.InstallPrinter,
                plan.QueueName,
                plan.DriverName,
                plan.InfPath,
                plan.Sha256,
                plan.ExpectedSignerSubject,
                null,
                null,
                new ElevatedPortRequest(plan.Port.Kind.ToString(), plan.Port.PortName, plan.Port.HostAddress, plan.Port.PortNumber, plan.Port.SharePath),
                DateTimeOffset.Now);

            IReadOnlyList<ElevatedStepRecord> records;

            if (!plan.RequiresElevation && ElevatedLauncher.IsProcessElevated())
            {
                records = ExecuteInProcess(request);
            }
            else
            {
                var elevated = _launcher.Execute(request);
                if (!elevated.IsSuccess)
                {
                    var failedStep = new InstallStepResult(
                        InstallStep.CreateQueue,
                        elevated.Status == OperationStatus.NeedsUserAction ? InstallStepState.Skipped : InstallStepState.Failed,
                        elevated.Message,
                        "PrinterMate.Elevated.exe",
                        -1,
                        elevated.Suggestion ?? string.Empty,
                        TimeSpan.Zero);
                    Report(failedStep);
                    _audit.Write("安装-提权", plan.QueueName, "失败", elevated.Message);
                    return new OperationResult<InstallReport>
                    {
                        Status = elevated.Status,
                        Value = new InstallReport(plan.QueueName, plan.DriverName, plan.Source, false, steps, DateTimeOffset.Now - started, elevated.Message),
                        Message = elevated.Message,
                        Suggestion = elevated.Suggestion,
                    };
                }

                records = elevated.Value!.Steps;
            }

            foreach (var record in records)
            {
                Report(new InstallStepResult(
                    Enum.TryParse<InstallStep>(record.Step, out var step) ? step : InstallStep.CreateQueue,
                    Enum.TryParse<InstallStepState>(record.State, out var state) ? state : InstallStepState.Failed,
                    record.Message,
                    record.Command,
                    record.ExitCode,
                    record.Output,
                    TimeSpan.FromMilliseconds(record.DurationMs)));
            }

            var failed = steps.LastOrDefault(s => s.State == InstallStepState.Failed);
            if (failed is not null)
            {
                return Finish(plan, steps, started, failed.Message);
            }

            var summary = $"{plan.Source switch
            {
                DriverSource.ClassDriver => "免驱队列",
                DriverSource.BuiltinUpd => "内置驱动",
                _ => "在线驱动",
            }}「{plan.DriverName}」安装完成,队列「{plan.QueueName}」已就绪。";

            _audit.Write("安装-完成", plan.QueueName, "成功", summary, string.Empty, plan.Sha256 ?? string.Empty, plan.ExpectedSignerSubject ?? string.Empty);

            return OperationResult<InstallReport>.Ok(
                new InstallReport(plan.QueueName, plan.DriverName, plan.Source, true, steps, DateTimeOffset.Now - started, summary),
                summary);
        }
        catch (Exception ex)
        {
            _audit.Write("安装-异常", plan.QueueName, "失败", ex.ToString());
            var failure = ex.ToFailure<InstallReport>("安装打印驱动");
            return new OperationResult<InstallReport>
            {
                Status = failure.Status,
                Message = failure.Message,
                Suggestion = failure.Suggestion,
                Exception = ex,
                Value = new InstallReport(plan.QueueName, plan.DriverName, plan.Source, false, steps, DateTimeOffset.Now - started, failure.Message),
            };
        }
    }

    /// <summary>打印系统测试页(在用户会话中执行,不需要提权)。</summary>
    public OperationResult<string> PrintSystemTestPage(string queueName)
    {
        try
        {
            SystemTestPage.Print(queueName);
            _audit.Write("测试页", queueName, "成功", "已提交系统测试页打印任务。");
            return OperationResult<string>.Ok(queueName, $"已向「{queueName}」提交系统测试页,请查看打印机出纸情况。");
        }
        catch (Exception ex)
        {
            _audit.Write("测试页", queueName, "失败", ex.Message);
            return ex.ToFailure<string>("打印系统测试页");
        }
    }

    private static string BuildDefaultQueueName(DiscoveredPrinter printer)
    {
        var baseName = string.IsNullOrWhiteSpace(printer.Model) ? printer.DisplayName : printer.Model;
        baseName = baseName.Trim();
        var suffix = printer.Connection switch
        {
            ConnectionKind.Ipp or ConnectionKind.Wsd or ConnectionKind.RawTcp when !string.IsNullOrWhiteSpace(printer.Address) => $" ({printer.Address})",
            _ => string.Empty,
        };

        var name = (baseName + suffix).Trim();
        return name.Length <= 80 ? name : name[..80];
    }

    private InstallStepResult RunPrecheck(InstallPlan plan)
    {
        var start = TimeSpan.Zero;
        var wpp = _wpp.Detect();

        if (wpp.Enabled && plan.Source != DriverSource.ClassDriver)
        {
            return new InstallStepResult(
                InstallStep.Precheck,
                InstallStepState.Failed,
                "系统已启用 Windows Protected Print Mode,禁止安装第三方打印驱动。",
                "WPP 检测",
                -1,
                wpp.Detail,
                start);
        }

        // 同名队列冲突检查
        var existing = Interop.SpoolerApi.GetPrinter(plan.QueueName);
        if (existing is not null)
        {
            var sameDriver = string.Equals(existing.DriverName, plan.DriverName, StringComparison.OrdinalIgnoreCase);
            if (sameDriver)
            {
                return new InstallStepResult(
                    InstallStep.Precheck,
                    InstallStepState.Succeeded,
                    $"队列「{plan.QueueName}」已存在且使用相同驱动,将直接复用。",
                    "Get-Printer",
                    0,
                    existing.DriverName,
                    start);
            }

            return new InstallStepResult(
                InstallStep.Precheck,
                InstallStepState.Failed,
                $"已存在同名打印队列「{plan.QueueName}」,但其驱动为「{existing.DriverName}」,与计划安装的「{plan.DriverName}」不同。",
                "Get-Printer",
                0,
                "请在安装前修改队列名称,或先删除同名队列。",
                start);
        }

        return new InstallStepResult(
            InstallStep.Precheck,
            InstallStepState.Succeeded,
            "环境预检通过。",
            "WPP 检测 + 队列冲突检查",
            0,
            wpp.Detail,
            start);
    }

    private InstallStepResult RunVerifyPackage(InstallPlan plan)
    {
        var infPath = plan.InfPath!;

        // 注意:厂商签名的载体是驱动包内的 .cat 目录文件,不是 .inf 本身。
        var verification = DriverPackageVerifier.Verify(infPath, plan.Sha256, plan.ExpectedSignerSubject);

        return new InstallStepResult(
            InstallStep.VerifyPackage,
            verification.Passed ? InstallStepState.Succeeded : InstallStepState.Failed,
            verification.Passed
                ? "驱动包哈希与数字签名校验通过。"
                : $"驱动包校验未通过:{verification.Message}",
            "SHA256 + WinVerifyTrust(目录文件)",
            0,
            $"SHA256={verification.Sha256};签名文件={verification.CatalogPath};签名主体={verification.SignerSubject}",
            TimeSpan.Zero);
    }

    /// <summary>当前进程已是管理员时,直接在本进程内执行同样的步骤。</summary>
    private static IReadOnlyList<ElevatedStepRecord> ExecuteInProcess(ElevatedInstallRequest request)
    {
        var runner = new PrinterInstallSteps();
        var steps = new List<ElevatedStepRecord>();

        if (!string.IsNullOrWhiteSpace(request.InfPath))
        {
            var add = runner.AddDriverToStore(request.InfPath);
            steps.Add(add);
            if (add.State == InstallStepState.Failed.ToString())
            {
                return steps;
            }

            var register = runner.RegisterDriver(request.DriverName, request.InfPath);
            steps.Add(register);
            if (register.State == InstallStepState.Failed.ToString())
            {
                return steps;
            }
        }

        if (request.IsRemoteShare && !string.IsNullOrWhiteSpace(request.Port.SharePath))
        {
            steps.Add(runner.CreateSharedConnection(request.Port.SharePath, request.QueueName));
            return steps;
        }

        var port = runner.CreatePort(request.Port);
        steps.Add(port);
        if (port.State == InstallStepState.Failed.ToString())
        {
            return steps;
        }

        var queue = runner.CreateQueue(request.QueueName, request.DriverName, request.Port.PortName);
        steps.Add(queue);
        if (queue.State == InstallStepState.Failed.ToString())
        {
            return steps;
        }

        steps.Add(runner.Verify(request.QueueName, request.DriverName));
        return steps;
    }

    private static OperationResult<InstallReport> Finish(InstallPlan plan, List<InstallStepResult> steps, DateTimeOffset started, string message)
    {
        var report = new InstallReport(plan.QueueName, plan.DriverName, plan.Source, false, steps, DateTimeOffset.Now - started, message);
        return new OperationResult<InstallReport>
        {
            Status = OperationStatus.Failed,
            Value = report,
            Message = message,
            Suggestion = "可展开执行明细查看每一步的命令与返回码;如需进一步排查,请导出诊断报告。",
        };
    }
}
