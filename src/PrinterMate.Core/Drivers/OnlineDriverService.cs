using System.Text.Json;
using System.Text.RegularExpressions;
using PrinterMate.Core.Diagnostics;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Security;

namespace PrinterMate.Core.Drivers;

/// <summary>在线检索到的驱动候选。</summary>
public sealed record OnlineDriverCandidate(
    string DisplayName,
    string Brand,
    string Version,
    string Size,
    string Url,
    string SourceName,
    bool IsDirectDownload,
    string Note)
{
    public override string ToString() => $"{DisplayName} ({SourceName})";
}

/// <summary>
/// L2 在线补齐通道。
///
/// 安全约束(不可妥协):
///   - 只从白名单 HTTPS 域名检索与下载(Microsoft Update Catalog + 厂商官方域名);
///   - 下载后必须通过 SHA256 + Authenticode 校验(SecureDownloader 内部完成);
///   - 检索不到可用包时,**给出中文原因与官方下载页**,绝不静默失败、绝不引导到第三方驱动站。
/// </summary>
public sealed class OnlineDriverService
{
    /// <summary>各品牌官方驱动支持页(HTTPS)。检索失败时作为兜底指引。</summary>
    private static readonly Dictionary<string, string> OfficialDriverPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HP"] = "https://support.hp.com/cn-zh/drivers/printers",
        ["Samsung"] = "https://support.hp.com/cn-zh/drivers/printers",
        ["Canon"] = "https://www.canon.com.cn/supports/download/",
        ["Epson"] = "https://www.epson.com.cn/tech_support/",
        ["Brother"] = "https://www.brother.cn/support",
        ["Kyocera"] = "https://www.kyoceradocumentsolutions.com.cn/support/",
        ["Ricoh"] = "https://www.ricoh.com.cn/support/",
        ["Lexmark"] = "https://www.lexmark.com/zh_CN/support.html",
        ["Fujifilm"] = "https://www.fujifilm.com/fbcn/zh/support",
        ["Konica Minolta"] = "https://www.konicaminolta.com.cn/support/",
        ["Sharp"] = "https://www.sharp.cn/support/",
        ["Toshiba"] = "https://www.toshibatec.com.cn/support/",
        ["OKI"] = "https://www.oki.com/cn/printing/support/",
        ["Pantum"] = "https://www.pantum.cn/support/download/",
        ["Lenovo"] = "https://support.lenovo.com/cn/zh/",
        ["Deli"] = "https://www.nbdeli.com/",
        ["Jolimark"] = "https://www.jolimark.com/",
    };

    private readonly SecureDownloader _downloader;
    private readonly AuditLog _audit;
    private readonly MicrosoftUpdateCatalogClient _catalog;
    private readonly HttpClient _http;

    public OnlineDriverService(
        SecureDownloader? downloader = null,
        AuditLog? audit = null,
        MicrosoftUpdateCatalogClient? catalog = null,
        HttpClient? http = null)
    {
        _audit = audit ?? new AuditLog();
        _downloader = downloader ?? new SecureDownloader(audit: _audit);
        _catalog = catalog ?? new MicrosoftUpdateCatalogClient(audit: _audit);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>按品牌/型号检索白名单来源中的驱动。</summary>
    public async Task<OperationResult<IReadOnlyList<OnlineDriverCandidate>>> SearchAsync(
        string brand,
        string? model,
        CancellationToken ct = default)
    {
        var results = new List<OnlineDriverCandidate>();
        var query = BuildQuery(brand, model);

        try
        {
            var entries = await _catalog.SearchAsync(query, ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (!_catalog.LooksLikeDriver(entry))
                {
                    continue;
                }

                results.Add(new OnlineDriverCandidate(
                    entry.Title,
                    brand,
                    string.IsNullOrWhiteSpace(entry.Version) ? "(未标注)" : entry.Version,
                    string.IsNullOrWhiteSpace(entry.Size) ? "(未知)" : entry.Size,
                    entry.UpdateId,
                    "Microsoft Update Catalog",
                    IsDirectDownload: false,
                    $"分类:{entry.Classification};适用系统:{entry.Products};更新时间:{entry.LastUpdated}。下载前会解析官方直链并校验签名。"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _audit.Write("在线检索-失败", query, "失败", ex.Message);
        }

        if (results.Count == 0)
        {
            var page = OfficialDriverPages.TryGetValue(brand, out var url) ? url : string.Empty;
            var suggestion = page.Length > 0
                ? $"可前往 {brand} 官方驱动页手动下载:{page}"
                : "请在厂商官网查找该型号驱动,或继续使用内置驱动库中同品牌的其他通用驱动。";

            _audit.Write("在线检索-无结果", query, "未命中", suggestion);

            return OperationResult<IReadOnlyList<OnlineDriverCandidate>>.Fail(
                $"在 Microsoft Update Catalog 中未检索到「{query}」对应的可用驱动包。",
                suggestion,
                OperationStatus.NeedsUserAction);
        }

        _audit.Write("在线检索-成功", query, "成功", $"命中 {results.Count} 条候选。");
        return OperationResult<IReadOnlyList<OnlineDriverCandidate>>.Ok(results, $"共检索到 {results.Count} 个候选驱动包。");
    }

    /// <summary>下载候选驱动到指定目录并完成三重校验,返回本地文件路径。</summary>
    public async Task<OperationResult<string>> DownloadAsync(
        OnlineDriverCandidate candidate,
        string destinationDirectory,
        string? expectedSignerSubject = null,
        IProgress<DownloadCheckpoint>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);

            var url = candidate.Url;
            if (!candidate.IsDirectDownload)
            {
                var resolved = await _catalog.ResolveDownloadUrlAsync(candidate, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    return OperationResult<string>.Fail(
                        "无法从 Microsoft Update Catalog 解析出官方直链。",
                        "请打开 Microsoft Update Catalog 手动下载后,在「驱动管理」中导入本地驱动包。",
                        OperationStatus.NeedsUserAction);
                }

                url = resolved;
            }

            var fileName = BuildFileName(candidate, url);
            var destination = Path.Combine(destinationDirectory, fileName);

            var result = await _downloader
                .DownloadAsync(url, destination, expectedSha256: null, expectedSignerSubject, candidate.Brand, progress, ct)
                .ConfigureAwait(false);

            return OperationResult<string>.Ok(destination, $"驱动包已下载并通过校验({result.Size} 字节)。");
        }
        catch (SecurityPolicyException ex)
        {
            _audit.Write("在线下载-拒绝", candidate.Url, "拒绝", $"{ex.Reason} {ex.Suggestion}");
            return OperationResult<string>.Fail($"下载被安全策略拒绝:{ex.Reason}", ex.Suggestion, OperationStatus.NotPermitted);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<string>.Fail("下载已取消。", null, OperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _audit.Write("在线下载-失败", candidate.Url, "失败", ex.Message);
            return ex.ToFailure<string>("下载驱动包");
        }
    }

    /// <summary>解压驱动包(CAB / ZIP / 自解压 EXE / 裸 INF),返回其中的 INF 文件路径。</summary>
    public OperationResult<string> Extract(string packagePath, string? extractDirectory = null)
    {
        try
        {
            var extension = Path.GetExtension(packagePath).ToLowerInvariant();

            if (extension == ".inf")
            {
                return OperationResult<string>.Ok(packagePath, "驱动包本身就是 INF 文件。");
            }

            var target = extractDirectory ?? Path.Combine(Path.GetDirectoryName(packagePath)!, Path.GetFileNameWithoutExtension(packagePath) + "_extracted");
            Directory.CreateDirectory(target);

            if (extension is ".zip")
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, target, overwriteFiles: true);
            }
            else if (extension is ".cab" or ".exe" or ".msi")
            {
                // 只使用"不执行"的解包工具,绝不盲猜开关去运行厂商安装器 ——
                // 那等于让安装器直接改系统并绕过签名与哈希校验。
                var sevenZip = FindSevenZip();
                if (sevenZip is not null)
                {
                    ProcessRunner.Run(sevenZip, new[] { "x", "-y", $"-o{target}", packagePath }, TimeSpan.FromMinutes(3));
                }

                if (!HasInf(target))
                {
                    var expand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
                    ProcessRunner.Run(expand, new[] { "-F:*", packagePath, target }, TimeSpan.FromMinutes(2));
                }

                if (!HasInf(target))
                {
                    var what = extension == ".cab" ? "CAB" : "自解压安装程序";
                    return OperationResult<string>.Fail(
                        $"无法在不运行安装器的前提下从该{what}中提取 INF 文件。",
                        "请安装 7-Zip 后重试,或手工解压后把文件夹内容导入「驱动管理」;"
                        + "也可以直接从厂商官网下载包含 INF/CAT 的驱动包。",
                        OperationStatus.NotSupported);
                }
            }
            else
            {
                return OperationResult<string>.Fail($"不支持的驱动包格式:{extension}", "支持 .cab / .zip / .exe / .msi / .inf。");
            }

            var inf = Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories)
                .OrderBy(f => f.Contains("ntamd64", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return inf is null
                ? OperationResult<string>.Fail("解压后未找到 INF 文件。", "该压缩包可能不是驱动包,请确认来源。")
                : OperationResult<string>.Ok(inf, $"已解压到 {target}");
        }
        catch (Exception ex)
        {
            return ex.ToFailure<string>("解压驱动包");
        }
    }

    /// <summary>
    /// 构造检索词。MUC 是按短语匹配的:
    ///   - 不要附加 "printer driver" 之类后缀,否则会直接查不到;
    ///   - 型号里已经含品牌时不要再拼一次,避免出现「HP HP Deskjet …」。
    /// </summary>
    public static string BuildQuery(string brand, string? model)
    {
        var b = (brand ?? string.Empty).Trim();
        var m = (model ?? string.Empty).Trim();

        if (m.Length == 0)
        {
            return b;
        }

        if (b.Length == 0 || m.Contains(b, StringComparison.OrdinalIgnoreCase))
        {
            return m;
        }

        return $"{b} {m}";
    }

    /// <summary>
    /// 构造 Microsoft Update Catalog 的搜索页地址(用于给用户人工兜底)。
    /// </summary>
    public static string BuildCatalogSearchUrl(string brand, string? model)
    {
        var query = BuildQuery(brand, model);
        if (string.IsNullOrWhiteSpace(query))
        {
            query = "printer driver";
        }

        return "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(query);
    }

    /// <summary>查找 7-Zip(可选依赖,只用于解包,不用于执行任何安装器)。</summary>
    private static string? FindSevenZip()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool HasInf(string directory)
        => Directory.Exists(directory)
           && Directory.EnumerateFiles(directory, "*.inf", SearchOption.AllDirectories).Any();

    private static string BuildFileName(OnlineDriverCandidate candidate, string url)
    {
        var fromUrl = Path.GetFileName(new Uri(url).AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.'))
        {
            return fromUrl;
        }

        var safe = Regex.Replace(candidate.DisplayName, @"[^\w\-\.]+", "_");
        return safe.Length > 80 ? safe[..80] + ".cab" : safe + ".cab";
    }
}

/// <summary>Microsoft Update Catalog 更新条目。</summary>
public sealed record UpdateCatalogEntry(
    string UpdateId,
    string Title,
    string Products,
    string Classification,
    string Version,
    string Size,
    string LastUpdated)
{
    public bool IsDriverEntry => Classification.Contains("Driver", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Microsoft Update Catalog 客户端(https://catalog.update.microsoft.com)。
/// 仅用于检索与解析官方直链,真正的下载由 SecureDownloader 执行白名单与签名校验。
/// </summary>
public sealed class MicrosoftUpdateCatalogClient
{
    private const string SearchEndpoint = "https://www.catalog.update.microsoft.com/Search.aspx?q=";
    private const string DownloadDialogEndpoint = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx";

    // 真实页面结构(2026-10 实测):
    //   <tr id="{GUID}_R{m}" ...>                       ← R{m} 是**行号**(R0/R1/R2…),不是常量
    //     <td id="{GUID}_C0_R{m}">…</td>                 勾选框列
    //     <td id="{GUID}_C1_R{m}"><a id='{GUID}_link' …>标题</a></td>
    //     <td id="{GUID}_C2_R{m}">Products</td>
    //     <td id="{GUID}_C3_R{m}">Drivers (Other Hardware)</td>   分类
    //     <td id="{GUID}_C4_R{m}">11/9/2009</td>                   更新时间
    //     <td id="{GUID}_C5_R{m}">n/a</td>                         版本
    //     <td id="{GUID}_C6_R{m}"><span id="{GUID}_size">6.7 MB</span></td>
    //   </tr>
    // 注意:早期实现把行号写死成 _R0,导致整页 14 条只解析出 1 条。
    private static readonly Regex RowRegex = new(
        @"(?is)<tr[^>]*id=""(?<guid>[0-9a-fA-F\-]{36})_R\d+""[^>]*>(?<body>.*?)</tr>",
        RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"(?is)id='[0-9a-fA-F\-]{36}_link'[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex CellRegex = new(
        @"(?is)id=""[0-9a-fA-F\-]{36}_C(?<n>\d+)_R\d+""[^>]*>(?<value>.*?)</td>",
        RegexOptions.Compiled);

    private static readonly Regex SizeRegex = new(
        @"(?is)id=""[0-9a-fA-F\-]{36}_size""[^>]*>(?<size>.*?)</span>",
        RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"(?<url>https?://[^'""\s\\]+\.(?:cab|msi|exe|zip))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>仅在分类列缺失时兜底使用的标题关键词。</summary>
    private static readonly Regex DriverKeywordRegex = new(
        @"(driver|打印|printer|print|pcl|postscript|\bps\b|universal)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly AuditLog _audit;

    public MicrosoftUpdateCatalogClient(HttpClient? http = null, AuditLog? audit = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _audit = audit ?? new AuditLog();
    }

    /// <summary>
    /// 判断一条更新是不是驱动。
    /// 优先看分类列(实测值形如「Drivers (Other Hardware)」「Drivers (Printers)」),
    /// 只有分类为空时才退回标题关键词。
    /// </summary>
    public bool LooksLikeDriver(UpdateCatalogEntry entry)
        => string.IsNullOrWhiteSpace(entry.Classification)
            ? DriverKeywordRegex.IsMatch(entry.Title)
            : entry.IsDriverEntry;

    public async Task<IReadOnlyList<UpdateCatalogEntry>> SearchAsync(string query, CancellationToken ct)
    {
        var url = SearchEndpoint + Uri.EscapeDataString(query);
        var html = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        var entries = new List<UpdateCatalogEntry>();
        foreach (Match match in RowRegex.Matches(html))
        {
            var body = match.Groups["body"].Value;

            var title = StripHtml(TitleRegex.Match(body).Groups["title"].Value);
            if (title.Length == 0)
            {
                continue;
            }

            var cells = new Dictionary<int, string>();
            foreach (Match cell in CellRegex.Matches(body))
            {
                if (int.TryParse(cell.Groups["n"].Value, out var index))
                {
                    cells[index] = StripHtml(cell.Groups["value"].Value);
                }
            }

            var size = StripHtml(SizeRegex.Match(body).Groups["size"].Value);

            entries.Add(new UpdateCatalogEntry(
                UpdateId: match.Groups["guid"].Value,
                Title: title,
                Products: cells.GetValueOrDefault(2, string.Empty),
                Classification: cells.GetValueOrDefault(3, string.Empty),
                LastUpdated: cells.GetValueOrDefault(4, string.Empty),
                Version: cells.GetValueOrDefault(5, string.Empty),
                Size: size.Length > 0 ? size : cells.GetValueOrDefault(6, string.Empty)));
        }

        _audit.Write("在线检索-MUC", query, entries.Count > 0 ? "成功" : "未命中",
            $"页面解析出 {entries.Count} 条;其中驱动 {entries.Count(e => LooksLikeDriver(e))} 条。URL={url}");

        return entries;
    }

    /// <summary>解析直链。MUC 的下载直链在 DownloadDialog.aspx 返回的脚本里。</summary>
    public async Task<string?> ResolveDownloadUrlAsync(OnlineDriverCandidate candidate, CancellationToken ct)
    {
        // candidate.Url 存放的是 updateId
        var payload = new Dictionary<string, string>
        {
            ["updateIDs"] = JsonSerializer.Serialize(new[]
            {
                new { size = 0, languages = "", uidInfo = candidate.Url, updateID = candidate.Url },
            }),
            ["updateIDsBlockedForImport"] = string.Empty,
            ["wsusApiPresent"] = string.Empty,
            ["contentImport"] = string.Empty,
            ["sku"] = string.Empty,
            ["serverName"] = string.Empty,
            ["ssl"] = "1",
        };

        using var content = new FormUrlEncodedContent(payload);
        using var response = await _http.PostAsync(DownloadDialogEndpoint, content, ct).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var match = UrlRegex.Match(html);
        if (!match.Success)
        {
            _audit.Write("在线检索-MUC直链", candidate.DisplayName, "失败", "DownloadDialog 未返回可识别的下载地址。");
            return null;
        }

        var url = match.Groups["url"].Value.Replace("&amp;", "&");

        // Microsoft Update Catalog 的直链有时是 http,统一升级为 https;
        // 若服务器不支持 https,SecureDownloader 会因非 HTTPS 而拒绝,这是预期行为。
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["http://".Length..];
        }

        return url;
    }

    private static string StripHtml(string html)
        => Regex.Replace(html, "<.*?>", string.Empty).Replace("&nbsp;", " ").Trim();
}
