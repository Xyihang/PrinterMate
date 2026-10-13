using PrinterMate.Security;
using Xunit;

namespace PrinterMate.UnitTests.Security;

public class DownloadSourceAllowlistTests
{
    [Fact]
    public void Default_LoadsEmbeddedWhitelist()
    {
        var allowlist = DownloadSourceAllowlist.Default;

        // 不断死数量:白名单会随官方 CDN 增补,数量不是契约,
        // 「功能上必需的域名在列表里」才是契约。
        Assert.Contains("catalog.update.microsoft.com", allowlist.MicrosoftDomains);
        Assert.Contains("dl.delivery.mp.microsoft.com", allowlist.MicrosoftDomains);
        // Microsoft Update Catalog 解析出的下载直链主机(2026-10 线上实测值)
        Assert.Contains("catalog.s.download.windowsupdate.com", allowlist.MicrosoftDomains);
        Assert.True(allowlist.MicrosoftDomains.Count >= 7);
        Assert.NotEmpty(allowlist.VendorDomains);
        Assert.Contains(allowlist.VendorDomains, v => v.Brand == "HP" && v.Domain == "hp.com");
    }

    [Theory]
    [InlineData("https://ftp.hp.com/pub/softpaq/sp1.exe", "HP")]
    [InlineData("https://h10025.www1.hp.com/ewfrf/wc/softwareDownloadIndex", "HP")]
    [InlineData("https://gdlp01.c-wss.com/gds/driver.zip", "Canon")]
    [InlineData("https://download.epson-europe.com/pub/download/1.zip", "Epson")]
    [InlineData("https://download.brother.com/welcome/dlf/driver.exe", "Brother")]
    [InlineData("https://downloads.lexmark.com/downloads/driver.exe", "Lexmark")]
    [InlineData("https://www.jolimark.com/download/driver.exe", "Jolimark")]
    public void Check_AllowsHttpsVendorDomains(string url, string brand)
    {
        var result = DownloadSourceAllowlist.Default.Check(url, brand);

        Assert.True(result.IsAllowed);
        Assert.Equal(SourceVerdict.Allowed, result.Verdict);
        Assert.True(result.IsVendorDomain);
        Assert.NotEmpty(result.MatchedDomain);
    }

    [Theory]
    [InlineData("http://ftp.hp.com/pub/softpaq/sp1.exe")]
    [InlineData("ftp://ftp.hp.com/pub/softpaq/sp1.exe")]
    [InlineData("file:///C:/windows/system32/driver.cab")]
    public void Check_RejectsNonHttps(string url)
    {
        var result = DownloadSourceAllowlist.Default.Check(url);

        Assert.False(result.IsAllowed);
        Assert.Equal(SourceVerdict.NotHttps, result.Verdict);
        Assert.Contains("https", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://hp.com.evil.io/payload.exe")]
    [InlineData("https://evil-hp.com/payload.exe")]
    [InlineData("https://notmicrosoft.com/payload.exe")]
    [InlineData("https://ahp.com/payload.exe")]
    [InlineData("https://microsoft.com.evil.io/payload.exe")]
    [InlineData("https://download.microsoft.com.evil.io/payload.exe")]
    [InlineData("https://driverscollection.com/hp.exe")]
    [InlineData("https://www.softpedia.com/driver.exe")]
    public void Check_RejectsDeceptiveAndThirdPartyDomains(string url)
    {
        var result = DownloadSourceAllowlist.Default.Check(url);

        Assert.False(result.IsAllowed);
        Assert.Equal(SourceVerdict.NotAllowlisted, result.Verdict);
    }

    [Fact]
    public void Check_AllowsMicrosoftDeliveryDomain()
    {
        var result = DownloadSourceAllowlist.Default.Check("https://dl.delivery.mp.microsoft.com/filestreamingservice/files/abc.cab");

        Assert.True(result.IsAllowed);
        Assert.Equal(SourceVerdict.Allowed, result.Verdict);
        Assert.False(result.IsVendorDomain);
        Assert.Equal("dl.delivery.mp.microsoft.com", result.MatchedDomain);
    }

    [Fact]
    public void Check_ReturnsVendorMismatchWhenBrandDisagrees()
    {
        var result = DownloadSourceAllowlist.Default.Check("https://ftp.hp.com/pub/softpaq/sp1.exe", "Canon");

        Assert.False(result.IsAllowed);
        Assert.Equal(SourceVerdict.VendorMismatch, result.Verdict);
        Assert.Contains("Canon", result.Reason);
    }

    [Fact]
    public void Check_AllowsMicrosoftHostEvenWhenExpectedBrandIsAVendor()
    {
        // 回归用例:早期实现会在这里返回 VendorMismatch,导致
        // 「期望 HP,却从微软 CDN 下载 HP 官方驱动包」这条唯一正确的路径被自家策略拒死。
        // Windows Update Catalog 就是微软替厂商分发官方驱动的渠道,品牌一致性由签名主体把关。
        var result = DownloadSourceAllowlist.Default.Check("https://download.microsoft.com/x.exe", "HP");

        Assert.True(result.IsAllowed, result.Reason);
        Assert.Equal(SourceVerdict.Allowed, result.Verdict);
        Assert.False(result.IsVendorDomain);
    }

    [Fact]
    public void Check_AllowsMicrosoftCatalogCdnForVendorPrinterDriver()
    {
        // 线上实测的完整场景:HP 打印机 → Microsoft Update Catalog → catalog.s.download.windowsupdate.com
        var result = DownloadSourceAllowlist.Default.Check(
            "https://catalog.s.download.windowsupdate.com/msdownload/update/driver/drvs/2013/01/20478280_abc.cab",
            "HP");

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void Check_ReturnsMalformedForGarbage()
    {
        var result = DownloadSourceAllowlist.Default.Check("not a url");

        Assert.False(result.IsAllowed);
        Assert.Equal(SourceVerdict.Malformed, result.Verdict);
    }

    [Theory]
    [InlineData("hp.com", "hp.com", true)]
    [InlineData("ftp.hp.com", "hp.com", true)]
    [InlineData("a.b.hp.com", "hp.com", true)]
    [InlineData("evil-hp.com", "hp.com", false)]
    [InlineData("ahp.com", "hp.com", false)]
    [InlineData("hp.com.evil.io", "hp.com", false)]
    [InlineData("hp.com.hk", "hp.com", false)]
    [InlineData("HP.COM", "hp.com", true)]
    public void DomainMatches_UsesLabelBoundaries(string host, string domain, bool expected)
    {
        Assert.Equal(expected, DownloadSourceAllowlist.DomainMatches(host, domain));
    }

    [Fact]
    public void Load_RejectsEmptyJson()
    {
        Assert.Throws<ArgumentException>(() => DownloadSourceAllowlist.Load("   "));
    }

    [Fact]
    public void Load_RejectsJsonWithoutDomains()
    {
        Assert.Throws<ArgumentException>(() => DownloadSourceAllowlist.Load("{\"microsoftDomains\":[],\"vendorDomains\":[]}"));
    }

    [Fact]
    public void Load_UsesCustomWhitelist()
    {
        const string json = """
        {
          "microsoftDomains": ["catalog.update.microsoft.com"],
          "vendorDomains": [ { "brand": "Acme", "domain": "acme-printers.example", "note": "测试" } ]
        }
        """;

        var allowlist = DownloadSourceAllowlist.Load(json);

        Assert.Single(allowlist.MicrosoftDomains);
        Assert.Single(allowlist.VendorDomains);
        Assert.True(allowlist.Check("https://printers.acme-printers.example/d.exe", "Acme").IsAllowed);
        Assert.Equal(SourceVerdict.NotAllowlisted, allowlist.Check("https://evil.example/d.exe").Verdict);
    }
}
