using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Infrastructure;

namespace PrinterMate.App.ViewModels;

/// <summary>
/// 新手教程页。内容以静态说明为主(写在 XAML 里便于直接改文案),
/// 这里只提供几条「本机实时状态」,让教程里的话不是空话。
/// </summary>
public sealed class TutorialViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;

    private string _libraryRoot = "-";
    private string _librarySummary = "正在读取驱动库状态 …";
    private string _policySummary = "-";
    private string _environmentText = "-";

    public TutorialViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    /// <summary>教程文案里说的「本机驱动库」到底在哪。</summary>
    public string LibraryRoot
    {
        get => _libraryRoot;
        private set => SetProperty(ref _libraryRoot, value);
    }

    /// <summary>驱动库当前有多少条可以直接用。</summary>
    public string LibrarySummary
    {
        get => _librarySummary;
        private set => SetProperty(ref _librarySummary, value);
    }

    /// <summary>本机内置驱动许可开关的当前状态。</summary>
    public string PolicySummary
    {
        get => _policySummary;
        private set => SetProperty(ref _policySummary, value);
    }

    /// <summary>架构 / 系统 / PowerShell / 提权辅助进程是否就位。</summary>
    public string EnvironmentText
    {
        get => _environmentText;
        private set => SetProperty(ref _environmentText, value);
    }

    public void OnActivated() => Refresh();

    public void OnDeactivated()
    {
    }

    private void Refresh()
    {
        var library = _services.Library;
        if (library is null)
        {
            LibraryRoot = "(驱动库不可用)";
            LibrarySummary = "内置驱动库未能加载,请到「设置」页查看启动错误。";
        }
        else
        {
            var status = library.GetStatus(DriverMatchEngine.CurrentArchitecture);
            LibraryRoot = status.Root;
            LibrarySummary =
                $"共 {status.TotalEntries} 条:系统免驱 {status.InboxEntries} 条,"
                + $"本地驱动包 {status.PackagedEntries} 条,当前可直接使用 {status.ReadyEntries} 条。";
            PolicySummary = library.Policy.Describe();
        }

        EnvironmentText =
            $"{DriverMatchEngine.CurrentArchitecture} / {DriverMatchEngine.CurrentOsTag} · "
            + $"PowerShell {(PowerShellRunner.IsAvailable ? "可用" : "不可用")} · "
            + $"提权辅助进程 {(Core.Elevation.ElevatedLauncher.IsHelperAvailable ? "已就位" : "缺失")}";

        OnPropertyChanged(nameof(LibraryRoot));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(PolicySummary));
        OnPropertyChanged(nameof(EnvironmentText));
    }
}
