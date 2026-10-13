using System.Text.Json;
using PrinterMate.Core.Infrastructure;

namespace PrinterMate.Core.Discovery;

/// <summary>
/// 本机打印资源枚举。优先使用 PowerShell(PrintManagement + PnP),
/// 失败时回落到 winspool 的 Win32 接口,保证在 PrintManagement 不可用时仍能工作。
/// </summary>
public sealed class LocalPrinterEnumerator
{
    private const string ListQueuesScript = """
        Get-Printer | Select-Object Name, DriverName, PortName, PrinterStatus, Type, Shared, ShareName, Comment, Location, PrintProcessor
        """;

    private const string ListPnpDevicesScript = """
        Get-PnpDevice -Class Printer -ErrorAction SilentlyContinue | ForEach-Object {
            $hw = @()
            $cid = @()
            $inf = $null
            try { $hw = @((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data) } catch { }
            try { $cid = @((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data) } catch { }
            try { $inf = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data } catch { }
            [pscustomobject]@{
                FriendlyName = $_.FriendlyName
                InstanceId   = $_.InstanceId
                Status       = [string]$_.Status
                Class        = $_.Class
                Manufacturer = $_.Manufacturer
                InfPath      = $inf
                HardwareIds  = $hw
                CompatibleIds = $cid
            }
        }
        """;

    private const string ListDriversScript = """
        Get-PrinterDriver | Select-Object Name, InfPath, Manufacturer, DriverVersion, PrinterEnvironment
        """;

    private readonly PowerShellRunner _powerShell;

    public LocalPrinterEnumerator(PowerShellRunner? powerShell = null)
    {
        _powerShell = powerShell ?? new PowerShellRunner(TimeSpan.FromSeconds(45));
    }

    /// <summary>枚举本机打印队列(含网络连接队列)。</summary>
    public OperationResult<IReadOnlyList<DiscoveredPrinter>> EnumeratePrintQueues()
    {
        var json = _powerShell.RunJson(ListQueuesScript);

        if (json.IsSuccess && json.Value is { Count: > 0 })
        {
            var list = json.Value.Select(MapQueue).OfType<DiscoveredPrinter>().ToList();
            if (list.Count > 0)
            {
                return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok(list, $"检测到 {list.Count} 个本机打印队列。");
            }
        }

        // 回落:直接调用 winspool
        try
        {
            var native = Interop.SpoolerApi.EnumeratePrinters();
            var list = native.Select(MapNativeQueue).ToList();
            var message = json.IsSuccess
                ? $"检测到 {list.Count} 个本机打印队列(来源:Win32 兜底)。"
                : $"PrintManagement 不可用,已改用 Win32 接口枚举,共 {list.Count} 个队列。";
            return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok(list, message);
        }
        catch (Exception ex)
        {
            return ex.ToFailure<IReadOnlyList<DiscoveredPrinter>>("枚举本机打印机");
        }
    }

    /// <summary>枚举 PnP 打印设备(包含尚未创建队列的 USB / WSD 设备)。</summary>
    public OperationResult<IReadOnlyList<DiscoveredPrinter>> EnumeratePnpDevices()
    {
        var json = _powerShell.RunJson(ListPnpDevicesScript);
        if (!json.IsSuccess)
        {
            return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Fail(
                json.Message,
                "请确认 Get-PnpDevice 可用(需要 Windows 10 1809 及以上版本)。");
        }

        var list = new List<DiscoveredPrinter>();
        foreach (var item in json.Value ?? Array.Empty<JsonElement>())
        {
            var friendlyName = item.GetString("FriendlyName");
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                continue;
            }

            var instanceId = item.GetString("InstanceId");
            var manufacturer = item.GetString("Manufacturer");
            var model = DeviceNameParser.CleanModelName(friendlyName);
            var vendor = DeviceNameParser.GuessVendor(manufacturer, friendlyName);
            var hardwareIds = item.GetStringArray("HardwareIds");
            var compatibleIds = item.GetStringArray("CompatibleIds");
            var connection = ResolvePnpConnection(instanceId, hardwareIds, compatibleIds);
            var infPath = item.GetStringOrNull("InfPath");

            list.Add(new DiscoveredPrinter
            {
                Id = "pnp:" + (string.IsNullOrWhiteSpace(instanceId) ? friendlyName : instanceId),
                DisplayName = friendlyName,
                Model = DeviceNameParser.BuildModelForMatching(vendor, model),
                Vendor = vendor,
                Connection = connection,
                Address = DeviceNameParser.ExtractAddress(instanceId) ?? string.Empty,
                PortName = UsbPrinterLocator.ResolvePortName(instanceId) ?? string.Empty,
                HardwareIds = hardwareIds,
                CompatibleIds = compatibleIds,
                SupportsIpp = connection == ConnectionKind.Wsd || connection == ConnectionKind.Ipp,
                IsMopria = false,
                Readiness = string.IsNullOrWhiteSpace(infPath) ? DriverReadiness.NeedsDriver : DriverReadiness.Installed,
                DiscoverySource = "PnP",
                Notes = string.IsNullOrWhiteSpace(item.GetString("Status")) ? string.Empty : $"设备状态:{item.GetString("Status")}",
            });
        }

        return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok(list, $"检测到 {list.Count} 个 PnP 打印设备。");
    }

    /// <summary>本机已注册的打印驱动。</summary>
    public OperationResult<IReadOnlyList<InstalledDriverInfo>> EnumerateInstalledDrivers()
    {
        var json = _powerShell.RunJson(ListDriversScript);
        if (!json.IsSuccess)
        {
            return OperationResult<IReadOnlyList<InstalledDriverInfo>>.Fail(json.Message, "请确认 PrintManagement 模块可用。");
        }

        var list = (json.Value ?? Array.Empty<JsonElement>())
            .Select(d => new InstalledDriverInfo(
                d.GetString("Name"),
                d.GetString("InfPath"),
                d.GetString("Manufacturer"),
                d.GetString("DriverVersion"),
                d.GetString("PrinterEnvironment")))
            .Where(d => d.Name.Length > 0)
            .ToList();

        return OperationResult<IReadOnlyList<InstalledDriverInfo>>.Ok(list, $"本机已注册 {list.Count} 个打印驱动。");
    }

    /// <summary>队列 + PnP 设备合并去重后的完整本机视图。</summary>
    public OperationResult<IReadOnlyList<DiscoveredPrinter>> EnumerateAll()
    {
        var queues = EnumeratePrintQueues();
        var pnp = EnumeratePnpDevices();

        var merged = new List<DiscoveredPrinter>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queue in queues.Value ?? Array.Empty<DiscoveredPrinter>())
        {
            merged.Add(queue);
            seenModels.Add($"{queue.Vendor}|{queue.Model}");
        }

        foreach (var device in pnp.Value ?? Array.Empty<DiscoveredPrinter>())
        {
            // 已经有同名队列的设备不重复展示
            if (seenModels.Contains($"{device.Vendor}|{device.Model}"))
            {
                continue;
            }

            if (merged.Any(m => string.Equals(m.DisplayName, device.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(device);
        }

        if (!queues.IsSuccess && !pnp.IsSuccess)
        {
            return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Fail(
                $"枚举本机打印资源失败:{queues.Message} {pnp.Message}",
                "请确认 Print Spooler 服务正在运行,并尝试以管理员身份重新打开本程序。");
        }

        return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok(
            merged,
            $"本机共发现 {merged.Count} 个打印资源(队列 {queues.Value?.Count ?? 0} 个,PnP 设备 {pnp.Value?.Count ?? 0} 个)。");
    }

    private static DiscoveredPrinter? MapQueue(JsonElement item)
    {
        var name = item.GetString("Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var driverName = item.GetString("DriverName");
        var portName = item.GetString("PortName");
        var connection = DeviceNameParser.GuessConnection(portName);
        var vendor = DeviceNameParser.GuessVendor(driverName, name);
        var model = DeviceNameParser.CleanModelName(name);
        var isClassDriver = driverName.Contains("Class Driver", StringComparison.OrdinalIgnoreCase);

        return new DiscoveredPrinter
        {
            Id = "queue:" + name,
            DisplayName = name,
            Model = DeviceNameParser.BuildModelForMatching(vendor, model),
            Vendor = vendor,
            Connection = connection,
            Address = DeviceNameParser.ExtractAddress(portName) ?? string.Empty,
            PortName = portName,
            ExistingQueueName = name,
            ExistingDriverName = driverName,
            IppUri = connection == ConnectionKind.Ipp ? BuildIppUriFromPort(portName) : null,
            SupportsIpp = connection is ConnectionKind.Ipp or ConnectionKind.Wsd || isClassDriver,
            IsMopria = isClassDriver && connection is ConnectionKind.Ipp or ConnectionKind.Wsd,
            Readiness = isClassDriver ? DriverReadiness.DriverFree : DriverReadiness.Installed,
            DiscoverySource = "本机队列",
            Notes = string.IsNullOrWhiteSpace(item.GetString("Comment")) ? string.Empty : item.GetString("Comment"),
        };
    }

    private static DiscoveredPrinter MapNativeQueue(Interop.NativePrinterInfo info) => new()
    {
        Id = "queue:" + info.Name,
        DisplayName = info.Name,
        Model = DeviceNameParser.BuildModelForMatching(DeviceNameParser.GuessVendor(info.DriverName, info.Name), DeviceNameParser.CleanModelName(info.Name)),
        Vendor = DeviceNameParser.GuessVendor(info.DriverName, info.Name),
        Connection = DeviceNameParser.GuessConnection(info.PortName),
        Address = DeviceNameParser.ExtractAddress(info.PortName) ?? string.Empty,
        PortName = info.PortName,
        ExistingQueueName = info.Name,
        ExistingDriverName = info.DriverName,
        SupportsIpp = DeviceNameParser.GuessConnection(info.PortName) is ConnectionKind.Ipp or ConnectionKind.Wsd,
        Readiness = info.DriverName.Contains("Class Driver", StringComparison.OrdinalIgnoreCase)
            ? DriverReadiness.DriverFree
            : DriverReadiness.Installed,
        DiscoverySource = "本机队列(Win32)",
        Notes = info.Comment,
    };

    private static ConnectionKind ResolvePnpConnection(string instanceId, IReadOnlyList<string> hardwareIds, IReadOnlyList<string> compatibleIds)
    {
        var all = string.Join(' ', new[] { instanceId }.Concat(hardwareIds).Concat(compatibleIds));

        if (all.Contains("USBPRINT", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Usb;
        }

        if (all.Contains("WSDPRINT", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Wsd;
        }

        if (all.Contains("IPPRINT", StringComparison.OrdinalIgnoreCase) || all.Contains("IPP", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Ipp;
        }

        if (instanceId.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Usb;
        }

        return ConnectionKind.Unknown;
    }

    private static string? BuildIppUriFromPort(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        var ip = DeviceNameParser.ExtractAddress(portName);
        return ip is null ? null : $"ipp://{ip}:631/ipp/print";
    }
}
