using System.Text.RegularExpressions;
using PrinterMate.Interop;

namespace PrinterMate.Core.Discovery;

/// <summary>USB 打印机的定位结果。</summary>
public sealed record UsbPrinterInfo(
    DiscoveredPrinter Device,
    string PortName,
    string? ExistingQueueName,
    string? ExistingDriverName,
    UsbBindingState State,
    string ActionText)
{
    public bool ReadyToPrint => State == UsbBindingState.Ready;
}

/// <summary>USB 打印机的绑定状态。</summary>
public enum UsbBindingState
{
    /// <summary>已有队列且驱动可用,可以直接打印。</summary>
    Ready,

    /// <summary>设备已插上,但还没有对应的打印队列,需要创建。</summary>
    NeedsQueue,

    /// <summary>已有队列,但驱动是通用占位驱动(如 MS Publisher Imagesetter / 通用文本),需要更换。</summary>
    NeedsDriverUpgrade,

    /// <summary>端口尚未被系统创建,设备可能未被完整识别。</summary>
    PortMissing,

    /// <summary>无法判断。</summary>
    Unknown,
}

/// <summary>
/// USB 打印机的定位与状态判定。
///
/// USB 打印机的端口(PortName,形如 USB001)**由 PnP 自动创建**,
/// 不能也不应该手工调用 Add-PrinterPort;设备实例 ID 的末段(retail...&amp;USB001)就是端口名。
/// </summary>
public static class UsbPrinterLocator
{
    private static readonly Regex UsbPortSuffix = new(@"&(?<port>USB\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UsbPrintInstance = new(@"^USBPRINT\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>常见"非厂商驱动"占位驱动,命中的队列需要更换为厂商驱动。</summary>
    private static readonly string[] PlaceholderDrivers =
    {
        "MS Publisher Imagesetter",
        "Microsoft XPS Document Writer v4",
        "Generic / Text Only",
        "通用 / 仅文本",
        "Microsoft Shared Fax Driver",
    };

    /// <summary>从 PnP 实例 ID 解析 USB 端口名(如 USBPRINT\HPXXX\7&amp;1A2B&amp;0&amp;USB001 → USB001)。</summary>
    public static string? ResolvePortName(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var match = UsbPortSuffix.Match(instanceId.Trim());
        return match.Success ? match.Groups["port"].Value.ToUpperInvariant() : null;
    }

    public static bool IsUsbPrinter(string? instanceId)
        => !string.IsNullOrWhiteSpace(instanceId) && UsbPrintInstance.IsMatch(instanceId.Trim());

    /// <summary>把型号名转成 USBPRINT 硬件 ID 里的写法,用于匹配(去空格、下划线、大写)。</summary>
    public static string NormalizeForHardwareId(string? model)
        => string.IsNullOrWhiteSpace(model)
            ? string.Empty
            : Regex.Replace(model.ToUpperInvariant(), @"[^A-Z0-9]+", "_").Trim('_');

    /// <summary>列出所有 USB 打印机及其绑定状态。</summary>
    public static OperationResult<IReadOnlyList<UsbPrinterInfo>> Locate(LocalPrinterEnumerator? enumerator = null)
    {
        enumerator ??= new LocalPrinterEnumerator();

        var pnp = enumerator.EnumeratePnpDevices();
        var queues = enumerator.EnumeratePrintQueues();

        if (!pnp.IsSuccess && !queues.IsSuccess)
        {
            return OperationResult<IReadOnlyList<UsbPrinterInfo>>.Fail(
                $"无法枚举 USB 打印机:{pnp.Message} {queues.Message}",
                "请确认 Print Spooler 服务正在运行,并重新插拔 USB 线后重试。");
        }

        var printerPorts = SafeEnumeratePrinterPorts();
        var queueList = queues.Value ?? Array.Empty<DiscoveredPrinter>();
        var result = new List<UsbPrinterInfo>();

        foreach (var device in pnp.Value ?? Array.Empty<DiscoveredPrinter>())
        {
            var instanceId = device.Id.StartsWith("pnp:", StringComparison.OrdinalIgnoreCase) ? device.Id[4..] : device.Id;
            if (!IsUsbPrinter(instanceId) && device.Connection != ConnectionKind.Usb)
            {
                continue;
            }

            var port = ResolvePortName(instanceId);
            var existingQueue = port is null
                ? null
                : queueList.FirstOrDefault(q => string.Equals(q.PortName, port, StringComparison.OrdinalIgnoreCase));

            var portExists = port is not null && printerPorts.Contains(port);

            var state = DetermineState(port, portExists, existingQueue);
            var action = DescribeAction(state, port, existingQueue);

            result.Add(new UsbPrinterInfo(device, port ?? string.Empty, existingQueue?.ExistingQueueName, existingQueue?.ExistingDriverName, state, action));
        }

        // 已有 USB 队列但 PnP 没枚举出来的情况(例如设备驱动异常),仍然要展示
        foreach (var queue in queueList.Where(q => q.Connection == ConnectionKind.Usb))
        {
            if (result.Any(r => string.Equals(r.PortName, queue.PortName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var state = PlaceholderDrivers.Any(p => queue.ExistingDriverName?.Contains(p, StringComparison.OrdinalIgnoreCase) == true)
                ? UsbBindingState.NeedsDriverUpgrade
                : UsbBindingState.Ready;

            result.Add(new UsbPrinterInfo(queue, queue.PortName, queue.ExistingQueueName, queue.ExistingDriverName, state, DescribeAction(state, queue.PortName, queue)));
        }

        var ready = result.Count(r => r.State == UsbBindingState.Ready);
        return OperationResult<IReadOnlyList<UsbPrinterInfo>>.Ok(
            result.OrderBy(r => r.State).ThenBy(r => r.PortName, StringComparer.OrdinalIgnoreCase).ToList(),
            $"检测到 {result.Count} 台 USB 打印机,其中 {ready} 台可直接打印。");
    }

    /// <summary>给定 USB 端口,推荐可用的 PortSpec(供安装计划使用)。</summary>
    public static PortSpec BuildPortSpec(string? portName, bool portExists)
    {
        if (!string.IsNullOrWhiteSpace(portName) && portExists)
        {
            // 端口已由 PnP 创建:复用现有端口
            return new PortSpec(PortKind.Existing, portName);
        }

        return new PortSpec(PortKind.Usb, portName ?? string.Empty);
    }

    private static UsbBindingState DetermineState(string? port, bool portExists, DiscoveredPrinter? queue)
    {
        if (port is null)
        {
            return queue is not null ? UsbBindingState.Ready : UsbBindingState.Unknown;
        }

        if (!portExists)
        {
            return UsbBindingState.PortMissing;
        }

        if (queue is null)
        {
            return UsbBindingState.NeedsQueue;
        }

        if (PlaceholderDrivers.Any(p => queue.ExistingDriverName?.Contains(p, StringComparison.OrdinalIgnoreCase) == true))
        {
            return UsbBindingState.NeedsDriverUpgrade;
        }

        return UsbBindingState.Ready;
    }

    private static string DescribeAction(UsbBindingState state, string? port, DiscoveredPrinter? queue) => state switch
    {
        UsbBindingState.Ready => $"已就绪,可直接打印(队列「{queue?.ExistingQueueName}」,端口 {port})。",
        UsbBindingState.NeedsQueue => $"设备已连接在端口 {port},但还没有打印队列,需要安装驱动并创建队列。",
        UsbBindingState.NeedsDriverUpgrade => $"当前使用通用占位驱动「{queue?.ExistingDriverName}」,建议更换为厂商驱动以获得完整功能。",
        UsbBindingState.PortMissing => "尚未分配 USB 打印端口,请重新插拔 USB 线或更换 USB 接口。",
        _ => "无法判断设备状态,可尝试重新插拔 USB 线。",
    };

    private static HashSet<string> SafeEnumeratePrinterPorts()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var printer in SpoolerApi.EnumeratePrinters())
        {
            if (!string.IsNullOrWhiteSpace(printer.PortName))
            {
                result.Add(printer.PortName.Trim());
            }
        }

        return result;
    }
}
