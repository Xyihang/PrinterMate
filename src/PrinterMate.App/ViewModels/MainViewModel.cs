using System.Collections.ObjectModel;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;

namespace PrinterMate.App.ViewModels;

/// <summary>左侧导航项。</summary>
public sealed class NavItem
{
    public NavItem(string title, ObservableObject viewModel)
    {
        Title = title;
        ViewModel = viewModel;
    }

    public string Title { get; }

    public ObservableObject ViewModel { get; }
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

        DeviceDiscovery = new DeviceDiscoveryViewModel(services);
        DriverManagement = new DriverManagementViewModel(services);
        Printing = new PrintingViewModel(services);
        Queues = new QueueViewModel(services);
        Settings = new SettingsViewModel(services);

        Pages = new ObservableCollection<NavItem>
        {
            new("设备发现与连接", DeviceDiscovery),
            new("驱动管理", DriverManagement),
            new("打印与测试页", Printing),
            new("任务队列与状态", Queues),
            new("设置", Settings),
        };

        _selectedPage = Pages[0];
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
