using System.Collections.ObjectModel;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using PrinterMate.Core.Drivers;

namespace PrinterMate.App.ViewModels;

/// <summary>设备发现与连接页(USB 优先)。</summary>
public sealed class DeviceDiscoveryViewModel : ObservableObject
{
    private readonly AppServices _services;

    private bool _isBusy;
    private string _usbSummary = "尚未扫描 USB 打印机。";
    private string _networkSummary = "尚未扫描局域网。";
    private string _manualAddress = string.Empty;
    private string _selectedProtocol = "IPP";

    public DeviceDiscoveryViewModel(AppServices services)
    {
        _services = services;
        _services.ManualCandidateChanged += RefreshManualDriver;

        ScanUsbCommand = new AsyncRelayCommand(() => ScanUsbAsync(), () => !IsBusy);
        ScanNetworkCommand = new AsyncRelayCommand(() => ScanNetworkAsync(), () => !IsBusy);
        AddManualCommand = new AsyncRelayCommand(() => AddManualAsync(), () => !IsBusy);
        ClearManualDriverCommand = new RelayCommand(ClearManualDriver, () => _services.ManualCandidate is not null);
    }

    public ObservableCollection<UsbDeviceRow> UsbDevices { get; } = new();

    public ObservableCollection<NetworkDeviceRow> NetworkDevices { get; } = new();

    public ObservableCollection<StepRow> Steps { get; } = new();

    public IReadOnlyList<string> Protocols { get; } = new[] { "IPP", "RAW", "LPD", "共享" };

    public AsyncRelayCommand ScanUsbCommand { get; }

    public AsyncRelayCommand ScanNetworkCommand { get; }

    public AsyncRelayCommand AddManualCommand { get; }

    public RelayCommand ClearManualDriverCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanUsbCommand.RaiseCanExecuteChanged();
                ScanNetworkCommand.RaiseCanExecuteChanged();
                AddManualCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string UsbSummary
    {
        get => _usbSummary;
        private set => SetProperty(ref _usbSummary, value);
    }

    public string NetworkSummary
    {
        get => _networkSummary;
        private set => SetProperty(ref _networkSummary, value);
    }

    public string ManualAddress
    {
        get => _manualAddress;
        set => SetProperty(ref _manualAddress, value);
    }

    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set => SetProperty(ref _selectedProtocol, value);
    }

    public string ManualDriverText => _services.ManualCandidate is null
        ? "当前未指定人工驱动,连接时使用自动匹配结果。"
        : $"已指定人工驱动:{_services.ManualCandidate.DriverName}({_services.ManualCandidate.SourceText},{_services.ManualCandidate.PdlText})";

    public bool HasManualDriver => _services.ManualCandidate is not null;

    public bool HasSteps => Steps.Count > 0;

    public bool HasUsbDevices => UsbDevices.Count > 0;

    public bool HasNetworkDevices => NetworkDevices.Count > 0;

    public void RefreshManualDriver()
    {
        OnPropertyChanged(nameof(ManualDriverText));
        OnPropertyChanged(nameof(HasManualDriver));
        ClearManualDriverCommand.RaiseCanExecuteChanged();
    }

    private void ClearManualDriver()
    {
        _services.ManualCandidate = null;
        RefreshManualDriver();
        StatusHub.Info("人工驱动", "已清除人工指定的驱动,后续连接将使用自动匹配结果。");
    }

    // ────────────────────────────── USB ──────────────────────────────

    private async Task ScanUsbAsync()
    {
        if (_services.UsbConnect is null)
        {
            StatusHub.Error("扫描 USB 打印机", "USB 连接服务不可用。", string.Join(" ", _services.StartupErrors));
            return;
        }

        IsBusy = true;
        StatusHub.Info("扫描 USB 打印机", "正在枚举 USB 打印机并匹配内置驱动 …");
        try
        {
            var listResult = await Task.Run(() => _services.UsbConnect.ListUsbPrinters());
            if (!listResult.IsSuccess || listResult.Value is null)
            {
                UsbSummary = ResultText.Message(listResult);
                ResultText.Publish("扫描 USB 打印机", listResult);
                return;
            }

            var devices = listResult.Value;
            var prepared = await Task.Run(() =>
                devices
                    .Select(device => (Info: device, Plan: _services.UsbConnect!.Prepare(device).Value))
                    .ToList());

            ApplyUsbRows(prepared);
            UsbSummary = listResult.Message;

            if (UsbDevices.Count == 0)
            {
                StatusHub.Warn(
                    "扫描 USB 打印机",
                    string.IsNullOrWhiteSpace(listResult.Message) ? "未检测到 USB 打印机。" : listResult.Message,
                    "请确认打印机已开机并通过 USB 线连接;可重新插拔 USB 线,或更换到主板直连的 USB 接口后重试。");
            }
            else
            {
                StatusHub.Success("扫描 USB 打印机", listResult.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("scan-usb", ex.ToString());
            StatusHub.Error("扫描 USB 打印机", "扫描失败:" + ex.Message, "请重新插拔 USB 线后重试,或导出诊断报告反馈。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyUsbRows(IReadOnlyList<(UsbPrinterInfo Info, UsbConnectPlan? Plan)> prepared)
    {
        UsbDevices.Clear();
        foreach (var item in prepared)
        {
            var row = new UsbDeviceRow(item.Info, item.Plan?.Decision);
            row.ConnectCommand = new AsyncRelayCommand(() => ConnectUsbAsync(row));
            UsbDevices.Add(row);
        }

        OnPropertyChanged(nameof(HasUsbDevices));
    }

    private async Task ConnectUsbAsync(UsbDeviceRow row)
    {
        if (IsBusy)
        {
            StatusHub.Warn("连接打印机", "已有操作正在执行,请稍候。");
            return;
        }

        if (_services.Installer is null || _services.UsbConnect is null)
        {
            StatusHub.Error("连接打印机", "安装服务不可用。", string.Join(" ", _services.StartupErrors));
            return;
        }

        IsBusy = true;
        try
        {
            Steps.Clear();
            OnPropertyChanged(nameof(HasSteps));

            var device = row.Info;

            if (device.State == UsbBindingState.PortMissing)
            {
                StatusHub.Warn("连接打印机", "系统尚未为该 USB 打印机分配端口。", device.ActionText);
                return;
            }

            if (device.State == UsbBindingState.Ready)
            {
                await OfferTestPageAsync(device.ExistingQueueName);
                return;
            }

            StatusHub.Info("准备连接", $"正在为「{row.DeviceName}」生成安装计划 …");

            InstallPlan? installPlan;
            UsbConnectPlan? usbPlan = null;
            var manual = _services.ManualCandidate;

            if (manual is not null)
            {
                var port = UsbPrinterLocator.BuildPortSpec(device.PortName, portExists: true);
                var built = _services.Installer.BuildPlan(device.Device, manual, port, null);
                if (!built.IsSuccess || built.Value is null)
                {
                    ResultText.Publish("生成安装计划", built);
                    return;
                }

                row.ApplyDecision(InstallHelper.ManualDecision(manual));
                installPlan = built.Value;
            }
            else
            {
                var prepared = _services.UsbConnect.Prepare(device);
                usbPlan = prepared.Value;
                if (usbPlan is null)
                {
                    ResultText.Publish("生成安装计划", prepared);
                    return;
                }

                row.ApplyDecision(usbPlan.Decision);
                installPlan = usbPlan.Plan;
                if (installPlan is null)
                {
                    StatusHub.Report(
                        "生成安装计划",
                        string.IsNullOrWhiteSpace(usbPlan.Summary) ? "未能生成安装计划。" : usbPlan.Summary,
                        StatusHub.LevelOf(prepared.Status),
                        usbPlan.Suggestion);
                    return;
                }
            }

            if (installPlan.RequiresDownload)
            {
                StatusHub.Warn(
                    "连接打印机",
                    "该型号需要从厂商官方源在线补齐驱动(L2),本版本尚未实现。",
                    "请在「驱动管理」页手动选择可用驱动,或使用厂商官方安装包安装后再回到本页连接。");
                return;
            }

            if (!_services.Dialogs.Confirm("连接确认 · 风险提示", InstallHelper.BuildDisclosure(installPlan), "同意并继续", "取消"))
            {
                StatusHub.Warn("连接已取消", "你取消了本次安装。", "如需连接,请再次点击「一键连接」并在确认框中选择「同意并继续」。");
                return;
            }

            StatusHub.Info("连接打印机", $"正在安装驱动并创建队列「{installPlan.QueueName}」,请在弹出的 UAC 确认框中选择「是」…");

            var progress = new Progress<InstallStepResult>(step => Steps.Add(new StepRow(step)));
            OperationResult<InstallReport> operation;

            if (usbPlan is not null)
            {
                var planRef = usbPlan;
                operation = await Task.Run(() => _services.UsbConnect!.Connect(planRef, printTestPage: true, progress));
            }
            else
            {
                var planRef = installPlan;
                operation = await Task.Run(() => _services.Installer!.Install(planRef, progress));
            }

            RefreshSteps(operation.Value?.Steps);
            ResultText.Publish("连接打印机", operation);

            if (operation.IsSuccess)
            {
                await RefreshUsbAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("connect-usb", ex.ToString());
            StatusHub.Error("连接打印机", "连接失败:" + ex.Message, "请重新插拔 USB 线后重试,或导出诊断报告反馈。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OfferTestPageAsync(string? queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            StatusHub.Warn("打印测试页", "未找到可用的打印队列。", "请先在「任务队列与状态」页确认队列是否已创建。");
            return;
        }

        var confirmed = _services.Dialogs.Confirm(
            "打印测试页",
            $"USB 打印机已就绪(队列「{queueName}」)。\n\n是否立即打印一张 Windows 系统测试页以验证连接?",
            "打印测试页",
            "暂不打印");

        if (!confirmed)
        {
            StatusHub.Info("连接打印机", $"设备已就绪,队列「{queueName}」可直接使用,未打印测试页。");
            return;
        }

        StatusHub.Info("打印测试页", $"正在向「{queueName}」提交系统测试页 …");
        var result = await Task.Run(() => _services.Installer!.PrintSystemTestPage(queueName));
        ResultText.Publish("打印测试页", result);
    }

    private async Task RefreshUsbAsync()
    {
        if (_services.UsbConnect is null)
        {
            return;
        }

        try
        {
            var listResult = await Task.Run(() => _services.UsbConnect.ListUsbPrinters());
            if (!listResult.IsSuccess || listResult.Value is null)
            {
                return;
            }

            var devices = listResult.Value;
            var prepared = await Task.Run(() =>
                devices
                    .Select(device => (Info: device, Plan: _services.UsbConnect!.Prepare(device).Value))
                    .ToList());

            ApplyUsbRows(prepared);
            UsbSummary = listResult.Message;
        }
        catch (Exception ex)
        {
            AppLog.Write("refresh-usb", ex.ToString());
        }
    }

    // ──────────────────────────── 局域网 ────────────────────────────

    private async Task ScanNetworkAsync()
    {
        IsBusy = true;
        StatusHub.Info("扫描局域网", "正在通过 SNMP / mDNS / WS-Discovery 发现网络打印机(约 8 秒)…");
        try
        {
            var result = await _services.Network.DiscoverAllAsync(DiscoveryOptions.Default);
            if (!result.IsSuccess || result.Value is null)
            {
                NetworkSummary = ResultText.Message(result);
                ResultText.Publish("扫描局域网", result);
                return;
            }

            var devices = result.Value;
            var decided = await Task.Run(() =>
                devices.Select(device => (Device: device, Decision: _services.MatchEngine?.Decide(device, preferDriverFree: true)))
                    .ToList());

            NetworkDevices.Clear();
            foreach (var item in decided)
            {
                AddNetworkRow(item.Device, item.Decision);
            }

            NetworkSummary = result.Message;
            OnPropertyChanged(nameof(HasNetworkDevices));

            if (devices.Count == 0)
            {
                StatusHub.Warn("扫描局域网", result.Message, "请确认打印机与本机在同一网段,且未禁用 SNMP/mDNS/WS-Discovery;也可用下方「手动添加」直接输入 IP。");
            }
            else
            {
                StatusHub.Success("扫描局域网", result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("scan-network", ex.ToString());
            StatusHub.Error("扫描局域网", "扫描失败:" + ex.Message, "请检查网络连通性后重试,或改用手动添加。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddManualAsync()
    {
        var address = (ManualAddress ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            StatusHub.Warn("手动添加", "请输入 IP 地址或主机名。", "例如 192.168.1.20 或 printer.local。");
            return;
        }

        IsBusy = true;
        try
        {
            var protocol = SelectedProtocol;
            var kind = ToConnectionKind(protocol);

            if (kind == ConnectionKind.Shared)
            {
                if (!address.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    address = @"\\" + address;
                }

                var shared = new DiscoveredPrinter
                {
                    Id = "manual:" + address,
                    DisplayName = address,
                    Connection = ConnectionKind.Shared,
                    Address = address,
                    DiscoverySource = "手动添加",
                };

                var sharedDecision = await Task.Run(() => _services.MatchEngine?.Decide(shared, preferDriverFree: true));
                AddNetworkRow(shared, sharedDecision);
                OnPropertyChanged(nameof(HasNetworkDevices));
                StatusHub.Success("手动添加", $"已添加共享打印机「{address}」。", "连接共享打印机要求目标主机允许当前账户访问。");
                return;
            }

            StatusHub.Info("手动添加", $"正在探测 {address}({protocol})…");
            var probe = await _services.Network.DiscoverHostAsync(address);

            DiscoveredPrinter device;
            if (probe.IsSuccess && probe.Value is not null)
            {
                device = ApplyProtocol(probe.Value, kind, address);
            }
            else
            {
                device = ApplyProtocol(
                    new DiscoveredPrinter
                    {
                        Id = "manual:" + address,
                        DisplayName = address,
                        Connection = kind,
                        Address = address,
                        DiscoverySource = "手动添加(未探测到服务)",
                    },
                    kind,
                    address);
            }

            var decision = await Task.Run(() => _services.MatchEngine?.Decide(device, preferDriverFree: true));
            AddNetworkRow(device, decision);
            OnPropertyChanged(nameof(HasNetworkDevices));

            if (probe.IsSuccess)
            {
                StatusHub.Success("手动添加", $"已添加「{device.DisplayName}」({protocol})。");
            }
            else
            {
                var suggestion = string.IsNullOrWhiteSpace(probe.Suggestion)
                    ? "仍可尝试连接,若失败请确认设备地址与协议是否正确。"
                    : probe.Suggestion + " 仍可尝试连接。";
                StatusHub.Warn("手动添加", $"{address} 未探测到打印服务,已按 {protocol} 添加为手动目标。", suggestion);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("manual-add", ex.ToString());
            StatusHub.Error("手动添加", "添加失败:" + ex.Message, "请检查地址格式后重试。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddNetworkRow(DiscoveredPrinter device, DriverDecision? decision)
    {
        var row = new NetworkDeviceRow(device, decision);
        row.ConnectCommand = new AsyncRelayCommand(() => ConnectNetworkAsync(row));
        NetworkDevices.Add(row);
    }

    private async Task ConnectNetworkAsync(NetworkDeviceRow row)
    {
        if (IsBusy)
        {
            StatusHub.Warn("连接打印机", "已有操作正在执行,请稍候。");
            return;
        }

        if (_services.Installer is null || _services.MatchEngine is null)
        {
            StatusHub.Error("连接打印机", "安装服务不可用。", string.Join(" ", _services.StartupErrors));
            return;
        }

        IsBusy = true;
        try
        {
            Steps.Clear();
            OnPropertyChanged(nameof(HasSteps));

            StatusHub.Info("准备连接", $"正在为「{row.Name}」匹配驱动 …");

            var device = row.Device;
            var decision = await Task.Run(() => _services.MatchEngine!.Decide(device, preferDriverFree: true));
            row.ApplyDecision(decision);

            if (decision.Selected is null)
            {
                StatusHub.Report("生成安装计划", decision.Message, StatusHub.LevelOf(decision.Status), decision.Suggestion);
                return;
            }

            if (decision.RequiresDownload)
            {
                StatusHub.Warn(
                    "连接打印机",
                    "该型号需要从厂商官方源在线补齐驱动(L2),本版本尚未实现。",
                    "请在「驱动管理」页手动选择可用驱动,或使用厂商官方安装包安装后再回到本页连接。");
                return;
            }

            var port = InstallHelper.BuildPortSpec(device);
            var built = _services.Installer.BuildPlan(device, decision.Selected, port, null);
            if (!built.IsSuccess || built.Value is null)
            {
                ResultText.Publish("生成安装计划", built);
                return;
            }

            var plan = built.Value;
            if (!_services.Dialogs.Confirm("连接确认 · 风险提示", InstallHelper.BuildDisclosure(plan), "同意并继续", "取消"))
            {
                StatusHub.Warn("连接已取消", "你取消了本次安装。");
                return;
            }

            StatusHub.Info("连接打印机", $"正在安装驱动并创建队列「{plan.QueueName}」…");

            var progress = new Progress<InstallStepResult>(step => Steps.Add(new StepRow(step)));
            var operation = await Task.Run(() => _services.Installer!.Install(plan, progress));

            RefreshSteps(operation.Value?.Steps);
            ResultText.Publish("连接打印机", operation);
        }
        catch (Exception ex)
        {
            AppLog.Write("connect-network", ex.ToString());
            StatusHub.Error("连接打印机", "连接失败:" + ex.Message, "请检查网络连通性后重试,或导出诊断报告反馈。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ──────────────────────────── 工具 ────────────────────────────

    private void RefreshSteps(IReadOnlyList<InstallStepResult>? steps)
    {
        if (steps is null)
        {
            return;
        }

        Steps.Clear();
        foreach (var step in steps)
        {
            Steps.Add(new StepRow(step));
        }

        OnPropertyChanged(nameof(HasSteps));
    }

    private static ConnectionKind ToConnectionKind(string? protocol) => protocol switch
    {
        "RAW" => ConnectionKind.RawTcp,
        "LPD" => ConnectionKind.Lpd,
        "共享" => ConnectionKind.Shared,
        _ => ConnectionKind.Ipp,
    };

    private static DiscoveredPrinter ApplyProtocol(DiscoveredPrinter source, ConnectionKind kind, string address)
    {
        var ip = DeviceNameParser.ExtractAddress(address) ?? address;

        return new DiscoveredPrinter
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Model = source.Model,
            Vendor = source.Vendor,
            Connection = kind,
            Address = ip,
            PortName = source.PortName,
            ExistingQueueName = source.ExistingQueueName,
            ExistingDriverName = source.ExistingDriverName,
            IppUri = kind == ConnectionKind.Ipp ? source.IppUri ?? $"ipp://{ip}:631/ipp/print" : null,
            HardwareIds = source.HardwareIds,
            CompatibleIds = source.CompatibleIds,
            IsMopria = source.IsMopria,
            SupportsIpp = kind == ConnectionKind.Ipp || source.SupportsIpp,
            SupportsRaw9100 = kind == ConnectionKind.RawTcp || source.SupportsRaw9100,
            SupportsLpd = kind == ConnectionKind.Lpd || source.SupportsLpd,
            Readiness = source.Readiness,
            DiscoverySource = source.DiscoverySource,
            Notes = source.Notes,
        };
    }
}
