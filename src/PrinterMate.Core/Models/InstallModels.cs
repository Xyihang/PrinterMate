namespace PrinterMate.Core;

/// <summary>安装状态机的步骤。</summary>
public enum InstallStep
{
    Precheck,
    VerifyPackage,
    AddDriverToStore,
    RegisterDriver,
    CreatePort,
    CreateQueue,
    Verify,
    PrintTestPage,
    Done,
}

public enum InstallStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>单个步骤的执行结果(含实际执行的命令与返回码,便于审计与排障)。</summary>
public sealed record InstallStepResult(
    InstallStep Step,
    InstallStepState State,
    string Message,
    string Command,
    int ExitCode,
    string Output,
    TimeSpan Duration)
{
    public string StepText => Step switch
    {
        InstallStep.Precheck => "环境预检",
        InstallStep.VerifyPackage => "驱动包校验",
        InstallStep.AddDriverToStore => "加入驱动仓库",
        InstallStep.RegisterDriver => "注册打印驱动",
        InstallStep.CreatePort => "创建打印端口",
        InstallStep.CreateQueue => "创建打印队列",
        InstallStep.Verify => "回读校验",
        InstallStep.PrintTestPage => "打印测试页",
        _ => "完成",
    };
}

/// <summary>端口类型。</summary>
public enum PortKind
{
    Ipp,
    RawTcp,
    Lpd,
    Usb,
    Shared,
    Existing,
}

/// <summary>端口规格。USB 端口由 PnP 自动创建,此处只做校验标记。</summary>
public sealed record PortSpec(
    PortKind Kind,
    string PortName,
    string? HostAddress = null,
    int? PortNumber = null,
    string? SharePath = null)
{
    public string KindText => Kind switch
    {
        PortKind.Ipp => "IPP 端口",
        PortKind.RawTcp => "RAW(TCP 9100)端口",
        PortKind.Lpd => "LPD 端口",
        PortKind.Usb => "USB(PnP 自动)",
        PortKind.Shared => "共享队列",
        _ => "沿用现有端口",
    };
}

/// <summary>一次安装的完整计划,提前展示给用户二次确认。</summary>
public sealed record InstallPlan(
    string QueueName,
    string DriverName,
    DriverSource Source,
    string? InfPath,
    string? Sha256,
    string? ExpectedSignerSubject,
    PortSpec Port,
    bool RequiresElevation,
    bool RequiresDownload,
    IReadOnlyList<string> Warnings,
    string RiskDisclosure);

/// <summary>一次安装的完整报告。</summary>
public sealed record InstallReport(
    string QueueName,
    string DriverName,
    DriverSource Source,
    bool Success,
    IReadOnlyList<InstallStepResult> Steps,
    TimeSpan TotalDuration,
    string Summary)
{
    public static InstallReport Empty(string reason) =>
        new("-", "-", DriverSource.None, false, Array.Empty<InstallStepResult>(), TimeSpan.Zero, reason);
}

/// <summary>已安装的打印驱动信息(来自 Get-PrinterDriver)。</summary>
public sealed record InstalledDriverInfo(string Name, string InfPath, string Manufacturer, string DriverVersion, string PrinterEnvironment);
