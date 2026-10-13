using System.Net;

namespace PrinterMate.Core.Discovery;

/// <summary>
/// SNMP v2c 广播(端口 161)。读 sysDescr(1.3.6.1.2.1.1.1.0)、sysName(1.3.6.1.2.1.1.5.0)、
/// hrDeviceDescr(1.3.6.1.2.1.25.3.2.1.3.1)、prtGeneralPrinterName(1.3.6.1.2.1.43.5.1.1.16.1)。
/// </summary>
public sealed class SnmpDiscovery : INetworkDiscovery
{
    private const int SnmpPort = 161;

    private readonly string _community;

    public SnmpDiscovery(string community = "public")
    {
        _community = string.IsNullOrWhiteSpace(community) ? "public" : community.Trim();
    }

    /// <inheritdoc />
    public string Name => "SNMP 广播";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct)
    {
        var results = new List<DiscoveredPrinter>();
        if (options is null || !options.EnableSnmp)
        {
            return results;
        }

        try
        {
            var requestId = Random.Shared.Next(1, int.MaxValue);
            var payload = SnmpCodec.EncodeGetRequest(requestId, _community, SnmpCodec.PrinterOids);
            var targets = BuildTargets(options);
            var timeout = options.ProviderTimeout(options.EffectiveTimeout);

            await DatagramTransport.SendAndReceiveAsync(
                payload,
                targets,
                bindTo: null,
                multicastGroup: null,
                timeout,
                (datagram, remote) =>
                {
                    if (!SnmpCodec.TryParseMessage(datagram, out var message) || message is null)
                    {
                        return;
                    }

                    if (message.PduTag != SnmpCodec.TagGetResponse || message.RequestId != requestId || message.ErrorStatus != 0)
                    {
                        return;
                    }

                    var printer = MapMessage(message, remote.Address.ToString());
                    if (printer is not null)
                    {
                        lock (results)
                        {
                            results.Add(printer);
                        }
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 无网卡 / 广播被拒 / 无响应:返回已收集到的结果。
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 每次发现自建套接字,无需释放长期资源。
    }

    /// <summary>一次 GetRequest 的广播目标:指定网段 + 本机定向广播地址 + 受限广播 255.255.255.255。</summary>
    public static IReadOnlyList<IPEndPoint> BuildTargets(DiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var addresses = new List<IPAddress>();
        if (options.Subnets is { Count: > 0 })
        {
            addresses.AddRange(options.Subnets);
        }
        else
        {
            addresses.AddRange(NetworkAddresses.GetLocalBroadcastAddresses());
        }

        addresses.Add(IPAddress.Broadcast);

        var targets = new List<IPEndPoint>();
        foreach (var address in addresses)
        {
            if (address is null)
            {
                continue;
            }

            var endpoint = new IPEndPoint(address, SnmpPort);
            if (!targets.Contains(endpoint))
            {
                targets.Add(endpoint);
            }
        }

        return targets;
    }

    /// <summary>
    /// 把一条 SNMP 响应映射成设备记录。只有拿到打印机专有 OID 或能识别出品牌时才认为它是打印机。
    /// </summary>
    public static DiscoveredPrinter? MapMessage(SnmpMessage message, string sourceAddress)
    {
        ArgumentNullException.ThrowIfNull(message);

        var sysDescr = message.SysDescr;
        var hrDeviceDescr = message.HrDeviceDescr;
        var printerName = message.PrinterName;
        var sysName = message.SysName;

        var brand = PrinterBrandParser.IdentifyFrom(hrDeviceDescr, sysDescr);
        var hasPrinterOid = !string.IsNullOrWhiteSpace(hrDeviceDescr) || !string.IsNullOrWhiteSpace(printerName);

        if (!hasPrinterOid && brand is null)
        {
            // 能应答 SNMP 但不是打印机(交换机/路由器等)。
            return null;
        }

        var host = DiscoveryText.NormalizeAddress(sourceAddress);
        var description = DiscoveryText.Pick(string.Empty, hrDeviceDescr, sysDescr);
        var model = PrinterBrandParser.ExtractModel(description);
        var displayName = DiscoveryText.Pick(
            "网络打印机",
            printerName,
            sysName,
            model.Length > 0 ? model : null,
            brand);

        return new DiscoveredPrinter
        {
            Id = DiscoveryText.BuildNetworkId(host),
            DisplayName = displayName,
            Model = model,
            Vendor = brand ?? string.Empty,
            Connection = ConnectionKind.Unknown,
            Address = host,
            DiscoverySource = "SNMP",
            Readiness = DriverReadiness.Unknown,
            Notes = description,
        };
    }
}
