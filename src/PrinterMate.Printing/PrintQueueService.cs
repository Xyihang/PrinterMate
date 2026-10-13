using PrinterMate.Interop;

namespace PrinterMate.Printing;

/// <summary>本机打印队列的只读查询与作业控制。</summary>
public sealed class PrintQueueService
{
    private const int PrinterStatusPaused = 0x00000001;
    private const int JobStatusPaused = 0x00000001;
    private const int JobStatusPrinting = 0x00000010;

    /// <summary>枚举本机已安装的打印机(含已建立的网络连接),并附带队列概况。</summary>
    public IReadOnlyList<QueuePrinterInfo> GetLocalPrinters()
    {
        var printers = SpoolerApi.EnumeratePrinters(includeNetworkConnections: true);
        var result = new List<QueuePrinterInfo>(printers.Count);
        foreach (var printer in printers)
        {
            result.Add(new QueuePrinterInfo(
                printer.Name,
                printer.DriverName,
                printer.PortName,
                printer.IsNetwork,
                printer.IsShared,
                printer.JobCount,
                (printer.Status & PrinterStatusPaused) != 0));
        }

        return result;
    }

    /// <summary>读取指定打印机队列中的全部作业。</summary>
    public IReadOnlyList<QueueJobInfo> GetJobs(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);

        var jobs = SpoolerApi.EnumerateJobs(printerName);
        var result = new List<QueueJobInfo>(jobs.Count);
        foreach (var job in jobs)
        {
            result.Add(new QueueJobInfo(
                job.JobId,
                job.Document,
                job.UserName,
                job.Datatype,
                job.StatusText,
                job.Status,
                job.TotalPages,
                job.PagesPrinted,
                job.Submitted,
                (job.Status & JobStatusPaused) != 0,
                (job.Status & JobStatusPrinting) != 0));
        }

        return result;
    }

    /// <summary>读取打印机的归一化健康状态;打印机不存在时返回 <see cref="PrinterHealth.NotAvailable"/>。</summary>
    public PrinterHealth GetHealth(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);

        var printer = SpoolerApi.GetPrinter(printerName);
        return printer is null
            ? PrinterHealth.NotAvailable
            : PrinterStatusMapper.MapPrinterHealth(printer.Status, printer.JobCount);
    }

    /// <summary>暂停整台打印机队列。</summary>
    public void PauseQueue(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.SetQueuePaused(printerName, paused: true);
    }

    /// <summary>恢复整台打印机队列。</summary>
    public void ResumeQueue(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.SetQueuePaused(printerName, paused: false);
    }

    /// <summary>清空该打印机上所有未完成的作业。</summary>
    public void PurgeQueue(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.PurgeQueue(printerName);
    }

    /// <summary>暂停单条作业。</summary>
    public void PauseJob(string printerName, int jobId)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.ControlJob(printerName, jobId, SpoolerApi.JobCommand.Pause);
    }

    /// <summary>恢复单条被暂停的作业。</summary>
    public void ResumeJob(string printerName, int jobId)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.ControlJob(printerName, jobId, SpoolerApi.JobCommand.Resume);
    }

    /// <summary>取消单条作业。</summary>
    public void CancelJob(string printerName, int jobId)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.ControlJob(printerName, jobId, SpoolerApi.JobCommand.Cancel);
    }

    /// <summary>从头重新打印单条作业。</summary>
    public void RestartJob(string printerName, int jobId)
    {
        PrintingErrors.RequirePrinterName(printerName);
        SpoolerApi.ControlJob(printerName, jobId, SpoolerApi.JobCommand.Restart);
    }
}
