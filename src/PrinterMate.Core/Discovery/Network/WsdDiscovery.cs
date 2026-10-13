using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrinterMate.Core.Discovery;

/// <summary>WS-Discovery(SOAP-over-UDP 组播 239.255.255.250:3702,Probe → ProbeMatches)。</summary>
public sealed class WsdDiscovery : INetworkDiscovery
{
    private const int WsdPort = 3702;

    private static readonly IPAddress WsdGroup = IPAddress.Parse("239.255.255.250");

    /// <inheritdoc />
    public string Name => "WS-Discovery";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct)
    {
        var results = new List<DiscoveredPrinter>();
        if (options is null || !options.EnableWsd)
        {
            return results;
        }

        try
        {
            var probe = WsdMessages.BuildProbe();
            var payload = Encoding.UTF8.GetBytes(probe);
            var devices = new List<WsdDevice>();
            var timeout = options.ProviderTimeout(options.EffectiveTimeout);

            await DatagramTransport.SendAndReceiveAsync(
                payload,
                [new IPEndPoint(WsdGroup, WsdPort)],
                bindTo: null,
                multicastGroup: WsdGroup,
                timeout,
                (datagram, _) =>
                {
                    var xml = Encoding.UTF8.GetString(datagram);
                    var parsed = WsdMessages.ParseProbeMatches(xml);
                    if (parsed.Count > 0)
                    {
                        lock (devices)
                        {
                            devices.AddRange(parsed);
                        }
                    }
                },
                ct).ConfigureAwait(false);

            results.AddRange(MapDevices(devices));
        }
        catch (Exception)
        {
            // 无网卡 / 组播不可用 / 无响应:返回空列表。
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 每次发现自建套接字,无需释放长期资源。
    }

    /// <summary>把 ProbeMatches 映射成设备记录,过滤掉非打印设备与没有可解析地址的条目。</summary>
    public static IReadOnlyList<DiscoveredPrinter> MapDevices(IEnumerable<WsdDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var results = new List<DiscoveredPrinter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (!WsdMessages.IsPrinterDevice(device))
            {
                continue;
            }

            var host = WsdMessages.ExtractHost(device);
            if (string.IsNullOrWhiteSpace(host) || !seen.Add(host))
            {
                continue;
            }

            var name = WsdMessages.GuessName(device);

            results.Add(new DiscoveredPrinter
            {
                Id = DiscoveryText.BuildNetworkId(host),
                DisplayName = DiscoveryText.Pick(host, name, device.EndpointAddress),
                Model = string.Empty,
                Vendor = PrinterBrandParser.Identify(device.Scopes) ?? string.Empty,
                Connection = ConnectionKind.Wsd,
                Address = host,
                SupportsIpp = false,
                Readiness = DriverReadiness.NeedsDriver,
                DiscoverySource = "WS-Discovery",
                Notes = DiscoveryText.Pick(string.Empty, device.Scopes, device.Types),
            });
        }

        return results;
    }
}
