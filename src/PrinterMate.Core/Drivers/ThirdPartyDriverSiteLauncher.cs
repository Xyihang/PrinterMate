using System.Diagnostics;
using System.Text;
using PrinterMate.Security;

namespace PrinterMate.Core.Drivers;

/// <summary>打开第三方驱动站搜索页的方式。</summary>
public enum DriverSiteOpenMode
{
    /// <summary>站点支持 GET 搜索,可以直接拼出搜索 URL。</summary>
    GetUrl,

    /// <summary>站点只接受 POST:生成本地表单页并交由浏览器提交(程序自身不发起该请求)。</summary>
    LocalPostBridge,

    /// <summary>只打开站点分类页,检索由用户在站内完成。</summary>
    BrowseOnly,
}

/// <summary>一个第三方驱动站的检索入口。</summary>
public sealed record DriverSiteSearch(
    string Id,
    string DisplayName,
    string SiteUrl,
    DriverSiteOpenMode Mode,
    string Endpoint,
    string CharsetName,
    string ParameterName,
    string RiskNote)
{
    public string ModeText => Mode switch
    {
        DriverSiteOpenMode.GetUrl => "GET 搜索",
        DriverSiteOpenMode.LocalPostBridge => "本地表单提交(POST)",
        _ => "仅打开分类页",
    };
}

/// <summary>一次"打开第三方站搜索页"的待执行请求。</summary>
public sealed record DriverSiteOpenRequest(
    DriverSiteSearch Site,
    string Model,
    string Target,
    bool IsLocalFile,
    string Description);

/// <summary>
/// 第三方驱动站检索入口(风险自担)。
///
/// 边界(有意为之,不要放宽):
///   - 本类**只负责在浏览器中打开一个页面**,不做下载、不做安装、不绕过任何校验;
///   - 程序自身的驱动获取通道仍然是白名单 + SHA256 + Authenticode;
///   - 每次调用都写入审计日志,记录型号、站点与最终 URL,便于事后追溯。
/// </summary>
public sealed class ThirdPartyDriverSiteLauncher
{
    /// <summary>打开前必须向用户展示的告知文本。</summary>
    public const string MandatoryWarning =
        "即将在浏览器中打开第三方驱动站的搜索页。请注意:\n\n"
        + "1. 这些站点并非厂商官方来源,其驱动包多为重新打包。\n"
        + "2. 实测样本(UPD_PCL6_8.2.0.26778.zip,SHA256 66022E55…)的驱动负载被封装成改名后的加密 7z 文件,"
        + "安装前无法检查内容,杀毒软件静态扫描同样无效。\n"
        + "3. 这类包普遍没有外露的 .cat 目录文件,Windows 驱动签名校验会被跳过。\n"
        + "4. 本程序不会替你下载或安装这些包,也不会放宽任何校验;后续操作完全由你自己判断。\n\n"
        + "如果你只是想给自己的打印机装上驱动,优先使用厂商官网或本程序的内置驱动库。\n\n"
        + "确认继续打开搜索页?";

    private static readonly TimeSpan BridgeFileRetention = TimeSpan.FromDays(1);

    private static readonly IReadOnlyList<DriverSiteSearch> RegisteredSites = new[]
    {
        new DriverSiteSearch(
            Id: "dyjqd",
            DisplayName: "打印机驱动网 (dyjqd.com)",
            SiteUrl: "http://www.dyjqd.com/",
            Mode: DriverSiteOpenMode.GetUrl,
            Endpoint: "http://www.dyjqd.com/plus/search.php",
            CharsetName: "gb2312",
            ParameterName: "q",
            RiskNote: "重打包来源,驱动包可能缺少数字签名。"),

        new DriverSiteSearch(
            Id: "drvsky",
            DisplayName: "驱动天空 (drvsky.com)",
            SiteUrl: "http://www.drvsky.com/",
            Mode: DriverSiteOpenMode.LocalPostBridge,
            Endpoint: "http://www.drvsky.com/search.asp",
            CharsetName: "gb2312",
            ParameterName: "k",
            RiskNote: "该站只接受 POST 检索;其打包的驱动负载为加密压缩,安装前不可审计。"),

        new DriverSiteSearch(
            Id: "drvsky-sort",
            DisplayName: "驱动天空 · 打印机分类页 (drvsky.com)",
            SiteUrl: "http://www.drvsky.com/sort/5_1.htm",
            Mode: DriverSiteOpenMode.BrowseOnly,
            Endpoint: "http://www.drvsky.com/sort/5_1.htm",
            CharsetName: "gb2312",
            ParameterName: string.Empty,
            RiskNote: "仅打开分类列表,由你自己在站内浏览。"),
    };

    private readonly AuditLog _audit;

    public ThirdPartyDriverSiteLauncher(AuditLog? audit = null)
    {
        _audit = audit ?? new AuditLog();
    }

    public static IReadOnlyList<DriverSiteSearch> Sites => RegisteredSites;

    public static DriverSiteSearch? FindSite(string siteId)
        => RegisteredSites.FirstOrDefault(s => string.Equals(s.Id, siteId, StringComparison.OrdinalIgnoreCase));

    /// <summary>构造待打开的目标(不产生任何副作用,便于单测与预览)。</summary>
    public OperationResult<DriverSiteOpenRequest> Prepare(string siteId, string model)
    {
        var site = FindSite(siteId);
        if (site is null)
        {
            return OperationResult<DriverSiteOpenRequest>.Fail(
                $"未注册的驱动站标识:{siteId}",
                "请从下拉列表中选择一个已注册的站点。");
        }

        var keyword = (model ?? string.Empty).Trim();
        if (site.Mode != DriverSiteOpenMode.BrowseOnly && keyword.Length == 0)
        {
            return OperationResult<DriverSiteOpenRequest>.Fail(
                "请先输入打印机型号。",
                "例如:HP LaserJet 1020、Canon MG6300、奔图 P2500。");
        }

        return site.Mode switch
        {
            DriverSiteOpenMode.GetUrl => OperationResult<DriverSiteOpenRequest>.Ok(
                new DriverSiteOpenRequest(site, keyword, BuildSearchUrl(site, keyword)!, IsLocalFile: false,
                    $"将在浏览器中打开 {site.DisplayName} 的搜索页,关键词「{keyword}」。"),
                "已生成搜索地址。"),

            DriverSiteOpenMode.LocalPostBridge => OperationResult<DriverSiteOpenRequest>.Ok(
                new DriverSiteOpenRequest(site, keyword, string.Empty, IsLocalFile: true,
                    $"该站只支持 POST 检索,将生成本地表单页并交由浏览器提交,关键词「{keyword}」。"),
                "已生成本地表单方案。"),

            _ => OperationResult<DriverSiteOpenRequest>.Ok(
                new DriverSiteOpenRequest(site, keyword, site.Endpoint, IsLocalFile: false,
                    $"将打开 {site.DisplayName},请自行在站内检索。"),
                "已生成站点地址。"),
        };
    }

    /// <summary>执行打开动作。会写入审计日志。</summary>
    public OperationResult<string> Open(DriverSiteOpenRequest request)
    {
        try
        {
            var target = request.Target;
            var note = request.Description;

            if (request.Site.Mode == DriverSiteOpenMode.LocalPostBridge)
            {
                target = WriteBridgeFile(request.Site, request.Model);
                note += $"(本地文件:{target})";
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

            _audit.Write(
                "第三方驱动站-打开搜索页",
                request.Site.DisplayName,
                "已打开",
                $"{note};站点={request.Site.SiteUrl};模式={request.Site.ModeText}",
                request.Site.SiteUrl);

            return new OperationResult<string>
            {
                Status = OperationStatus.Success,
                Value = target,
                Message = $"已在浏览器中打开 {request.Site.DisplayName}。",
                Suggestion = "本程序不会自动下载或安装该站的任何文件,也不会放宽签名校验。",
            };
        }
        catch (Exception ex)
        {
            _audit.Write("第三方驱动站-打开搜索页", request.Site.DisplayName, "失败", ex.Message, request.Site.SiteUrl);
            return ex.ToFailure<string>("打开浏览器");
        }
    }

    /// <summary>把型号按站点字符集百分号编码后拼成 GET 搜索地址。</summary>
    public static string? BuildSearchUrl(DriverSiteSearch site, string model)
    {
        if (site.Mode != DriverSiteOpenMode.GetUrl)
        {
            return null;
        }

        var encoded = PercentEncode(model.Trim(), site.CharsetName);
        var separator = site.Endpoint.Contains('?') ? "&" : "?";
        return $"{site.Endpoint}{separator}{site.ParameterName}={encoded}";
    }

    /// <summary>
    /// 生成本地表单页。站点只接受 POST 时使用:
    /// 页面以站点字符集声明并以该字符集写盘,浏览器会据此编码表单数据,
    /// 从而在不运行任何站点脚本的前提下完成检索跳转。
    /// </summary>
    public static string BuildBridgeHtml(DriverSiteSearch site, string model)
    {
        var keyword = model.Trim();
        var escaped = keyword
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta http-equiv="Content-Type" content="text/html; charset={{site.CharsetName}}">
            <title>PrinterMate · 正在打开 {{site.DisplayName}} 搜索页</title>
            <style>
            body { font-family: "Microsoft YaHei", sans-serif; margin: 40px; color: #333; }
            .card { border: 1px solid #e5e7eb; border-radius: 8px; padding: 20px; max-width: 640px; }
            .warn { color: #b45309; }
            code { background: #f3f4f6; padding: 1px 4px; }
            </style>
            </head>
            <body>
            <div class="card">
            <h2>正在打开 {{site.DisplayName}} 的搜索结果</h2>
            <p>检索关键词:<code>{{escaped}}</code></p>
            <p>如果没有自动跳转,请点击下面的按钮。</p>
            <form id="f" action="{{site.Endpoint}}" method="post" target="_blank">
            <input type="hidden" name="{{site.ParameterName}}" value="{{escaped}}">
            <button type="submit">前往 {{site.DisplayName}} 搜索</button>
            </form>
            <p class="warn">提示:该站点并非厂商官方来源,驱动包可能为重新打包且缺失数字签名。
            PrinterMate 不会自动下载或安装该站的任何文件。</p>
            </div>
            <script>document.getElementById('f').submit();</script>
            </body>
            </html>
            """;
    }

    private static string WriteBridgeFile(DriverSiteSearch site, string model)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PrinterMate");
        Directory.CreateDirectory(directory);
        CleanupOldBridges(directory);

        var path = Path.Combine(directory, $"search-{site.Id}-{Guid.NewGuid():N}.html");
        var encoding = ResolveEncoding(site.CharsetName);
        File.WriteAllText(path, BuildBridgeHtml(site, model), encoding);
        return path;
    }

    private static void CleanupOldBridges(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "search-*.html"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > BridgeFileRetention)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }
    }

    private static string PercentEncode(string text, string charsetName)
    {
        var encoding = ResolveEncoding(charsetName);
        var bytes = encoding.GetBytes(text);
        var sb = new StringBuilder(bytes.Length * 3);

        foreach (var b in bytes)
        {
            // 非保留字符直接输出,其余按字节百分号编码(这是传统表单的编码方式)。
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else if (b == ' ')
            {
                sb.Append("%20");
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解析站点字符集。.NET 默认不携带代码页,必须先注册 CodePages 提供程序;
    /// 若运行环境确实拿不到 GBK,则退回 UTF-8 并保证不抛异常。
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static Encoding ResolveEncoding(string charsetName)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(charsetName);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
