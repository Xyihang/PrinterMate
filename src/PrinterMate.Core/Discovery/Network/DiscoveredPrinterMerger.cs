using System.ComponentModel;
using System.Net;

namespace PrinterMate.Core.Discovery;

/// <summary>
/// 多来源发现结果去重合并:同一 IP(或同一主机名)只保留一条信息最全的记录。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DiscoveredPrinterMerger
{
    /// <summary>按地址归并,保持首次出现的顺序。</summary>
    public static IReadOnlyList<DiscoveredPrinter> Merge(IEnumerable<DiscoveredPrinter> printers)
    {
        ArgumentNullException.ThrowIfNull(printers);

        var buckets = new Dictionary<string, DiscoveredPrinter>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var printer in printers)
        {
            if (printer is null)
            {
                continue;
            }

            var key = BuildKey(printer);
            if (buckets.TryGetValue(key, out var existing))
            {
                buckets[key] = MergeTwo(existing, printer);
            }
            else
            {
                buckets[key] = printer;
                order.Add(key);
            }
        }

        var results = new List<DiscoveredPrinter>(order.Count);
        foreach (var key in order)
        {
            results.Add(buckets[key]);
        }

        return results;
    }

    /// <summary>计算合并键:优先用 IP,其次用 net: 形式的 Id(去掉端口),最后退回地址文本。</summary>
    public static string BuildKey(DiscoveredPrinter printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        var address = DiscoveryText.NormalizeHost(printer.Address ?? string.Empty);
        if (IPAddress.TryParse(address, out var ip))
        {
            return "ip:" + ip;
        }

        var id = (printer.Id ?? string.Empty).Trim();
        if (id.StartsWith("net:", StringComparison.OrdinalIgnoreCase))
        {
            var host = id[4..];
            var colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                host = host[..colon];
            }

            if (host.Length > 0)
            {
                return "net:" + host.ToLowerInvariant();
            }
        }

        if (id.Length > 0)
        {
            return id.ToLowerInvariant();
        }

        return "addr:" + address.ToLowerInvariant();
    }

    /// <summary>把两条记录合并成一条:以信息更全的一条为基准,缺失字段从另一条补齐。</summary>
    public static DiscoveredPrinter MergeTwo(DiscoveredPrinter left, DiscoveredPrinter right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var (rich, poor) = Completeness(left) >= Completeness(right) ? (left, right) : (right, left);

        return new DiscoveredPrinter
        {
            Id = DiscoveryText.Pick(rich.Id, poor.Id),
            DisplayName = DiscoveryText.Pick("未知打印机", rich.DisplayName, poor.DisplayName),
            Model = DiscoveryText.Pick(string.Empty, rich.Model, poor.Model),
            Vendor = DiscoveryText.Pick(string.Empty, rich.Vendor, poor.Vendor),
            Connection = rich.Connection != ConnectionKind.Unknown ? rich.Connection : poor.Connection,
            Address = DiscoveryText.Pick(string.Empty, rich.Address, poor.Address),
            PortName = DiscoveryText.Pick(string.Empty, rich.PortName, poor.PortName),
            ExistingQueueName = DiscoveryText.NullIfBlank(rich.ExistingQueueName) ?? DiscoveryText.NullIfBlank(poor.ExistingQueueName),
            ExistingDriverName = DiscoveryText.NullIfBlank(rich.ExistingDriverName) ?? DiscoveryText.NullIfBlank(poor.ExistingDriverName),
            IppUri = DiscoveryText.NullIfBlank(rich.IppUri) ?? DiscoveryText.NullIfBlank(poor.IppUri),
            HardwareIds = Union(rich.HardwareIds, poor.HardwareIds),
            CompatibleIds = Union(rich.CompatibleIds, poor.CompatibleIds),
            IsMopria = rich.IsMopria || poor.IsMopria,
            SupportsIpp = rich.SupportsIpp || poor.SupportsIpp,
            SupportsRaw9100 = rich.SupportsRaw9100 || poor.SupportsRaw9100,
            SupportsLpd = rich.SupportsLpd || poor.SupportsLpd,
            Readiness = ReadinessRank(rich.Readiness) >= ReadinessRank(poor.Readiness) ? rich.Readiness : poor.Readiness,
            DiscoverySource = DiscoveryText.MergeSources(rich.DiscoverySource, poor.DiscoverySource),
            Notes = DiscoveryText.Pick(string.Empty, rich.Notes, poor.Notes),
        };
    }

    /// <summary>信息完整度打分,用于决定合并时的基准记录。</summary>
    public static int Completeness(DiscoveredPrinter printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        var score = 0;
        if (!string.IsNullOrWhiteSpace(printer.Model))
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(printer.Vendor))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(printer.IppUri))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(printer.Notes))
        {
            score += 1;
        }

        if (printer.IsMopria)
        {
            score += 2;
        }

        if (printer.SupportsIpp)
        {
            score += 1;
        }

        if (printer.SupportsRaw9100)
        {
            score += 1;
        }

        if (printer.SupportsLpd)
        {
            score += 1;
        }

        if (printer.HardwareIds.Count > 0)
        {
            score += 1;
        }

        return score;
    }

    private static int ReadinessRank(DriverReadiness readiness) => readiness switch
    {
        DriverReadiness.Installed => 3,
        DriverReadiness.DriverFree => 2,
        DriverReadiness.NeedsDriver => 1,
        _ => 0,
    };

    private static IReadOnlyList<string> Union(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var results = new List<string>();
        foreach (var value in left.Concat(right))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(trimmed);
            }
        }

        return results;
    }
}
