using System.Text;
using System.Text.RegularExpressions;

namespace PrinterMate.Catalog;

/// <summary>匹配依据。</summary>
public enum MatchKind
{
    /// <summary>PnP HardwareID / CompatibleID 命中,最可靠。</summary>
    HardwareId,

    /// <summary>型号字符串命中品牌型号模式。</summary>
    ModelPattern,

    /// <summary>仅凭厂商推断到品牌,未命中具体型号。</summary>
    BrandFallback,
}

public enum MatchConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>一条候选匹配结果。</summary>
public sealed record CatalogMatch(
    DriverCatalogEntry Entry,
    MatchKind Kind,
    int Score,
    MatchConfidence Confidence,
    string Reason)
{
    public static MatchConfidence ConfidenceOf(MatchKind kind, int score) => kind switch
    {
        MatchKind.HardwareId => MatchConfidence.High,
        MatchKind.ModelPattern => score >= 560 ? MatchConfidence.High : MatchConfidence.Medium,
        _ => MatchConfidence.Low,
    };
}

/// <summary>匹配引擎的输入:打印机上报的信息。</summary>
public sealed record DeviceMatchInput(
    string? Model,
    string? Vendor,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds)
{
    public static DeviceMatchInput Empty { get; } = new(null, null, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// 型号/硬件 ID → 内置 UPD 的匹配。只做"候选打分",不做分层决策与安装判定
/// (由 Core 的 DriverMatchEngine 负责)。
/// </summary>
public static class CatalogMatcher
{
    private static readonly Dictionary<string, string[]> BrandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hewlett"] = new[] { "HP" },
        ["hewlett-packard"] = new[] { "HP" },
        ["hp"] = new[] { "HP" },
        ["samsung"] = new[] { "Samsung", "HP" },
        ["canon"] = new[] { "Canon" },
        ["epson"] = new[] { "Epson" },
        ["seiko"] = new[] { "Epson" },
        ["brother"] = new[] { "Brother" },
        ["kyocera"] = new[] { "Kyocera" },
        ["ricoh"] = new[] { "Ricoh" },
        ["lanier"] = new[] { "Ricoh" },
        ["savin"] = new[] { "Ricoh" },
        ["lexmark"] = new[] { "Lexmark" },
        ["fuji"] = new[] { "Fujifilm" },
        ["fujifilm"] = new[] { "Fujifilm" },
        ["xerox"] = new[] { "Fujifilm" },
        ["konica"] = new[] { "Konica Minolta" },
        ["minolta"] = new[] { "Konica Minolta" },
        ["sharp"] = new[] { "Sharp" },
        ["toshiba"] = new[] { "Toshiba" },
        ["oki"] = new[] { "OKI" },
        ["pantum"] = new[] { "Pantum", "HP" },
        ["奔图"] = new[] { "Pantum", "HP" },
        ["lenovo"] = new[] { "Lenovo", "HP" },
        ["联想"] = new[] { "Lenovo", "HP" },
        ["deli"] = new[] { "Deli", "HP" },
        ["得力"] = new[] { "Deli", "HP" },
        ["jolimark"] = new[] { "Jolimark" },
        ["映美"] = new[] { "Jolimark" },
    };

    /// <summary>型号串归一化:大写、去括号后缀、压缩空白,保留连字符与空格。</summary>
    public static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(model.Length);
        var depth = 0;
        foreach (var ch in model)
        {
            switch (ch)
            {
                case '(' or '[' or '「' or '【':
                    depth++;
                    continue;
                case ')' or ']' or '」' or '】':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    continue;
            }

            if (depth > 0)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? char.ToUpperInvariant(ch) : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>硬件 ID 归一化:去掉前缀参数与多余空白。</summary>
    public static string NormalizeHardwareId(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return string.Empty;
        }

        var trimmed = hardwareId.Split('\0')[0].Trim();
        return Regex.Replace(trimmed.ToUpperInvariant(), @"\s+", " ");
    }

    /// <summary>大小写无关的通配符匹配(支持 * 与 ?)。</summary>
    public static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>通配符中固定前缀的长度,用于给更具体的模式更高权重。</summary>
    private static int LiteralPrefixLength(string pattern)
    {
        var idx = pattern.IndexOfAny(new[] { '*', '?' });
        return idx < 0 ? pattern.Length : idx;
    }

    /// <summary>把厂商字符串解析成驱动库中的品牌(可能包含贴牌回退品牌)。</summary>
    public static IReadOnlyList<string> ResolveBrands(string? vendor, string? model, DriverLibrary library)
    {
        var known = library.Brands;

        if (!string.IsNullOrWhiteSpace(vendor))
        {
            var normalizedVendor = vendor.Trim();
            foreach (var alias in BrandAliases)
            {
                if (normalizedVendor.Contains(alias.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = alias.Value.Where(b => known.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (resolved.Count > 0)
                    {
                        return resolved;
                    }
                }
            }

            var direct = known.FirstOrDefault(b => normalizedVendor.Contains(b, StringComparison.OrdinalIgnoreCase));
            if (direct is not null)
            {
                return new[] { direct };
            }
        }

        // 厂商缺失时从型号推断品牌
        var normalizedModel = NormalizeModel(model);
        if (normalizedModel.Length > 0)
        {
            foreach (var alias in BrandAliases)
            {
                if (normalizedModel.Contains(alias.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = alias.Value.Where(b => known.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (resolved.Count > 0)
                    {
                        return resolved;
                    }
                }
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>返回按得分降序排列的候选驱动。</summary>
    public static IReadOnlyList<CatalogMatch> Match(
        DriverLibrary library,
        DeviceMatchInput input,
        bool includeClassDrivers = false)
    {
        var entries = library.Entries
            .Where(e => includeClassDrivers || e.Pdl != PrintDescriptionLanguage.ClassDriver)
            .ToList();

        if (entries.Count == 0)
        {
            return Array.Empty<CatalogMatch>();
        }

        var matches = new List<CatalogMatch>();
        var ids = input.HardwareIds.Concat(input.CompatibleIds)
            .Select(NormalizeHardwareId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 1) 硬件 ID 精确匹配
        foreach (var entry in entries)
        {
            foreach (var pattern in entry.HardwareIdPatterns)
            {
                var normalizedPattern = NormalizeHardwareId(pattern).Replace(@"\\", @"\");
                foreach (var id in ids)
                {
                    if (WildcardMatch(id, normalizedPattern))
                    {
                        var score = 1000 + LiteralPrefixLength(normalizedPattern);
                        matches.Add(new CatalogMatch(entry, MatchKind.HardwareId, score, MatchConfidence.High,
                            $"硬件 ID 命中模式 {pattern}(设备上报 {id})"));
                    }
                }
            }
        }

        // 2) 型号模式匹配(按品牌收敛)
        var normalized = NormalizeModel(input.Model);
        var brands = ResolveBrands(input.Vendor, input.Model, library);
        var scoped = brands.Count == 0
            ? entries
            : entries.Where(e => brands.Contains(e.Brand, StringComparer.OrdinalIgnoreCase)).ToList();

        if (normalized.Length > 0)
        {
            foreach (var entry in scoped)
            {
                foreach (var pattern in entry.ModelPatterns)
                {
                    if (pattern == "*")
                    {
                        continue;
                    }

                    var normalizedPattern = NormalizeModel(pattern);
                    if (normalizedPattern.Length == 0)
                    {
                        continue;
                    }

                    if (WildcardMatch(normalized, normalizedPattern))
                    {
                        var score = 600 + LiteralPrefixLength(normalizedPattern);
                        matches.Add(new CatalogMatch(entry, MatchKind.ModelPattern, score, MatchConfidence.High,
                            $"型号 {normalized} 命中模式 {pattern}"));
                    }
                    else if (normalized.Contains(normalizedPattern.TrimEnd('*').Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        var score = 420 + LiteralPrefixLength(normalizedPattern);
                        matches.Add(new CatalogMatch(entry, MatchKind.ModelPattern, score,
                            CatalogMatch.ConfidenceOf(MatchKind.ModelPattern, score),
                            $"型号 {normalized} 包含模式 {pattern}"));
                    }
                }
            }
        }

        // 3) 品牌兜底:只认出品牌,没认出型号
        if (matches.Count == 0 && brands.Count > 0)
        {
            foreach (var entry in entries.Where(e => brands.Contains(e.Brand, StringComparer.OrdinalIgnoreCase)))
            {
                matches.Add(new CatalogMatch(entry, MatchKind.BrandFallback, 200 + Math.Min(entry.Priority, 99),
                    MatchConfidence.Low,
                    $"仅识别出品牌 {entry.Brand},未识别具体型号,建议人工确认候选驱动"));
            }
        }

        // 同一驱动名去重,保留得分最高的一条;同分时 PCL6 优先于 PS
        return matches
            .GroupBy(m => m.Entry.DriverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Entry.Pdl == PrintDescriptionLanguage.PCL6 ? 0 : 1)
                .ThenByDescending(m => m.Entry.Priority)
                .First())
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Pdl == PrintDescriptionLanguage.PCL6 ? 0 : 1)
            .ThenByDescending(m => m.Entry.Priority)
            .ToList();
    }
}
