namespace PrinterMate.Printing;

/// <summary>打印队列中的一条作业。</summary>
public sealed record QueueJobInfo(
    int JobId,
    string Document,
    string UserName,
    string Datatype,
    string StatusText,
    int Status,
    int TotalPages,
    int PagesPrinted,
    DateTime? Submitted,
    bool IsPaused,
    bool IsPrinting);

/// <summary>本机已安装的打印机及其队列概况。</summary>
public sealed record QueuePrinterInfo(
    string Name,
    string DriverName,
    string PortName,
    bool IsNetwork,
    bool IsShared,
    int JobCount,
    bool IsPaused);

/// <summary>归一化后的打印机/作业健康状态。</summary>
public enum PrinterHealth
{
    Online,
    Busy,
    Printing,
    Paused,
    Offline,
    PaperOut,
    PaperJam,
    TonerLow,
    NoToner,
    DoorOpen,
    Error,
    NotAvailable,
    Unknown,
}
