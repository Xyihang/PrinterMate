using System.Reflection;
using System.Text.Json;

namespace PrinterMate.Security;

/// <summary>下载来源校验结论。</summary>
public enum SourceVerdict
{
    /// <summary>来源通过校验。</summary>
    Allowed,

    /// <summary>不是 https 协议。</summary>
    NotHttps,

    /// <summary>域名不在白名单内。</summary>
    NotAllowlisted,

    /// <summary>URL 无法解析。</summary>
    Malformed,

    /// <summary>域名品牌与期望品牌不一致。</summary>
    VendorMismatch,
}

/// <summary>一次来源校验的结果。</summary>
public sealed record SourceCheckResult(
    SourceVerdict Verdict,
    bool IsAllowed,
    string Reason,
    string MatchedDomain,
    bool IsVendorDomain);

/// <summary>白名单中的厂商官方域名。</summary>
public sealed record VendorDomain(string Brand, string Domain, string Note);

/// <summary>
/// 下载源白名单:仅允许 https,且域名必须命中微软或打印机厂商官方域名。
/// 域名匹配按标签边界进行,<c>evil-hp.com</c> 与 <c>hp.com.evil.io</c> 都不会命中 <c>hp.com</c>。
/// </summary>
public sealed class DownloadSourceAllowlist
{
    private const string ResourceSuffix = "whitelist.json";
    private const string MicrosoftBrand = "Microsoft";

    private readonly List<string> _microsoftDomains;
    private readonly List<VendorDomain> _vendorDomains;

    private static readonly Lazy<DownloadSourceAllowlist> LazyDefault = new(LoadDefault, isThreadSafe: true);

    /// <summary>从嵌入资源 Data/whitelist.json 加载的默认白名单。</summary>
    public static DownloadSourceAllowlist Default => LazyDefault.Value;

    private DownloadSourceAllowlist(List<string> microsoftDomains, List<VendorDomain> vendorDomains)
    {
        _microsoftDomains = microsoftDomains;
        _vendorDomains = vendorDomains;
    }

    public IReadOnlyList<string> MicrosoftDomains => _microsoftDomains;

    public IReadOnlyList<VendorDomain> VendorDomains => _vendorDomains;

    /// <summary>从 JSON 文本加载白名单。JSON 非法时抛出中文异常。</summary>
    public static DownloadSourceAllowlist Load(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("白名单 JSON 为空,无法加载。", nameof(json));
        }

        WhitelistModel? model;
        try
        {
            model = JsonSerializer.Deserialize<WhitelistModel>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"白名单 JSON 解析失败:{ex.Message}", nameof(json), ex);
        }

        if (model is null)
        {
            throw new ArgumentException("白名单 JSON 解析结果为空。", nameof(json));
        }

        var microsoft = new List<string>();
        foreach (var domain in model.MicrosoftDomains ?? new List<string>())
        {
            var normalized = NormalizeDomain(domain);
            if (normalized.Length > 0 && !microsoft.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                microsoft.Add(normalized);
            }
        }

        var vendors = new List<VendorDomain>();
        foreach (var entry in model.VendorDomains ?? new List<VendorDomainModel>())
        {
            var normalized = NormalizeDomain(entry.Domain);
            if (normalized.Length == 0)
            {
                continue;
            }

            var brand = (entry.Brand ?? string.Empty).Trim();
            if (brand.Length == 0)
            {
                throw new ArgumentException($"白名单厂商域名 {entry.Domain} 缺少品牌名称。", nameof(json));
            }

            if (!vendors.Any(v => v.Domain.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                vendors.Add(new VendorDomain(brand, normalized, (entry.Note ?? string.Empty).Trim()));
            }
        }

        if (microsoft.Count == 0 && vendors.Count == 0)
        {
            throw new ArgumentException("白名单中没有任何有效域名。", nameof(json));
        }

        return new DownloadSourceAllowlist(microsoft, vendors);
    }

    /// <summary>校验 URL 字符串。</summary>
    public SourceCheckResult Check(string url, string? expectedBrand = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Reject(SourceVerdict.Malformed, "下载地址为空,无法校验来源。", string.Empty);
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return Reject(SourceVerdict.Malformed, $"下载地址无法解析为绝对 URL:{url}", string.Empty);
        }

        return Check(uri, expectedBrand);
    }

    /// <summary>校验 URI。任何非 https 协议都会被直接拒绝。</summary>
    public SourceCheckResult Check(Uri uri, string? expectedBrand = null)
    {
        if (uri is null)
        {
            return Reject(SourceVerdict.Malformed, "下载地址为空,无法校验来源。", string.Empty);
        }

        if (!uri.IsAbsoluteUri)
        {
            return Reject(SourceVerdict.Malformed, $"下载地址必须是绝对 URL:{uri}", string.Empty);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                SourceVerdict.NotHttps,
                $"仅允许 https 下载,当前协议为 {uri.Scheme}:存在中间人篡改风险,已拒绝。",
                string.Empty);
        }

        var host = NormalizeDomain(uri.Host);
        if (host.Length == 0)
        {
            return Reject(SourceVerdict.Malformed, $"下载地址缺少主机名:{uri}", string.Empty);
        }

        // 微软来源优先(更具体),但不计入厂商域名。
        if (TryMatch(host, _microsoftDomains, out var microsoftMatch))
        {
            if (expectedBrand is not null && !expectedBrand.Trim().Equals(MicrosoftBrand, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceCheckResult(
                    SourceVerdict.VendorMismatch,
                    false,
                    $"下载来源属于微软更新服务,与期望品牌「{expectedBrand}」不一致,已拒绝。",
                    microsoftMatch,
                    false);
            }

            return new SourceCheckResult(
                SourceVerdict.Allowed,
                true,
                $"来源 {microsoftMatch} 属于微软官方更新服务,已通过校验。",
                microsoftMatch,
                false);
        }

        if (TryMatchVendor(host, out var vendor))
        {
            if (expectedBrand is not null
                && !vendor.Brand.Equals(expectedBrand.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new SourceCheckResult(
                    SourceVerdict.VendorMismatch,
                    false,
                    $"下载来源属于「{vendor.Brand}」({vendor.Domain}),与期望品牌「{expectedBrand}」不一致,已拒绝。",
                    vendor.Domain,
                    true);
            }

            return new SourceCheckResult(
                SourceVerdict.Allowed,
                true,
                $"来源 {vendor.Domain} 是「{vendor.Brand}」官方域名,已通过校验。",
                vendor.Domain,
                true);
        }

        return Reject(
            SourceVerdict.NotAllowlisted,
            $"域名 {host} 不在官方下载源白名单内:可能是第三方驱动站,存在捆绑或篡改风险,已拒绝。",
            string.Empty);
    }

    /// <summary>
    /// 域名标签边界匹配:必须完全相等,或为 <c>.*{domain}</c> 形式的子域。
    /// 因此 <c>evil-hp.com</c> 与 <c>hp.com.evil.io</c> 都无法命中 <c>hp.com</c>。
    /// </summary>
    internal static bool DomainMatches(string host, string domain)
    {
        if (host.Length == 0 || domain.Length == 0)
        {
            return false;
        }

        host = NormalizeDomain(host);
        domain = NormalizeDomain(domain);
        if (host.Length == 0 || domain.Length == 0)
        {
            return false;
        }

        if (host.Equals(domain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.Length > domain.Length
               && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryMatch(string host, List<string> domains, out string matched)
    {
        matched = string.Empty;
        foreach (var domain in domains)
        {
            if (DomainMatches(host, domain) && domain.Length > matched.Length)
            {
                matched = domain;
            }
        }

        return matched.Length > 0;
    }

    private bool TryMatchVendor(string host, out VendorDomain vendor)
    {
        vendor = null!;
        var bestLength = -1;
        foreach (var candidate in _vendorDomains)
        {
            if (DomainMatches(host, candidate.Domain) && candidate.Domain.Length > bestLength)
            {
                vendor = candidate;
                bestLength = candidate.Domain.Length;
            }
        }

        return bestLength >= 0;
    }

    private static string NormalizeDomain(string? domain)
        => (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static SourceCheckResult Reject(SourceVerdict verdict, string reason, string matchedDomain)
        => new(verdict, false, reason, matchedDomain, false);

    private static DownloadSourceAllowlist LoadDefault()
    {
        var assembly = typeof(DownloadSourceAllowlist).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                "未找到嵌入的下载源白名单资源 Data/whitelist.json,无法初始化安全策略。");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取嵌入的白名单资源:{resourceName}。");

        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class WhitelistModel
    {
        public List<string>? MicrosoftDomains { get; set; }

        public List<VendorDomainModel>? VendorDomains { get; set; }
    }

    private sealed class VendorDomainModel
    {
        public string? Brand { get; set; }

        public string? Domain { get; set; }

        public string? Note { get; set; }
    }
}
