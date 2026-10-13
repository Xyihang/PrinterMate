using PrinterMate.Catalog;

namespace PrinterMate.Core;

/// <summary>连接方式。</summary>
public enum ConnectionKind
{
    /// <summary>本机已有的打印队列。</summary>
    LocalQueue,

    Usb,

    Ipp,

    Wsd,

    /// <summary>网络 RAW(TCP 9100)。</summary>
    RawTcp,

    Lpd,

    /// <summary>Windows 共享队列(\\host\printer)。</summary>
    Shared,

    Unknown,
}

/// <summary>驱动就绪状态。</summary>
public enum DriverReadiness
{
    /// <summary>已有可用驱动与队列。</summary>
    Installed,

    /// <summary>免驱可用(Mopria / IPP Class Driver)。</summary>
    DriverFree,

    /// <summary>需要安装驱动。</summary>
    NeedsDriver,

    Unknown,
}

/// <summary>发现到的一台打印机(本机队列、PnP 设备、网络设备或手动目标)。</summary>
public sealed class DiscoveredPrinter
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Model { get; init; } = string.Empty;

    public string Vendor { get; init; } = string.Empty;

    public ConnectionKind Connection { get; init; } = ConnectionKind.Unknown;

    public string Address { get; init; } = string.Empty;

    public string PortName { get; init; } = string.Empty;

    /// <summary>本机已存在的队列名(若已安装)。</summary>
    public string? ExistingQueueName { get; init; }

    public string? ExistingDriverName { get; init; }

    /// <summary>IPP 打印 URI,例如 ipp://192.168.1.20:631/ipp/print。</summary>
    public string? IppUri { get; init; }

    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();

    /// <summary>设备自述支持 Mopria / IPP Everywhere。</summary>
    public bool IsMopria { get; init; }

    public bool SupportsIpp { get; init; }

    public bool SupportsRaw9100 { get; init; }

    public bool SupportsLpd { get; init; }

    public DriverReadiness Readiness { get; init; } = DriverReadiness.Unknown;

    /// <summary>发现来源:本机队列 / PnP / SNMP / mDNS / WS-Discovery / 手动。</summary>
    public string DiscoverySource { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string ConnectionText => Connection switch
    {
        ConnectionKind.LocalQueue => "本机队列",
        ConnectionKind.Usb => "USB",
        ConnectionKind.Ipp => "网络 IPP",
        ConnectionKind.Wsd => "网络 WSD",
        ConnectionKind.RawTcp => "网络 RAW",
        ConnectionKind.Lpd => "网络 LPD",
        ConnectionKind.Shared => "共享队列",
        _ => "未知",
    };

    public string ReadinessText => Readiness switch
    {
        DriverReadiness.Installed => "已装驱动",
        DriverReadiness.DriverFree => "免驱可用",
        DriverReadiness.NeedsDriver => "需安装驱动",
        _ => "状态未知",
    };

    public DeviceMatchInput ToMatchInput() => new(Model, Vendor, HardwareIds, CompatibleIds);

    public override string ToString() => $"{DisplayName}({Model}) [{ConnectionText}] {ReadinessText}";
}
