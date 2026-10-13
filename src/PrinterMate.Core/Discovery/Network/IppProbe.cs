using System.Net;
using System.Net.Sockets;

namespace PrinterMate.Core.Discovery;

/// <summary>对单个地址做能力探测:IPP(631)、RAW(9100)、LPD(515)端口探测 + IPP Get-Printer-Attributes。</summary>
public sealed class IppProbe
{
    private static readonly TimeSpan DefaultPortTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultAttributeTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _attributeTimeout;
    private readonly IIppAttributeSource? _source;
    private IIppAttributeSource? _defaultSource;

    public IppProbe(TimeSpan? timeout = null)
        : this(timeout, null)
    {
    }

    /// <summary>注入自定义 IPP 属性来源(便于单元测试;生产代码使用默认实现)。</summary>
    public IppProbe(TimeSpan? timeout, IIppAttributeSource? source)
    {
        _attributeTimeout = timeout is { Ticks: > 0 } ? timeout.Value : DefaultAttributeTimeout;
        _source = source;
    }

    /// <summary>探测一个具体 IP。</summary>
    public Task<DiscoveredPrinter?> ProbeAsync(IPAddress address, DiscoveryOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        return ProbeCoreAsync(address.ToString(), address, options ?? DiscoveryOptions.Default, ct);
    }

    /// <summary>探测一个 IP 或主机名(必要时做名称解析)。</summary>
    public Task<DiscoveredPrinter?> ProbeHostAsync(string hostOrIp, DiscoveryOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            return Task.FromResult<DiscoveredPrinter?>(null);
        }

        return ProbeHostCoreAsync(DiscoveryText.NormalizeHost(hostOrIp), options ?? DiscoveryOptions.Default, ct);
    }

    /// <summary>构造 ipp:// 打印 URI(与 PrinterMate.Printing.IppClient.BuildIppUri 语义一致)。</summary>
    public static Uri BuildIppUri(string host, int port = 631, string resource = "/ipp/print")
    {
        var normalized = DiscoveryText.NormalizeHost(host);
        if (IPAddress.TryParse(normalized, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            normalized = $"[{normalized}]";
        }

        var path = string.IsNullOrWhiteSpace(resource) ? "/ipp/print" : resource.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return new Uri($"ipp://{normalized}:{port}{path}");
    }

    private async Task<DiscoveredPrinter?> ProbeHostCoreAsync(string host, DiscoveryOptions options, CancellationToken ct)
    {
        IPAddress? address = IPAddress.TryParse(host, out var parsed) ? parsed : null;
        if (address is null)
        {
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                address = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception)
            {
                // 解析失败仍可让 TcpClient 自己尝试。
            }
        }

        return await ProbeCoreAsync(host, address, options, ct).ConfigureAwait(false);
    }

    private async Task<DiscoveredPrinter?> ProbeCoreAsync(string host, IPAddress? address, DiscoveryOptions options, CancellationToken ct)
    {
        try
        {
            var portTimeout = PortTimeout(options);
            var ippTask = options.EnableIppProbe
                ? OpenAsync(host, address, TcpPortProbe.IppPort, portTimeout, ct)
                : Task.FromResult(false);
            var rawTask = options.EnableRawProbe
                ? OpenAsync(host, address, TcpPortProbe.RawPort, portTimeout, ct)
                : Task.FromResult(false);
            var lpdTask = OpenAsync(host, address, TcpPortProbe.LpdPort, portTimeout, ct);

            await Task.WhenAll(ippTask, rawTask, lpdTask).ConfigureAwait(false);

            var supportsIpp = ippTask.Result;
            var supportsRaw = rawTask.Result;
            var supportsLpd = lpdTask.Result;
            if (!supportsIpp && !supportsRaw && !supportsLpd)
            {
                return null;
            }

            Uri? ippUri = null;
            IppDeviceAttributes? attributes = null;
            if (supportsIpp)
            {
                ippUri = BuildIppUri(host, TcpPortProbe.IppPort, "/ipp/print");
                attributes = await TryGetAttributesAsync(ippUri, options, ct).ConfigureAwait(false);
            }

            return BuildResult(host, address, ippUri, attributes, supportsIpp, supportsRaw, supportsLpd);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Task<bool> OpenAsync(string host, IPAddress? address, int port, TimeSpan timeout, CancellationToken ct) =>
        address is not null
            ? TcpPortProbe.IsOpenAsync(address, port, timeout, ct)
            : TcpPortProbe.IsOpenAsync(host, port, timeout, ct);

    private DiscoveredPrinter BuildResult(
        string host,
        IPAddress? address,
        Uri? ippUri,
        IppDeviceAttributes? attributes,
        bool supportsIpp,
        bool supportsRaw,
        bool supportsLpd)
    {
        var hostId = address?.ToString() ?? host;
        var makeAndModel = attributes?.MakeAndModel ?? string.Empty;
        var manufacturer = attributes?.Manufacturer ?? string.Empty;
        var vendor = PrinterBrandParser.IdentifyFrom(manufacturer, makeAndModel) ?? manufacturer;
        var model = PrinterBrandParser.ExtractModel(makeAndModel);

        var isMopria = attributes is not null &&
                       (attributes.SupportsIppEverywhere || attributes.UrfSupported.Count > 0);

        var connection = supportsIpp
            ? ConnectionKind.Ipp
            : supportsRaw
                ? ConnectionKind.RawTcp
                : ConnectionKind.Lpd;

        var displayName = !string.IsNullOrWhiteSpace(makeAndModel)
            ? makeAndModel
            : !string.IsNullOrWhiteSpace(attributes?.Info)
                ? attributes!.Info
                : $"{hostId} 上的网络打印机";

        var formats = attributes?.DocumentFormats is { Count: > 0 } list ? string.Join(',', list) : string.Empty;

        return new DiscoveredPrinter
        {
            Id = DiscoveryText.BuildNetworkId(hostId),
            DisplayName = displayName,
            Model = model,
            Vendor = vendor,
            Connection = connection,
            Address = hostId,
            IppUri = ippUri?.ToString(),
            IsMopria = isMopria,
            SupportsIpp = supportsIpp,
            SupportsRaw9100 = supportsRaw,
            SupportsLpd = supportsLpd,
            Readiness = isMopria ? DriverReadiness.DriverFree : DriverReadiness.NeedsDriver,
            DiscoverySource = "端口探测",
            Notes = DiscoveryText.Pick(string.Empty, attributes?.PrinterState, formats, attributes?.Uuid),
        };
    }

    private async Task<IppDeviceAttributes?> TryGetAttributesAsync(Uri ippUri, DiscoveryOptions options, CancellationToken ct)
    {
        var timeout = _attributeTimeout;
        if (options.Timeout > TimeSpan.Zero && timeout > options.Timeout)
        {
            timeout = options.Timeout;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            var source = _source ?? (_defaultSource ??= new PrintingIppAttributeSource(_attributeTimeout));
            return await source.GetAttributesAsync(ippUri, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeSpan PortTimeout(DiscoveryOptions options)
    {
        var value = DefaultPortTimeout;
        if (options.Timeout > TimeSpan.Zero && options.Timeout < value)
        {
            value = options.Timeout;
        }

        if (value > TimeSpan.FromSeconds(1))
        {
            value = TimeSpan.FromSeconds(1);
        }

        return value < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : value;
    }
}
