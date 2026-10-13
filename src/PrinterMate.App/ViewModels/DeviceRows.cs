using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;

namespace PrinterMate.App.ViewModels;

/// <summary>USB 设备列表中的一行。</summary>
public sealed class UsbDeviceRow : ObservableObject
{
    private DriverDecision? _decision;

    public UsbDeviceRow(UsbPrinterInfo info, DriverDecision? decision)
    {
        Info = info;
        _decision = decision;
    }

    public UsbPrinterInfo Info { get; }

    public AsyncRelayCommand? ConnectCommand { get; set; }

    public string DeviceName => Info.Device.DisplayName;

    public string Model => string.IsNullOrWhiteSpace(Info.Device.Model) ? "-" : Info.Device.Model;

    public string PortName => string.IsNullOrWhiteSpace(Info.PortName) ? "(未分配)" : Info.PortName;

    public string Vendor => string.IsNullOrWhiteSpace(Info.Device.Vendor) ? "-" : Info.Device.Vendor;

    public string ActionText => Info.ActionText;

    public string StateText => Info.State switch
    {
        UsbBindingState.Ready => "已就绪",
        UsbBindingState.NeedsQueue => "需要创建队列",
        UsbBindingState.NeedsDriverUpgrade => "需更换驱动",
        UsbBindingState.PortMissing => "端口缺失",
        _ => "状态未知",
    };

    public bool CanConnect => Info.State != UsbBindingState.PortMissing;

    public DriverDecision? Decision
    {
        get => _decision;
        private set
        {
            if (SetProperty(ref _decision, value))
            {
                OnPropertyChanged(nameof(LayerText));
                OnPropertyChanged(nameof(DecisionText));
                OnPropertyChanged(nameof(SuggestionText));
                OnPropertyChanged(nameof(CandidateText));
            }
        }
    }

    public string LayerText
    {
        get
        {
            var layer = _decision?.Layer;
            return string.IsNullOrWhiteSpace(layer) || layer == "-" ? "未确定" : layer;
        }
    }

    public string DecisionText => _decision?.Message ?? "尚未生成驱动决策。";

    public string SuggestionText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_decision?.Suggestion))
            {
                return _decision!.Suggestion;
            }

            return Info.ActionText;
        }
    }

    public string CandidateText
    {
        get
        {
            var candidates = _decision?.Candidates;
            if (candidates is null || candidates.Count == 0)
            {
                return "无候选驱动。";
            }

            return string.Join(Environment.NewLine, candidates.Select(InstallHelper.DescribeCandidate));
        }
    }

    public void ApplyDecision(DriverDecision decision) => Decision = decision;
}

/// <summary>局域网设备列表中的一行。</summary>
public sealed class NetworkDeviceRow : ObservableObject
{
    private DriverDecision? _decision;

    public NetworkDeviceRow(DiscoveredPrinter device, DriverDecision? decision)
    {
        Device = device;
        _decision = decision;
    }

    public DiscoveredPrinter Device { get; }

    public AsyncRelayCommand? ConnectCommand { get; set; }

    public string Name => Device.DisplayName;

    public string Model => string.IsNullOrWhiteSpace(Device.Model) ? "-" : Device.Model;

    public string ConnectionText => Device.ConnectionText;

    public string AddressText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Device.Address))
            {
                return Device.Address;
            }

            return string.IsNullOrWhiteSpace(Device.PortName) ? "-" : Device.PortName;
        }
    }

    public string SourceText => string.IsNullOrWhiteSpace(Device.DiscoverySource) ? "-" : Device.DiscoverySource;

    public string ReadinessText => Device.ReadinessText;

    public DriverDecision? Decision
    {
        get => _decision;
        private set
        {
            if (SetProperty(ref _decision, value))
            {
                OnPropertyChanged(nameof(LayerText));
                OnPropertyChanged(nameof(SuggestionText));
                OnPropertyChanged(nameof(CandidateText));
            }
        }
    }

    public string LayerText
    {
        get
        {
            var layer = _decision?.Layer;
            return string.IsNullOrWhiteSpace(layer) || layer == "-" ? "未确定" : layer;
        }
    }

    public string SuggestionText => _decision?.Suggestion ?? "尚未生成驱动决策。";

    public string CandidateText
    {
        get
        {
            var candidates = _decision?.Candidates;
            if (candidates is null || candidates.Count == 0)
            {
                return "无候选驱动。";
            }

            return string.Join(Environment.NewLine, candidates.Select(InstallHelper.DescribeCandidate));
        }
    }

    public void ApplyDecision(DriverDecision decision) => Decision = decision;
}
