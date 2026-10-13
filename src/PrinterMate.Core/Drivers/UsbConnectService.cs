using PrinterMate.Core.Discovery;

namespace PrinterMate.Core.Drivers;

/// <summary>USB 连接计划:设备 + 驱动决策 + 安装计划。</summary>
public sealed record UsbConnectPlan(
    UsbPrinterInfo Device,
    DriverDecision Decision,
    InstallPlan? Plan,
    string Summary,
    string Suggestion)
{
    public bool CanProceed => Plan is not null && Decision.Status == OperationStatus.Success;
}

/// <summary>
/// USB 直连服务。这是本产品最主要的连接路径:
/// 用户插上 USB 线 → 程序识别设备 → 匹配内置驱动 → 安装并创建队列 → 打印测试页。
/// </summary>
public sealed class UsbConnectService
{
    private readonly DriverMatchEngine _engine;
    private readonly PrinterInstaller _installer;
    private readonly LocalPrinterEnumerator _enumerator;

    public UsbConnectService(DriverMatchEngine engine, PrinterInstaller installer, LocalPrinterEnumerator? enumerator = null)
    {
        _engine = engine;
        _installer = installer;
        _enumerator = enumerator ?? new LocalPrinterEnumerator();
    }

    /// <summary>列出当前通过 USB 连接的打印机及其状态。</summary>
    public OperationResult<IReadOnlyList<UsbPrinterInfo>> ListUsbPrinters() => UsbPrinterLocator.Locate(_enumerator);

    /// <summary>为某台 USB 打印机准备连接计划(不执行任何安装)。</summary>
    public OperationResult<UsbConnectPlan> Prepare(UsbPrinterInfo device, string? queueName = null)
    {
        if (device.State == UsbBindingState.Ready)
        {
            return OperationResult<UsbConnectPlan>.Ok(
                new UsbConnectPlan(device, DriverDecision.Unresolved("已就绪", string.Empty), null,
                    $"USB 打印机「{device.Device.DisplayName}」已就绪,端口 {device.PortName},队列「{device.ExistingQueueName}」。",
                    "可直接打印测试页验证。"),
                "该设备无需安装驱动。");
        }

        if (device.State == UsbBindingState.PortMissing)
        {
            return OperationResult<UsbConnectPlan>.Fail(
                "系统尚未为该 USB 打印机分配端口。",
                "请重新插拔 USB 线,或更换到主板直连的 USB 接口后重试;部分打印机需要先开机再连线。",
                OperationStatus.NeedsUserAction);
        }

        // USB 打印机通常不具备 IPP 能力,匹配引擎会走到 L1 内置驱动或 L2 在线源
        var decision = _engine.Decide(device.Device, preferDriverFree: true);
        if (decision.Selected is null)
        {
            var candidates = decision.Candidates.Where(c => c.Installable).ToList();
            var suggestion = candidates.Count > 1
                ? "该型号对应多个驱动,请在驱动管理页手动选择后再连接。"
                : decision.Suggestion;

            return new OperationResult<UsbConnectPlan>
            {
                Status = decision.Status,
                Message = decision.Message,
                Suggestion = suggestion,
                Value = new UsbConnectPlan(device, decision, null, decision.Message, suggestion),
            };
        }

        var portSpec = UsbPrinterLocator.BuildPortSpec(device.PortName, portExists: device.State != UsbBindingState.PortMissing);
        var defaultQueue = queueName
            ?? device.ExistingQueueName
            ?? BuildQueueName(device);

        var plan = _installer.BuildPlan(device.Device, decision.Selected, portSpec, defaultQueue);
        if (!plan.IsSuccess || plan.Value is null)
        {
            return new OperationResult<UsbConnectPlan>
            {
                Status = plan.Status,
                Message = plan.Message,
                Suggestion = plan.Suggestion,
                Value = new UsbConnectPlan(device, decision, null, plan.Message, plan.Suggestion ?? decision.Suggestion),
            };
        }

        return OperationResult<UsbConnectPlan>.Ok(
            new UsbConnectPlan(device, decision, plan.Value, decision.Message, decision.Suggestion),
            decision.Message);
    }

    /// <summary>执行连接(安装驱动 + 创建队列),可选打印测试页。</summary>
    public OperationResult<InstallReport> Connect(UsbConnectPlan plan, bool printTestPage, IProgress<InstallStepResult>? progress = null)
    {
        if (plan.Plan is null)
        {
            return OperationResult<InstallReport>.Fail(
                string.IsNullOrWhiteSpace(plan.Summary) ? "该设备当前不需要安装驱动。" : plan.Summary,
                plan.Suggestion);
        }

        var install = _installer.Install(plan.Plan, progress);
        var report = install.Value;

        if (install.IsSuccess && printTestPage && report is not null)
        {
            var test = _installer.PrintSystemTestPage(report.QueueName);
            var steps = report.Steps.ToList();
            steps.Add(new InstallStepResult(
                InstallStep.PrintTestPage,
                test.IsSuccess ? InstallStepState.Succeeded : InstallStepState.Failed,
                test.Message,
                $"rundll32 printui.dll,PrintUIEntry /k /n \"{report.QueueName}\"",
                test.IsSuccess ? 0 : -1,
                test.Suggestion ?? string.Empty,
                TimeSpan.Zero));

            report = report with
            {
                Steps = steps,
                Success = report.Success && test.IsSuccess,
                Summary = report.Summary + " " + test.Message,
            };
        }

        return install with { Value = report };
    }

    private static string BuildQueueName(UsbPrinterInfo device)
    {
        var model = DeviceNameParser.CleanModelName(device.Device.Model);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = device.Device.DisplayName;
        }

        return string.IsNullOrWhiteSpace(device.PortName) ? model : $"{model} ({device.PortName})";
    }
}
