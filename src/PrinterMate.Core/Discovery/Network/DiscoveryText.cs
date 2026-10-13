using System.ComponentModel;
using System.Net;

namespace PrinterMate.Core.Discovery;

/// <summary>发现模块内部的字符串/主机名处理工具。对用户不可见,仅为可测性公开。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DiscoveryText
{
    /// <summary>返回第一个非空白字符串;全部为空时返回 <paramref name="fallback"/>。</summary>
    public static string Pick(string? fallback, params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback ?? string.Empty;
    }

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>去掉 IPv6 方括号与前后空白,得到可用于 TcpClient/DNS 的主机部分。</summary>
    public static string NormalizeHost(string hostOrIp)
    {
        var host = (hostOrIp ?? string.Empty).Trim();
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host;
    }

    /// <summary>生成稳定的设备 Id:有 IP 时用 ip,否则退回到主机名。</summary>
    public static string BuildNetworkId(string hostOrIp)
    {
        var host = NormalizeHost(hostOrIp);
        return "net:" + host.ToLowerInvariant();
    }

    /// <summary>把同一台设备的多来源标注合并为 "SNMP+mDNS" 形式。</summary>
    public static string MergeSources(string? left, string? right)
    {
        var parts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in new[] { left, right })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var piece in source.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                parts.Add(piece);
            }
        }

        return string.Join('+', parts);
    }

    /// <summary>把 IP 地址规范化为小写字符串;失败时返回去空白的原始文本。</summary>
    public static string NormalizeAddress(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        return IPAddress.TryParse(text, out var ip) ? ip.ToString() : text.ToLowerInvariant();
    }
}
