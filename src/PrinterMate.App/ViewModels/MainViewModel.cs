using System.Collections.ObjectModel;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;

namespace PrinterMate.App.ViewModels;

/// <summary>
/// 左侧导航项。
///
/// 有两种形态:
///   · 分组标题(IsGroupHeader = true):只做视觉分隔,不可被选中;
///   · 页面项:承载实际页面 ViewModel。
/// 两者混在同一个集合里,保证导航只有【一个选择作用域】,
/// 避免用两个 ListBox 分组时出现"互相清除选中"的绑定回环。
/// </summary>
public sealed class NavItem
{
    private NavItem(string title, ObservableObject? viewModel, string iconKey, bool isGroupHeader)
    {
        Title = title;
        ViewModel = viewModel;
        IconKey = iconKey;
        IsGroupHeader = isGroupHeader;
    }

    /// <summary>创建一个页面导航项。</summary>
    public static NavItem Page(string title, ObservableObject viewModel, string iconKey)
        => new(title, viewModel, iconKey, isGroupHeader: false);

    /// <summary>创建一个分组标题项(不可选中)。</summary>
    public static NavItem Group(string title)
        => new(title, null, string.Empty, isGroupHeader: true);

    public string Title { get; }

    public ObservableObject? ViewModel { get; }

    /// <summary>图标键,由导航项模板通过 DataTrigger 映射到 Resources/Icons.xaml 里的矢量。</summary>
    public string IconKey { get; }

    public bool IsGroupHeader { get; }
}

/// <summary>主窗口视图模型:导航、顶部状态条、全局异常落点。</summary>
public sealed class MainViewModel : ObservableObject
{
    private NavItem _selectedPage;
    private string _operationText = "就绪";
    private string _resultText = "PrinterMate 已启动,请先扫描 USB 打印机。";
    private string _suggestionText = string.Empty;
    private StatusLevel _level = StatusLevel.Info;

    public MainViewModel()
        : this(AppServices.Instance)
    {
    }

    public MainViewModel(AppServices services)
    {
        Services = services;

        Tutorial = new TutorialViewModel(services);
        DeviceDiscovery = new DeviceDiscoveryViewModel(services);
        DriverManagement = new DriverManagementViewModel(services);
        Printing = new PrintingViewModel(services);
        Queues = new QueueViewModel(services);
        Settings = new SettingsViewModel(services);

        Pages = new ObservableCollection<NavItem>
        {
            NavItem.Group("核心流程"),
            NavItem.Page("新手必读", Tutorial, "Book"),
            NavItem.Page("设备发现与连接", DeviceDiscovery, "Usb"),
            NavItem.Page("打印与测试页", Printing, "Print"),
            NavItem.Page("任务队列与状态", Queues, "Queue"),
            NavItem.Group("配置与帮助"),
            NavItem.Page("驱动管理", DriverManagement, "Driver"),
            NavItem.Page("设置", Settings, "Settings"),
        };

        // 关键:初始选中项必须是【页面项】而不是分组标题。
        // 分组标题不可选中,若取 Pages[0] 会得到一个无法显示页面的选中态。
        _selectedPage = Pages.First(p => !p.IsGroupHeader);
        StatusHub.Updated += OnStatusUpdated;

        var last = StatusHub.Last;
        if (last is not null)
        {
            Apply(last);
        }

        if (services.StartupErrors.Count > 0)
        {
            StatusHub.Error("启动检查", string.Join(" ", services.StartupErrors));
        }
    }

    public AppServices Services { get; }

    /// <summary>新手教程页(导航第一项)。</summary>
    public TutorialViewModel Tutorial { get; }

    public DeviceDiscoveryViewModel DeviceDiscovery { get; }

    public DriverManagementViewModel DriverManagement { get; }

    public PrintingViewModel Printing { get; }

    public QueueViewModel Queues { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> Pages { get; }

    public NavItem SelectedPage
    {
        get => _selectedPage;
        set
        {
            // 安全网:分组标题不可被选中。
            // 即使某个输入设备仍让它变成选中项,这里直接忽略,避免页面区变空白。
            if (value is { IsGroupHeader: true })
            {
                return;
            }

            var previous = _selectedPage;
            if (!SetProperty(ref _selectedPage, value))
            {
                return;
            }

            (previous?.ViewModel as IActivatable)?.OnDeactivated();
            (_selectedPage?.ViewModel as IActivatable)?.OnActivated();
        }
    }

    public string OperationText
    {
        get => _operationText;
        private set => SetProperty(ref _operationText, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public string SuggestionText
    {
        get => _suggestionText;
        private set
        {
            if (SetProperty(ref _suggestionText, value))
            {
                OnPropertyChanged(nameof(HasSuggestion));
            }
        }
    }

    public bool HasSuggestion => !string.IsNullOrWhiteSpace(_suggestionText);

    public StatusLevel Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    private void OnStatusUpdated(StatusUpdate update)
    {
        Ui.Post(() => Apply(update));
    }

    private void Apply(StatusUpdate update)
    {
        OperationText = string.IsNullOrWhiteSpace(update.Operation) ? "操作" : update.Operation;
        ResultText = update.Result;
        SuggestionText = string.IsNullOrWhiteSpace(update.Suggestion) ? string.Empty : "建议:" + update.Suggestion;
        Level = update.Level;
    }
}
