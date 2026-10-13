using System.Net;
using System.Net.Sockets;

namespace PrinterMate.Core.Discovery;

/// <summary>mDNS/DNS-SD 组播发现 _ipp._tcp.local 与 _printer._tcp.local(IPv4 组播 224.0.0.251:5353)。</summary>
public sealed class MdnsDiscovery : INetworkDiscovery
{
    /// <summary>IPP 服务类型。</summary>
    public const string IppService = "_ipp._tcp.local";

    /// <summary>老式 LPD 打印服务类型。</summary>
    public const string PrinterService = "_printer._tcp.local";

    private const int MdnsPort = 5353;

    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    /// <inheritdoc />
    public string Name => "mDNS / DNS-SD";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct)
    {
        var results = new List<DiscoveredPrinter>();
        if (options is null || !options.EnableMdns)
        {
            return results;
        }

        try
        {
            var query = DnsWire.EncodeQuery(
                0,
                [
                    new DnsQuestion(IppService, DnsWire.TypePTR, DnsWire.ClassIN),
                    new DnsQuestion(PrinterService, DnsWire.TypePTR, DnsWire.ClassIN),
                ]);

            var records = new List<DnsResourceRecord>();
            var timeout = options.ProviderTimeout(options.EffectiveTimeout);
            var targets = new List<IPEndPoint> { new(MdnsGroup, MdnsPort) };

            await DatagramTransport.SendAndReceiveAsync(
                query,
                targets,
                bindTo: new IPEndPoint(IPAddress.Any, MdnsPort),
                multicastGroup: MdnsGroup,
                timeout,
                (datagram, _) =>
                {
                    if (!DnsWire.TryParse(datagram, out var message) || message is null || !message.IsResponse)
                    {
                        return;
                    }

                    lock (records)
                    {
                        records.AddRange(message.AllRecords);
                    }
                },
                ct).ConfigureAwait(false);

            results.AddRange(MapRecords(records));
        }
        catch (Exception)
        {
            // 无网卡 / 5353 被占用 / 无响应:返回空列表。
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 每次发现自建套接字,无需释放长期资源。
    }

    /// <summary>把 DNS-SD 资源记录集合汇编成设备记录。</summary>
    public static IReadOnlyList<DiscoveredPrinter> MapRecords(IEnumerable<DnsResourceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var all = records.ToList();
        var byName = new Dictionary<string, List<DnsResourceRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in all)
        {
            if (!byName.TryGetValue(record.Name, out var list))
            {
                list = [];
                byName[record.Name] = list;
            }

            list.Add(record);
        }

        var results = new List<DiscoveredPrinter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ptr in all)
        {
            if (ptr.Type != DnsWire.TypePTR || string.IsNullOrWhiteSpace(ptr.DomainName))
            {
                continue;
            }

            var instance = ptr.DomainName!;
            if (!IsPrintService(ptr.Name) || !seen.Add(instance))
            {
                continue;
            }

            var instanceRecords = byName.TryGetValue(instance, out var list) ? list : [];
            var srv = instanceRecords.FirstOrDefault(r => r.Type == DnsWire.TypeSRV);
            var txt = instanceRecords.FirstOrDefault(r => r.Type == DnsWire.TypeTXT);

            IPAddress? address = null;
            if (!string.IsNullOrWhiteSpace(srv?.Target) && byName.TryGetValue(srv!.Target!, out var hostRecords))
            {
                address = hostRecords.FirstOrDefault(r => r.Type == DnsWire.TypeA)?.Address
                          ?? hostRecords.FirstOrDefault(r => r.Type == DnsWire.TypeAAAA)?.Address;
            }

            results.Add(MapInstance(instance, ptr.Name, srv, txt, address));
        }

        return results;
    }

    /// <summary>把单个 DNS-SD 服务实例映射成设备记录。</summary>
    public static DiscoveredPrinter MapInstance(
        string instanceName,
        string serviceType,
        DnsResourceRecord? srv,
        DnsResourceRecord? txt,
        IPAddress? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var properties = ParseTxt(txt);
        var host = address?.ToString() ?? DiscoveryText.NullIfBlank(srv?.Target) ?? instanceName;

        var label = UnescapeLabel(instanceName.Split('.')[0]);
        var model = DiscoveryText.Pick(string.Empty, Get(properties, "usb_MDL"), Get(properties, "usb_mdl"), Get(properties, "ty"));
        var vendor = DiscoveryText.Pick(
            string.Empty,
            Get(properties, "usb_MFG"),
            Get(properties, "usb_mfg"),
            Get(properties, "mfg"),
            PrinterBrandParser.IdentifyFrom(Get(properties, "ty"), label));
        var note = Get(properties, "note");

        var isIppService = serviceType.Contains("_ipp._tcp", StringComparison.OrdinalIgnoreCase);
        var port = srv?.Port is > 0 ? srv.Port!.Value : isIppService ? 631 : 515;

        var resource = DiscoveryText.NullIfBlank(Get(properties, "rp")) ?? DiscoveryText.NullIfBlank(Get(properties, "path"));
        if (resource is not null)
        {
            resource = resource.TrimStart('/');
        }

        var ippUri = isIppService
            ? $"ipp://{host}:{port}/{resource ?? "ipp/print"}"
            : null;

        var pdl = Get(properties, "pdl") ?? string.Empty;
        var hasUrf = properties.ContainsKey("URF");
        var hasPrivatePdl = pdl.Contains("application/vnd.", StringComparison.OrdinalIgnoreCase) ||
                            pdl.Contains("pclm", StringComparison.OrdinalIgnoreCase);
        var isMopria = isIppService && hasUrf && !hasPrivatePdl;

        return new DiscoveredPrinter
        {
            Id = DiscoveryText.BuildNetworkId(host),
            DisplayName = DiscoveryText.Pick(label, note, model),
            Model = model,
            Vendor = vendor,
            Connection = isIppService ? ConnectionKind.Ipp : ConnectionKind.Lpd,
            Address = host,
            PortName = string.Empty,
            IppUri = ippUri,
            IsMopria = isMopria,
            SupportsIpp = isIppService,
            SupportsRaw9100 = false,
            SupportsLpd = !isIppService,
            Readiness = isMopria ? DriverReadiness.DriverFree : DriverReadiness.NeedsDriver,
            DiscoverySource = "mDNS",
            Notes = DiscoveryText.Pick(string.Empty, note, Get(properties, "ty")),
        };
    }

    /// <summary>解析 mDNS TXT 记录为键值字典(键大小写不敏感,无 “=” 的条目视为空值标志位)。</summary>
    public static IReadOnlyDictionary<string, string> ParseTxt(DnsResourceRecord? txt)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (txt?.Text is null)
        {
            return values;
        }

        foreach (var entry in txt.Text)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var separator = entry.IndexOf('=');
            if (separator < 0)
            {
                values[entry] = string.Empty;
            }
            else
            {
                values[entry[..separator].Trim()] = entry[(separator + 1)..];
            }
        }

        return values;
    }

    private static bool IsPrintService(string serviceName) =>
        serviceName.Contains("_ipp._tcp", StringComparison.OrdinalIgnoreCase) ||
        serviceName.Contains("_printer._tcp", StringComparison.OrdinalIgnoreCase);

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? DiscoveryText.NullIfBlank(value) : null;

    private static string UnescapeLabel(string label)
    {
        if (!label.Contains('\\', StringComparison.Ordinal))
        {
            return label;
        }

        var builder = new System.Text.StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] == '\\' && i + 1 < label.Length)
            {
                i++;
            }

            builder.Append(label[i]);
        }

        return builder.ToString();
    }
}
