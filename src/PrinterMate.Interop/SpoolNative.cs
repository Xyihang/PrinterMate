using System.Runtime.InteropServices;

namespace PrinterMate.Interop;

/// <summary>winspool.drv 原始声明。所有字符串使用 Unicode 变体。</summary>
internal static class SpoolNative
{
    internal const string Spool = "winspool.drv";

    internal const int PRINTER_ENUM_LOCAL = 0x00000002;
    internal const int PRINTER_ENUM_CONNECTIONS = 0x00000004;
    internal const int PRINTER_ENUM_NAME = 0x00000008;
    internal const int PRINTER_ENUM_REMOTE = 0x00000010;

    internal const int PRINTER_STATUS_PAUSED = 0x00000001;
    internal const int PRINTER_STATUS_ERROR = 0x00000002;
    internal const int PRINTER_STATUS_PENDING_DELETION = 0x00000004;
    internal const int PRINTER_STATUS_PAPER_JAM = 0x00000008;
    internal const int PRINTER_STATUS_PAPER_OUT = 0x00000010;
    internal const int PRINTER_STATUS_MANUAL_FEED = 0x00000020;
    internal const int PRINTER_STATUS_PAPER_PROBLEM = 0x00000040;
    internal const int PRINTER_STATUS_OFFLINE = 0x00000080;
    internal const int PRINTER_STATUS_IO_ACTIVE = 0x00000100;
    internal const int PRINTER_STATUS_BUSY = 0x00000200;
    internal const int PRINTER_STATUS_PRINTING = 0x00000400;
    internal const int PRINTER_STATUS_OUTPUT_BIN_FULL = 0x00000800;
    internal const int PRINTER_STATUS_NOT_AVAILABLE = 0x00001000;
    internal const int PRINTER_STATUS_WAITING = 0x00002000;
    internal const int PRINTER_STATUS_PROCESSING = 0x00004000;
    internal const int PRINTER_STATUS_INITIALIZING = 0x00008000;
    internal const int PRINTER_STATUS_WARMING_UP = 0x00010000;
    internal const int PRINTER_STATUS_TONER_LOW = 0x00020000;
    internal const int PRINTER_STATUS_NO_TONER = 0x00040000;
    internal const int PRINTER_STATUS_USER_INTERVENTION = 0x00100000;
    internal const int PRINTER_STATUS_OUT_OF_MEMORY = 0x00200000;
    internal const int PRINTER_STATUS_DOOR_OPEN = 0x00400000;
    internal const int PRINTER_STATUS_SERVER_UNKNOWN = 0x00800000;
    internal const int PRINTER_STATUS_POWER_SAVE = 0x01000000;

    internal const int JOB_STATUS_PAUSED = 0x00000001;
    internal const int JOB_STATUS_ERROR = 0x00000002;
    internal const int JOB_STATUS_DELETING = 0x00000004;
    internal const int JOB_STATUS_SPOOLING = 0x00000008;
    internal const int JOB_STATUS_PRINTING = 0x00000010;
    internal const int JOB_STATUS_OFFLINE = 0x00000020;
    internal const int JOB_STATUS_PAPEROUT = 0x00000040;
    internal const int JOB_STATUS_PRINTED = 0x00000080;
    internal const int JOB_STATUS_DELETED = 0x00000100;
    internal const int JOB_STATUS_BLOCKED_DEVQ = 0x00000200;
    internal const int JOB_STATUS_USER_INTERVENTION = 0x00000400;
    internal const int JOB_STATUS_RESTART = 0x00000800;

    internal const int JOB_CONTROL_PAUSE = 1;
    internal const int JOB_CONTROL_RESUME = 2;
    internal const int JOB_CONTROL_CANCEL = 3;
    internal const int JOB_CONTROL_RESTART = 4;

    internal const int PRINTER_CONTROL_PAUSE = 1;
    internal const int PRINTER_CONTROL_RESUME = 2;
    internal const int PRINTER_CONTROL_PURGE = 3;

    internal const int PRINTER_ACCESS_USE = 0x00000008;
    internal const int PRINTER_ACCESS_ADMINISTER = 0x00000004;
    internal const int PRINTER_ALL_ACCESS = 0x000F000C;
    internal const int PRINTER_DEFAULT_ACCESS = 0x00040000;

    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public int DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_INFO_1
    {
        public int Flags;
        public IntPtr pDescription;
        public IntPtr pName;
        public IntPtr pComment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_INFO_2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public int Attributes;
        public int Priority;
        public int DefaultPriority;
        public int StartTime;
        public int UntilTime;
        public int Status;
        public int cJobs;
        public int AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_INFO_4
    {
        public IntPtr pPrinterName;
        public IntPtr pServerName;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct JOB_INFO_1
    {
        public int JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public int Status;
        public int Priority;
        public int Position;
        public int TotalPages;
        public int PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;

        internal readonly DateTime? ToLocal()
        {
            if (Year == 0) return null;
            try
            {
                return new DateTime(Year, Month, Math.Max((ushort)1, Day), Hour, Minute, Second, DateTimeKind.Utc).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
    }

    [DllImport(Spool, EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool OpenPrinter(string pPrinterName, out SafePrinterHandle phPrinter, ref PRINTER_DEFAULTS pDefault);

    [DllImport(Spool, EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool OpenPrinter(string pPrinterName, out SafePrinterHandle phPrinter, IntPtr pDefault);

    [DllImport(Spool, SetLastError = true)]
    internal static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(Spool, EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool EnumPrinters(int flags, string? name, int level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Spool, EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetPrinter(SafePrinterHandle hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport(Spool, EntryPoint = "SetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetPrinter(SafePrinterHandle hPrinter, int level, IntPtr pPrinter, int command);

    [DllImport(Spool, EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool EnumJobs(SafePrinterHandle hPrinter, int firstJob, int noJobs, int level, IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Spool, EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetJob(SafePrinterHandle hPrinter, int jobId, int level, IntPtr pJob, int cbBuf, out int pcbNeeded);

    [DllImport(Spool, EntryPoint = "SetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetJob(SafePrinterHandle hPrinter, int jobId, int level, IntPtr pJob, int command);

    [DllImport(Spool, EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int StartDocPrinter(SafePrinterHandle hPrinter, int level, ref DOC_INFO_1 di);

    [DllImport(Spool, SetLastError = true)]
    internal static extern bool EndDocPrinter(SafePrinterHandle hPrinter);

    [DllImport(Spool, SetLastError = true)]
    internal static extern bool StartPagePrinter(SafePrinterHandle hPrinter);

    [DllImport(Spool, SetLastError = true)]
    internal static extern bool EndPagePrinter(SafePrinterHandle hPrinter);

    [DllImport(Spool, SetLastError = true)]
    internal static extern bool WritePrinter(SafePrinterHandle hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport(Spool, EntryPoint = "GetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetDefaultPrinter(System.Text.StringBuilder? pszBuffer, ref int pcchBuffer);

    [DllImport(Spool, EntryPoint = "SetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetDefaultPrinter(string pszPrinter);
}
