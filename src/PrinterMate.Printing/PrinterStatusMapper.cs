namespace PrinterMate.Printing;

/// <summary>把 winspool 的状态位与作业状态位翻译成统一健康状态,并给出中文说明与处置建议。</summary>
public static class PrinterStatusMapper
{
    // winspool PRINTER_STATUS_* 状态位。
    private const int PrinterPaused = 0x00000001;
    private const int PrinterError = 0x00000002;
    private const int PrinterPendingDeletion = 0x00000004;
    private const int PrinterPaperJam = 0x00000008;
    private const int PrinterPaperOut = 0x00000010;
    private const int PrinterManualFeed = 0x00000020;
    private const int PrinterPaperProblem = 0x00000040;
    private const int PrinterOffline = 0x00000080;
    private const int PrinterIoActive = 0x00000100;
    private const int PrinterBusy = 0x00000200;
    private const int PrinterPrinting = 0x00000400;
    private const int PrinterOutputBinFull = 0x00000800;
    private const int PrinterNotAvailable = 0x00001000;
    private const int PrinterWaiting = 0x00002000;
    private const int PrinterProcessing = 0x00004000;
    private const int PrinterInitializing = 0x00008000;
    private const int PrinterWarmingUp = 0x00010000;
    private const int PrinterTonerLow = 0x00020000;
    private const int PrinterNoToner = 0x00040000;
    private const int PrinterPagePunt = 0x00080000;
    private const int PrinterUserIntervention = 0x00100000;
    private const int PrinterOutOfMemory = 0x00200000;
    private const int PrinterDoorOpen = 0x00400000;
    private const int PrinterServerUnknown = 0x00800000;
    private const int PrinterPowerSave = 0x01000000;
    private const int PrinterServerOffline = 0x02000000;
    private const int PrinterDriverUpdateNeeded = 0x04000000;

    // winspool JOB_STATUS_* 状态位。
    private const int JobPaused = 0x00000001;
    private const int JobError = 0x00000002;
    private const int JobDeleting = 0x00000004;
    private const int JobSpooling = 0x00000008;
    private const int JobPrinting = 0x00000010;
    private const int JobOffline = 0x00000020;
    private const int JobPaperOut = 0x00000040;
    private const int JobPrinted = 0x00000080;
    private const int JobDeleted = 0x00000100;
    private const int JobBlockedDeviceQueue = 0x00000200;
    private const int JobUserIntervention = 0x00000400;
    private const int JobRestart = 0x00000800;
    private const int JobComplete = 0x00001000;
    private const int JobRetained = 0x00002000;
    private const int JobRenderingLocally = 0x00004000;

    private const int PrinterKnownMask =
        PrinterPaused | PrinterError | PrinterPendingDeletion | PrinterPaperJam | PrinterPaperOut |
        PrinterManualFeed | PrinterPaperProblem | PrinterOffline | PrinterIoActive | PrinterBusy |
        PrinterPrinting | PrinterOutputBinFull | PrinterNotAvailable | PrinterWaiting | PrinterProcessing |
        PrinterInitializing | PrinterWarmingUp | PrinterTonerLow | PrinterNoToner | PrinterPagePunt |
        PrinterUserIntervention | PrinterOutOfMemory | PrinterDoorOpen | PrinterServerUnknown |
        PrinterPowerSave | PrinterServerOffline | PrinterDriverUpdateNeeded;

    private const int PrinterFaultMask =
        PrinterError | PrinterPaperProblem | PrinterOutputBinFull | PrinterOutOfMemory |
        PrinterUserIntervention | PrinterPagePunt | PrinterDriverUpdateNeeded;

    private const int PrinterActiveMask = PrinterProcessing | PrinterIoActive;

    private const int PrinterBusyMask =
        PrinterBusy | PrinterWaiting | PrinterInitializing | PrinterWarmingUp | PrinterManualFeed;

    /// <summary>按严重程度优先归并多状态位,缺纸/卡纸等需要人工介入的状态优先暴露给用户。</summary>
    public static PrinterHealth MapPrinterHealth(int printerStatus, int jobCount)
    {
        if ((printerStatus & PrinterNoToner) != 0)
        {
            return PrinterHealth.NoToner;
        }

        if ((printerStatus & PrinterTonerLow) != 0)
        {
            return PrinterHealth.TonerLow;
        }

        if ((printerStatus & PrinterPaperJam) != 0)
        {
            return PrinterHealth.PaperJam;
        }

        if ((printerStatus & PrinterPaperOut) != 0)
        {
            return PrinterHealth.PaperOut;
        }

        if ((printerStatus & PrinterDoorOpen) != 0)
        {
            return PrinterHealth.DoorOpen;
        }

        if ((printerStatus & (PrinterOffline | PrinterServerOffline | PrinterServerUnknown)) != 0)
        {
            return PrinterHealth.Offline;
        }

        if ((printerStatus & PrinterNotAvailable) != 0)
        {
            return PrinterHealth.NotAvailable;
        }

        if ((printerStatus & PrinterFaultMask) != 0)
        {
            return PrinterHealth.Error;
        }

        if ((printerStatus & PrinterPaused) != 0)
        {
            return PrinterHealth.Paused;
        }

        if ((printerStatus & (PrinterPrinting | PrinterActiveMask)) != 0)
        {
            return PrinterHealth.Printing;
        }

        if ((printerStatus & (PrinterBusyMask | PrinterPendingDeletion)) != 0)
        {
            return PrinterHealth.Busy;
        }

        if (printerStatus == 0)
        {
            return jobCount > 0 ? PrinterHealth.Printing : PrinterHealth.Online;
        }

        if ((printerStatus & ~PrinterKnownMask) != 0)
        {
            return PrinterHealth.Unknown;
        }

        return printerStatus == PrinterPowerSave ? PrinterHealth.Online : PrinterHealth.Unknown;
    }

    /// <summary>把单条作业的状态位翻译成健康状态。</summary>
    public static PrinterHealth MapJobHealth(int jobStatus)
    {
        if ((jobStatus & JobPaused) != 0)
        {
            return PrinterHealth.Paused;
        }

        if ((jobStatus & (JobError | JobBlockedDeviceQueue | JobUserIntervention)) != 0)
        {
            return PrinterHealth.Error;
        }

        if ((jobStatus & JobPaperOut) != 0)
        {
            return PrinterHealth.PaperOut;
        }

        if ((jobStatus & JobOffline) != 0)
        {
            return PrinterHealth.Offline;
        }

        if ((jobStatus & JobPrinting) != 0)
        {
            return PrinterHealth.Printing;
        }

        if ((jobStatus & (JobSpooling | JobDeleting | JobRestart | JobRenderingLocally)) != 0)
        {
            return PrinterHealth.Busy;
        }

        if ((jobStatus & (JobPrinted | JobComplete)) != 0)
        {
            return PrinterHealth.Online;
        }

        return PrinterHealth.Unknown;
    }

    /// <summary>简短的中文状态短语,用于列表/角标。</summary>
    public static string Describe(PrinterHealth health) => health switch
    {
        PrinterHealth.Online => "就绪",
        PrinterHealth.Busy => "处理中",
        PrinterHealth.Printing => "打印中",
        PrinterHealth.Paused => "已暂停",
        PrinterHealth.Offline => "脱机",
        PrinterHealth.PaperOut => "缺纸",
        PrinterHealth.PaperJam => "卡纸",
        PrinterHealth.TonerLow => "墨粉不足",
        PrinterHealth.NoToner => "墨粉耗尽",
        PrinterHealth.DoorOpen => "舱门未关",
        PrinterHealth.Error => "故障",
        PrinterHealth.NotAvailable => "不可用",
        _ => "状态未知",
    };

    /// <summary>可操作的中文处置建议。</summary>
    public static string Suggest(PrinterHealth health) => health switch
    {
        PrinterHealth.Online => "设备就绪,可以直接下发打印任务。",
        PrinterHealth.Busy => "设备正在处理上一批任务,请等待队列清空后重试。",
        PrinterHealth.Printing => "设备正在打印,请勿断电或强行关机。",
        PrinterHealth.Paused => "请在队列中点「恢复打印」;若仍无效,检查是否勾选了「暂停打印」。",
        PrinterHealth.Offline => "请检查电源、USB/网线连接,或在「设备和打印机」中取消「脱机使用打印机」。",
        PrinterHealth.PaperOut => "请装入纸张后点击恢复;同时确认纸盒导轨与纸张规格(A4)一致。",
        PrinterHealth.PaperJam => "请打开舱门取出卡住的纸张,确认无碎纸残留后点击恢复。",
        PrinterHealth.TonerLow => "墨粉即将耗尽,请提前准备备用硒鼓/墨盒,以免打印中途中断。",
        PrinterHealth.NoToner => "请更换硒鼓/墨盒,更换后在设备面板校准并点击恢复。",
        PrinterHealth.DoorOpen => "请关紧前后舱门与出纸口挡板,确认卡扣到位后点击恢复。",
        PrinterHealth.Error => "请查看设备面板错误码,重启打印机电源;若持续报错请联系厂商报修。",
        PrinterHealth.NotAvailable => "打印机当前不可用,请确认已开机并连接到本机,然后重试。",
        _ => "无法识别设备状态,请重启打印机与后台打印服务(Print Spooler)后重试。",
    };
}
