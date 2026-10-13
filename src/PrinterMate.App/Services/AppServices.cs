using PrinterMate.Catalog;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Printing;
using PrinterMate.Security;

namespace PrinterMate.App.Services;

/// <summary>
/// 组合根:一次性构建 Core 层服务。任何单个服务构造失败都不允许让程序无法启动,
/// 失败信息会记录在 <see cref="StartupErrors"/> 中并在界面上展示。
/// </summary>
public sealed class AppServices
{
    private static readonly Lazy<AppServices> LazyInstance = new(() => new AppServices(new DialogService()), isThreadSafe: true);

    public static AppServices Instance => LazyInstance.Value;

    public AppServices(IDialogService dialogs)
    {
        Dialogs = dialogs;

        var errors = new List<string>();

        try
        {
            Audit = new AuditLog();
        }
        catch (Exception ex)
        {
            errors.Add("审计日志初始化失败:" + ex.Message);
            AppLog.Write("startup", ex.ToString());
        }

        try
        {
            Library = DriverLibrary.Load();
        }
        catch (Exception ex)
        {
            errors.Add("内置驱动库加载失败:" + ex.Message);
            AppLog.Write("startup", ex.ToString());
        }

        if (Library is not null)
        {
            try
            {
                MatchEngine = new DriverMatchEngine(Library, Wpp);
            }
            catch (Exception ex)
            {
                errors.Add("驱动匹配引擎初始化失败:" + ex.Message);
                AppLog.Write("startup", ex.ToString());
            }
        }

        if (Library is not null)
        {
            try
            {
                Installer = new PrinterInstaller(Library, Audit, Wpp);
            }
            catch (Exception ex)
            {
                errors.Add("安装编排器初始化失败:" + ex.Message);
                AppLog.Write("startup", ex.ToString());
            }
        }

        if (MatchEngine is not null && Installer is not null)
        {
            try
            {
                UsbConnect = new UsbConnectService(MatchEngine, Installer, Enumerator);
            }
            catch (Exception ex)
            {
                errors.Add("USB 连接服务初始化失败:" + ex.Message);
                AppLog.Write("startup", ex.ToString());
            }
        }

        StartupErrors = errors;
        foreach (var error in errors)
        {
            AppLog.Write("startup", error);
        }
    }

    public IDialogService Dialogs { get; }

    public DriverLibrary? Library { get; }

    public DriverMatchEngine? MatchEngine { get; }

    public PrinterInstaller? Installer { get; }

    public UsbConnectService? UsbConnect { get; }

    public LocalPrinterEnumerator Enumerator { get; } = new();

    public NetworkDiscoveryService Network { get; } = new();

    public PrintQueueService Queue { get; } = new();

    public WppModeDetector Wpp { get; } = new();

    public AuditLog? Audit { get; }

    /// <summary>启动期收集到的错误(界面必须显眼展示)。</summary>
    public IReadOnlyList<string> StartupErrors { get; }

    /// <summary>用户在「驱动管理」页人工选定的驱动;非空时优先用于连接。</summary>
    public DriverCandidate? ManualCandidate
    {
        get => _manualCandidate;
        set
        {
            _manualCandidate = value;
            ManualCandidateChanged?.Invoke();
        }
    }

    /// <summary>人工驱动变化通知(设备页据此刷新提示)。</summary>
    public event Action? ManualCandidateChanged;

    public bool IsReady => Library is not null && MatchEngine is not null && Installer is not null && UsbConnect is not null;

    private DriverCandidate? _manualCandidate;
}
