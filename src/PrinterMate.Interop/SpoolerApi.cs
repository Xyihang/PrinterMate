using System.Runtime.InteropServices;
using System.Text;

namespace PrinterMate.Interop;

/// <summary>本机打印队列的一条记录(来自 winspool 的 PRINTER_INFO_2)。</summary>
public sealed record NativePrinterInfo(
    string Name,
    string DriverName,
    string PortName,
    string ServerName,
    string Comment,
    string Location,
    string PrintProcessor,
    string Datatype,
    int Status,
    int Attributes,
    int JobCount)
{
    public bool IsNetwork => !string.IsNullOrEmpty(ServerName) && !ServerName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public bool IsShared => (Attributes & 0x00000008) != 0; // PRINTER_ATTRIBUTE_SHARED
}

/// <summary>打印作业记录(来自 winspool 的 JOB_INFO_1)。</summary>
public sealed record NativePrintJob(
    int JobId,
    string Document,
    string UserName,
    string MachineName,
    string Datatype,
    string StatusText,
    int Status,
    int TotalPages,
    int PagesPrinted,
    DateTime? Submitted);

/// <summary>winspool.drv 的高层封装:枚举、状态、控制、RAW 写入。</summary>
public static class SpoolerApi
{
    private const int PRINTER_ATTRIBUTE_SHARED = 0x00000008;

    public static IReadOnlyList<NativePrinterInfo> EnumeratePrinters(bool includeNetworkConnections = true)
    {
        var flags = SpoolNative.PRINTER_ENUM_LOCAL;
        if (includeNetworkConnections)
        {
            flags |= SpoolNative.PRINTER_ENUM_CONNECTIONS;
        }

        var level = 2;
        SpoolNative.EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0)
        {
            return Array.Empty<NativePrinterInfo>();
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!SpoolNative.EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
            {
                var err = NativeError.LastError();
                if (err != SpoolNative.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw NativeError.Exception("枚举打印机", code: err);
                }
            }

            var result = new List<NativePrinterInfo>(returned);
            var stride = Marshal.SizeOf<SpoolNative.PRINTER_INFO_2>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<SpoolNative.PRINTER_INFO_2>(buffer + (i * stride));
                result.Add(new NativePrinterInfo(
                    PtrToString(info.pPrinterName),
                    PtrToString(info.pDriverName),
                    PtrToString(info.pPortName),
                    PtrToString(info.pServerName),
                    PtrToString(info.pComment),
                    PtrToString(info.pLocation),
                    PtrToString(info.pPrintProcessor),
                    PtrToString(info.pDatatype),
                    info.Status,
                    info.Attributes,
                    info.cJobs));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static NativePrinterInfo? GetPrinter(string printerName)
    {
        using var handle = OpenPrinter(printerName, administrative: false);
        if (handle.IsInvalid)
        {
            return null;
        }

        SpoolNative.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!SpoolNative.GetPrinter(handle, 2, buffer, needed, out _))
            {
                return null;
            }

            var info = Marshal.PtrToStructure<SpoolNative.PRINTER_INFO_2>(buffer);
            return new NativePrinterInfo(
                PtrToString(info.pPrinterName),
                PtrToString(info.pDriverName),
                PtrToString(info.pPortName),
                PtrToString(info.pServerName),
                PtrToString(info.pComment),
                PtrToString(info.pLocation),
                PtrToString(info.pPrintProcessor),
                PtrToString(info.pDatatype),
                info.Status,
                info.Attributes,
                info.cJobs);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static IReadOnlyList<NativePrintJob> EnumerateJobs(string printerName)
    {
        using var handle = OpenPrinter(printerName, administrative: false);
        if (handle.IsInvalid)
        {
            return Array.Empty<NativePrintJob>();
        }

        SpoolNative.EnumJobs(handle, 0, 999, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0)
        {
            return Array.Empty<NativePrintJob>();
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!SpoolNative.EnumJobs(handle, 0, 999, 1, buffer, needed, out _, out var returned))
            {
                var err = NativeError.LastError();
                if (err != SpoolNative.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw NativeError.Exception("枚举打印作业", printerName, err);
                }
            }

            var jobs = new List<NativePrintJob>(returned);
            var stride = Marshal.SizeOf<SpoolNative.JOB_INFO_1>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<SpoolNative.JOB_INFO_1>(buffer + (i * stride));
                jobs.Add(new NativePrintJob(
                    info.JobId,
                    PtrToString(info.pDocument),
                    PtrToString(info.pUserName),
                    PtrToString(info.pMachineName),
                    PtrToString(info.pDatatype),
                    PtrToString(info.pStatus),
                    info.Status,
                    info.TotalPages,
                    info.PagesPrinted,
                    info.Submitted.ToLocal()));
            }

            return jobs;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>暂停(false)/恢复正常(true)整台打印机的队列。</summary>
    public static void SetQueuePaused(string printerName, bool paused)
    {
        using var handle = OpenPrinter(printerName, administrative: true);
        var command = paused ? SpoolNative.PRINTER_CONTROL_PAUSE : SpoolNative.PRINTER_CONTROL_RESUME;
        if (!SpoolNative.SetPrinter(handle, 0, IntPtr.Zero, command))
        {
            throw NativeError.Exception(paused ? "暂停打印机" : "恢复打印机", printerName);
        }
    }

    /// <summary>清空队列,删除该打印机上所有未完成的作业。</summary>
    public static void PurgeQueue(string printerName)
    {
        using var handle = OpenPrinter(printerName, administrative: true);
        if (!SpoolNative.SetPrinter(handle, 0, IntPtr.Zero, SpoolNative.PRINTER_CONTROL_PURGE))
        {
            throw NativeError.Exception("清空打印队列", printerName);
        }
    }

    public enum JobCommand
    {
        Pause = SpoolNative.JOB_CONTROL_PAUSE,
        Resume = SpoolNative.JOB_CONTROL_RESUME,
        Cancel = SpoolNative.JOB_CONTROL_CANCEL,
        Restart = SpoolNative.JOB_CONTROL_RESTART,
    }

    public static void ControlJob(string printerName, int jobId, JobCommand command)
    {
        using var handle = OpenPrinter(printerName, administrative: true);
        if (!SpoolNative.SetJob(handle, jobId, 0, IntPtr.Zero, (int)command))
        {
            throw NativeError.Exception($"对作业 {jobId} 执行 {Describe(command)}", printerName);
        }
    }

    private static string Describe(JobCommand c) => c switch
    {
        JobCommand.Pause => "暂停",
        JobCommand.Resume => "恢复",
        JobCommand.Cancel => "取消",
        JobCommand.Restart => "重新开始",
        _ => c.ToString(),
    };

    /// <summary>把 RAW 数据直接投递到队列(datatype=RAW,由打印机/驱动自行解释)。</summary>
    public static void WriteRaw(string printerName, ReadOnlySpan<byte> data, string documentName)
    {
        using var handle = OpenPrinter(printerName, administrative: false);
        var docInfo = new SpoolNative.DOC_INFO_1
        {
            pDocName = documentName,
            pOutputFile = null,
            pDatatype = "RAW",
        };

        if (SpoolNative.StartDocPrinter(handle, 1, ref docInfo) == 0)
        {
            throw NativeError.Exception("开始打印文档", printerName);
        }

        var pageStarted = false;
        try
        {
            if (!SpoolNative.StartPagePrinter(handle))
            {
                throw NativeError.Exception("开始打印页面", printerName);
            }

            pageStarted = true;

            var pinned = GCHandle.Alloc(data.ToArray(), GCHandleType.Pinned);
            try
            {
                if (!SpoolNative.WritePrinter(handle, pinned.AddrOfPinnedObject(), data.Length, out var written) || written != data.Length)
                {
                    throw NativeError.Exception("写入打印数据", printerName);
                }
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            if (pageStarted)
            {
                SpoolNative.EndPagePrinter(handle);
            }

            SpoolNative.EndDocPrinter(handle);
        }
    }

    public static string? GetDefaultPrinter()
    {
        var size = 0;
        SpoolNative.GetDefaultPrinter(null, ref size);
        if (size <= 1)
        {
            return null;
        }

        var sb = new StringBuilder(size);
        return SpoolNative.GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    private static SafePrinterHandle OpenPrinter(string printerName, bool administrative)
    {
        var access = administrative
            ? SpoolNative.PRINTER_ALL_ACCESS
            : SpoolNative.PRINTER_ACCESS_USE;

        var defaults = new SpoolNative.PRINTER_DEFAULTS
        {
            pDatatype = IntPtr.Zero,
            pDevMode = IntPtr.Zero,
            DesiredAccess = access,
        };

        if (SpoolNative.OpenPrinter(printerName, out var handle, ref defaults) || SpoolNative.OpenPrinter(printerName, out handle, IntPtr.Zero))
        {
            return handle;
        }

        return handle;
    }

    private static string PtrToString(IntPtr p) => p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(p) ?? string.Empty;
}
