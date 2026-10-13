using System.Text.RegularExpressions;
using PrinterMate.Catalog;

namespace PrinterMate.Core.Discovery;

/// <summary>从设备名称/端口名中解析厂商、型号与连接方式。</summary>
public static class DeviceNameParser
{
    private static readonly Regex ParenthesizedSuffix = new(@"\s*[\(\[][^)\]]*[\)\]]\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> VendorKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hewlett-packard"] = "HP",
        ["hewlett"] = "HP",
        ["hp"] = "HP",
        ["samsung"] = "Samsung",
        ["canon"] = "Canon",
        ["epson"] = "Epson",
        ["brother"] = "Brother",
        ["kyocera"] = "Kyocera",
        ["ricoh"] = "Ricoh",
        ["lanier"] = "Ricoh",
        ["savin"] = "Ricoh",
        ["lexmark"] = "Lexmark",
        ["fuji"] = "Fujifilm",
        ["xerox"] = "Fujifilm",
        ["konica"] = "Konica Minolta",
        ["minolta"] = "Konica Minolta",
        ["sharp"] = "Sharp",
        ["toshiba"] = "Toshiba",
        ["oki"] = "OKI",
        ["pantum"] = "Pantum",
        ["奔图"] = "Pantum",
        ["lenovo"] = "Lenovo",
        ["联想"] = "Lenovo",
        ["deli"] = "Deli",
        ["得力"] = "Deli",
        ["jolimark"] = "Jolimark",
        ["映美"] = "Jolimark",
        ["microsoft"] = "Microsoft",
    };

    /// <summary>去掉尾部括号信息与常见噪声词,得到干净的型号展示名。</summary>
    public static string CleanModelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();
        for (var i = 0; i < 3 && ParenthesizedSuffix.IsMatch(text); i++)
        {
            text = ParenthesizedSuffix.Replace(text, string.Empty).Trim();
        }

        foreach (var noise in new[] { " Series", " series", " Printer", " 打印机", "(USB)" })
        {
            text = text.Replace(noise, string.Empty, StringComparison.Ordinal);
        }

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>从名称/厂商字段推断品牌(仅字符串规则,不查驱动库)。</summary>
    public static string GuessVendor(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var text = candidate.Trim();
            var tokens = Regex.Split(text, @"[^\p{L}\p{N}]+")
                .Where(t => t.Length > 0)
                .ToArray();

            foreach (var keyword in VendorKeywords)
            {
                // 中日韩别名没有词边界问题,直接子串匹配;
                // 拉丁短别名(如 hp、oki)只做整词匹配,避免 "Sharp" 命中 "hp"、"Php" 之类误判;
                // 长别名(如 hewlett、kyocera)才允许子串匹配。
                var hasCjk = keyword.Key.Any(c => c >= '\u2e80');
                var matched = hasCjk || keyword.Key.Length >= 4
                    ? text.Contains(keyword.Key, StringComparison.OrdinalIgnoreCase)
                    : tokens.Any(t => t.Equals(keyword.Key, StringComparison.OrdinalIgnoreCase));

                if (matched)
                {
                    return keyword.Value;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>根据端口名判断连接方式。</summary>
    public static ConnectionKind GuessConnection(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return ConnectionKind.Unknown;
        }

        var port = portName.Trim();

        if (port.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ConnectionKind.Shared;
        }

        if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Usb;
        }

        if (port.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Wsd;
        }

        if (port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.LocalQueue;
        }

        if (port.StartsWith("ipp", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.Ipp;
        }

        if (port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) || IsIpAddress(port))
        {
            return ConnectionKind.RawTcp;
        }

        if (port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || port.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionKind.LocalQueue;
        }

        // 形如 "192.168.1.20:9100" 或 "192.168.1.20_1"
        var head = port.Split('_', ':', '/')[0];
        return IsIpAddress(head) ? ConnectionKind.RawTcp : ConnectionKind.Unknown;
    }

    /// <summary>从端口名中提取 IP 地址(若有)。</summary>
    public static string? ExtractAddress(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        foreach (var token in portName.Split('_', ':', '/', '\\'))
        {
            var candidate = token.Trim();
            if (IsIpAddress(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsIpAddress(string? text)
        => !string.IsNullOrWhiteSpace(text) && System.Net.IPAddress.TryParse(text.Trim(), out _);

    /// <summary>把厂商 + 型号拼成用于匹配引擎的型号字符串。</summary>
    public static string BuildModelForMatching(string? vendor, string? model)
    {
        var cleanModel = CleanModelName(model);
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return cleanModel;
        }

        return cleanModel.Contains(vendor, StringComparison.OrdinalIgnoreCase)
            ? cleanModel
            : $"{vendor} {cleanModel}".Trim();
    }
}
