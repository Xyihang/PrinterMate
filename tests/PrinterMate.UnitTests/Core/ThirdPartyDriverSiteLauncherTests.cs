using System.Text;
using PrinterMate.Core.Drivers;
using Xunit;

namespace PrinterMate.UnitTests.Core;

public class ThirdPartyDriverSiteLauncherTests
{
    [Fact]
    public void 应注册三个第三方检索入口()
    {
        var ids = ThirdPartyDriverSiteLauncher.Sites.Select(s => s.Id).ToList();

        Assert.Equal(3, ids.Count);
        Assert.Contains("dyjqd", ids);
        Assert.Contains("drvsky", ids);
        Assert.Contains("drvsky-sort", ids);
        Assert.All(ThirdPartyDriverSiteLauncher.Sites, s => Assert.False(string.IsNullOrWhiteSpace(s.RiskNote)));
    }

    [Fact]
    public void GBK代码页必须可用_否则中文型号检索会乱码()
    {
        var gbk = ThirdPartyDriverSiteLauncher.ResolveEncoding("gb2312");

        // GBK 是双字节编码:"奔图" = 4 字节;若退化成 UTF-8 会是 6 字节。
        Assert.Equal(4, gbk.GetBytes("奔图").Length);
    }

    [Fact]
    public void GET站点应拼出可直接打开的搜索地址()
    {
        var site = ThirdPartyDriverSiteLauncher.FindSite("dyjqd")!;

        var url = ThirdPartyDriverSiteLauncher.BuildSearchUrl(site, "HP 1020");

        Assert.Equal("http://www.dyjqd.com/plus/search.php?q=HP%201020", url);
    }

    [Fact]
    public void 中文型号必须按GBK百分号编码()
    {
        var site = ThirdPartyDriverSiteLauncher.FindSite("dyjqd")!;
        var gbk = ThirdPartyDriverSiteLauncher.ResolveEncoding("gb2312");

        var url = ThirdPartyDriverSiteLauncher.BuildSearchUrl(site, "奔图")!;

        var expected = string.Concat(gbk.GetBytes("奔图").Select(b => "%" + b.ToString("X2")));
        Assert.EndsWith("?q=" + expected, url);
        // 2 个汉字 → 4 组百分号编码
        Assert.Equal(4, url[(url.IndexOf("?q=", StringComparison.Ordinal) + 3)..].Split('%').Length - 1);
    }

    [Fact]
    public void POST站点不应产生GET地址()
    {
        var site = ThirdPartyDriverSiteLauncher.FindSite("drvsky")!;

        Assert.Null(ThirdPartyDriverSiteLauncher.BuildSearchUrl(site, "HP 1020"));
    }

    [Fact]
    public void 本地表单页必须包含POST表单与自动提交()
    {
        var site = ThirdPartyDriverSiteLauncher.FindSite("drvsky")!;

        var html = ThirdPartyDriverSiteLauncher.BuildBridgeHtml(site, "HP 1020");

        Assert.Contains("action=\"http://www.drvsky.com/search.asp\"", html);
        Assert.Contains("method=\"post\"", html);
        Assert.Contains("name=\"k\"", html);
        Assert.Contains("value=\"HP 1020\"", html);
        Assert.Contains("charset=gb2312", html);
        Assert.Contains("document.getElementById('f').submit()", html);
    }

    [Fact]
    public void 本地表单页必须转义HTML特殊字符()
    {
        var site = ThirdPartyDriverSiteLauncher.FindSite("drvsky")!;

        var html = ThirdPartyDriverSiteLauncher.BuildBridgeHtml(site, "A<b>&\"1");

        Assert.Contains("value=\"A&lt;b&gt;&amp;&quot;1\"", html);
        Assert.DoesNotContain("value=\"A<b>", html);
    }

    [Fact]
    public void 打开前必须告知风险且文案要覆盖加密负载这一实测结论()
    {
        var warning = ThirdPartyDriverSiteLauncher.MandatoryWarning;

        Assert.Contains("加密", warning);
        Assert.Contains("不会替你下载或安装", warning);
        Assert.Contains("66022E55", warning);
    }

    [Fact]
    public void 未知站点应被拒绝()
    {
        var result = new ThirdPartyDriverSiteLauncher().Prepare("unknown-site", "HP 1020");

        Assert.False(result.IsSuccess);
        Assert.Contains("未注册", result.Message);
    }

    [Fact]
    public void 搜索类站点缺少型号时应被拒绝()
    {
        var result = new ThirdPartyDriverSiteLauncher().Prepare("dyjqd", "   ");

        Assert.False(result.IsSuccess);
        Assert.Contains("型号", result.Message);
    }

    [Fact]
    public void 分类页允许不填型号()
    {
        var result = new ThirdPartyDriverSiteLauncher().Prepare("drvsky-sort", string.Empty);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Value!.IsLocalFile);
        Assert.Equal("http://www.drvsky.com/sort/5_1.htm", result.Value.Target);
    }

    [Fact]
    public void 准备阶段不得产生任何副作用()
    {
        var before = Directory.Exists(Path.Combine(Path.GetTempPath(), "PrinterMate"))
            ? Directory.GetFiles(Path.Combine(Path.GetTempPath(), "PrinterMate")).Length
            : 0;

        var result = new ThirdPartyDriverSiteLauncher().Prepare("drvsky", "HP 1020");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsLocalFile);

        var after = Directory.Exists(Path.Combine(Path.GetTempPath(), "PrinterMate"))
            ? Directory.GetFiles(Path.Combine(Path.GetTempPath(), "PrinterMate")).Length
            : 0;

        // Prepare 只描述方案,真正写文件发生在 Open
        Assert.Equal(before, after);
    }
}
