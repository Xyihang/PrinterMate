using System.Text.Json;
using PrinterMate.Catalog;
using PrinterMate.Core.Drivers;
using Xunit;

namespace PrinterMate.UnitTests.Core;

public class InfDriverNameParsingTests
{
    /// <summary>HP 官方 UPD 的真实段命名风格:驱动名在 [HP.NTamd64],不在 [Models]。</summary>
    private const string HpStyleInf = """
        ; HP Universal Print Driver for Windows PCL6
        [Version]
        Signature="$Windows NT$"
        Class=Printer
        ClassGuid={4d36e979-e325-11ce-bfc1-08002be10318}
        Provider=%HP%

        [Manufacturer]
        %HP%=HP,NTamd64

        [HP]
        "HP Universal Printing PCL 6" = HPUPD_PCL6.Install,USBPRINT\HPXXX
        "HP Universal Printing PS"    = HPUPD_PS.Install,USBPRINT\HPYYY

        [HP.NTamd64]
        "HP Universal Printing PCL 6" = HPUPD_PCL6.Install,USBPRINT\HPXXX
        "HP LaserJet 1020"            = HPUPD_PCL6.Install,USBPRINT\HPLJ1020

        [HPUPD_PCL6.Install]
        CopyFiles=@hpcu060.dll

        [Strings]
        HP="HP Inc."
        """;

    private static string WriteTempInf(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "pm-inf-" + Guid.NewGuid().ToString("N") + ".inf");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void 应解析出不在Models段里的驱动名()
    {
        var path = WriteTempInf(HpStyleInf);
        try
        {
            var names = OnlineDriverInstaller.ReadDriverNamesFromInf(path);

            Assert.Contains("HP Universal Printing PCL 6", names);
            Assert.Contains("HP Universal Printing PS", names);
            Assert.Contains("HP LaserJet 1020", names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 不应把Version与Strings段内容当成驱动名()
    {
        var path = WriteTempInf(HpStyleInf);
        try
        {
            var names = OnlineDriverInstaller.ReadDriverNamesFromInf(path);

            Assert.DoesNotContain("$Windows NT$", names);
            Assert.DoesNotContain("HP Inc.", names);
            Assert.DoesNotContain("NTamd64", names);
            Assert.DoesNotContain("HP", names);
            Assert.DoesNotContain("Printer", names);
            // 安装节里的指令不应被当成驱动名
            Assert.DoesNotContain("CopyFiles", names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 驱动名应去重()
    {
        var path = WriteTempInf(HpStyleInf);
        try
        {
            var names = OnlineDriverInstaller.ReadDriverNamesFromInf(path);

            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 文件不存在时应返回空列表而不是抛异常()
    {
        Assert.Empty(OnlineDriverInstaller.ReadDriverNamesFromInf(@"C:\definitely\not\here.inf"));
    }

    [Fact]
    public void 微软更新目录的CDN直链必须被白名单放行()
    {
        // 回归用例:实测 Microsoft Update Catalog 解析出的直链主机是
        // catalog.s.download.windowsupdate.com。早期白名单里只有 download.microsoft.com
        // 和 windowsupdate.microsoft.com,导致下载会被自己的安全策略拒绝。
        var allowlist = PrinterMate.Security.DownloadSourceAllowlist.Default;

        var real = allowlist.Check("https://catalog.s.download.windowsupdate.com/msdownload/update/driver/drvs/2013/01/20478280_abc.cab");
        Assert.True(real.IsAllowed, real.Reason);

        var search = allowlist.Check("https://www.catalog.update.microsoft.com/Search.aspx?q=hp");
        Assert.True(search.IsAllowed, search.Reason);

        var dialog = allowlist.Check("https://www.catalog.update.microsoft.com/DownloadDialog.aspx");
        Assert.True(dialog.IsAllowed, dialog.Reason);

        // 伪装域名仍然必须被拒绝
        Assert.False(allowlist.Check("https://catalog.s.download.windowsupdate.com.evil.io/a.cab").IsAllowed);
        Assert.False(allowlist.Check("https://evildownload.windowsupdate.com.attacker.net/a.cab").IsAllowed);
        Assert.False(allowlist.Check("https://driverscollection.com/hp.exe").IsAllowed);
    }

    [Fact]
    public void 官方公布哈希应为标准SHA256向量()
    {
        // SHA256("") 的标准 Base64 与十六进制表示
        const string base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";
        Assert.Equal(
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            MicrosoftUpdateCatalogClient.TryBase64ToHex(base64));
    }

    [Fact]
    public void 线上公布的Base64摘要应转为六十四位十六进制()
    {
        // 2026-10 从 Microsoft Update Catalog DownloadDialog 实测抓到的值
        var hex = MicrosoftUpdateCatalogClient.TryBase64ToHex("tAH7ljtTr09KKUaPKY+sZi0kat6PolKhqdiiBmPmzjQ=");

        Assert.NotNull(hex);
        Assert.Equal(64, hex!.Length);
        Assert.Matches("^[0-9A-F]{64}$", hex);
    }

    [Fact]
    public void 非法的或长度不符的摘要应返回null()
    {
        Assert.Null(MicrosoftUpdateCatalogClient.TryBase64ToHex(null));
        Assert.Null(MicrosoftUpdateCatalogClient.TryBase64ToHex(string.Empty));
        Assert.Null(MicrosoftUpdateCatalogClient.TryBase64ToHex("not-base64!!"));
        // SHA1 是 20 字节,不是 SHA256
        Assert.Null(MicrosoftUpdateCatalogClient.TryBase64ToHex(Convert.ToBase64String(new byte[20])));
    }

    [Theory]
    [InlineData("HP", "HP Deskjet 2520 series", "https://www.catalog.update.microsoft.com/Search.aspx?q=HP%20Deskjet%202520%20series")]
    [InlineData("HP", "Deskjet 2520 series", "https://www.catalog.update.microsoft.com/Search.aspx?q=HP%20Deskjet%202520%20series")]
    [InlineData("", "", "https://www.catalog.update.microsoft.com/Search.aspx?q=printer%20driver")]
    public void 官方目录搜索地址构造(string brand, string model, string expected)
    {
        Assert.Equal(expected, OnlineDriverService.BuildCatalogSearchUrl(brand, model));
    }

    [Theory]
    [InlineData("HP", "HP Deskjet 2520 series", "HP Deskjet 2520 series")]
    [InlineData("HP", "Deskjet 2520 series", "HP Deskjet 2520 series")]
    [InlineData("HP", "", "HP")]
    [InlineData("", "Deskjet 2520", "Deskjet 2520")]
    [InlineData("Canon", "MG6300", "Canon MG6300")]
    public void 检索词构造(string brand, string model, string expected)
    {
        // 回归用例:早期实现会拼出「HP HP Deskjet 2520 series printer driver」,
        // 这种短语在 Microsoft Update Catalog 上直接查不到任何结果。
        Assert.Equal(expected, OnlineDriverService.BuildQuery(brand, model));
    }

    [Theory]
    [InlineData("HP Universal Printing PCL 6", PrintDescriptionLanguage.PCL6)]
    [InlineData("HP Universal Printing PS", PrintDescriptionLanguage.PS)]
    [InlineData("Canon Generic Plus UFR II", PrintDescriptionLanguage.PCL6)]
    [InlineData("KONICA MINOLTA Universal PCL", PrintDescriptionLanguage.PCL6)]
    [InlineData("OKI GDI Driver", PrintDescriptionLanguage.GDI)]
    [InlineData("", PrintDescriptionLanguage.PCL6)]
    public void PDL推断(string name, PrintDescriptionLanguage expected)
    {
        Assert.Equal(expected, OnlineDriverInstaller.InferPdl(name));
    }
}

public class DriverLibraryWriterTests
{
    private static DriverPackageInstallRequest Request(SandboxLibrary sandbox, string sourceDirectory, bool redistributable) => new(
        Brand: "HP",
        Family: "Universal Print Driver",
        Pdl: PrintDescriptionLanguage.PCL6,
        DriverName: "HP Universal Printing PCL 6",
        SourceDirectory: sourceDirectory,
        InfFileName: "hpcu060.inf",
        Sha256: new string('B', 64),
        SignerSubject: "CN=HP Inc, O=HP Inc, C=US",
        Arch: new[] { "x64" },
        ConfirmRedistribution: redistributable);

    private static string CreateIncomingPackage(string root)
    {
        var source = Path.Combine(root, "incoming");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hpcu060.inf"), "[Version]\nSignature=\"$Windows NT$\"\n");
        File.WriteAllText(Path.Combine(source, "hpcu060.cat"), "not-a-real-catalog");
        File.WriteAllText(Path.Combine(source, "hpcu060.dll"), "binary");
        return source;
    }

    [Fact]
    public void 应把驱动包复制进驱动库并回填清单()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);

        var result = new DriverLibraryWriter(sandbox.Library).AddOrUpdate(Request(sandbox, source, redistributable: true));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.InfRelativePath);
        Assert.StartsWith("Drivers/HP/", result.InfRelativePath, StringComparison.OrdinalIgnoreCase);

        // 文件真的落盘了
        var onDisk = Path.Combine(sandbox.Root, result.InfRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(onDisk));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(onDisk)!, "hpcu060.cat")));

        // 清单里也写进去了,且重新加载后能看到
        Assert.Contains(sandbox.Library.Entries, e => e.DriverName == "HP Universal Printing PCL 6");
    }

    [Fact]
    public void 回填的哈希与签名主体应来自请求()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);

        new DriverLibraryWriter(sandbox.Library).AddOrUpdate(Request(sandbox, source, redistributable: true));

        var entry = sandbox.Library.Entries.Single(e => e.DriverName == "HP Universal Printing PCL 6");
        Assert.Equal(new string('B', 64), entry.Sha256.X64);
        Assert.Equal("CN=HP Inc, O=HP Inc, C=US", entry.SignerSubject);
        Assert.Equal(PrintDescriptionLanguage.PCL6, entry.Pdl);
    }

    [Fact]
    public void 未确认再分发时入库条目不得被标记为可分发()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);

        new DriverLibraryWriter(sandbox.Library).AddOrUpdate(Request(sandbox, source, redistributable: false));

        var entry = sandbox.Library.Entries.Single(e => e.DriverName == "HP Universal Printing PCL 6");
        Assert.False(entry.Redistributable);
    }

    [Fact]
    public void 重复入库应更新而不是产生重复条目()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);
        var writer = new DriverLibraryWriter(sandbox.Library);

        writer.AddOrUpdate(Request(sandbox, source, redistributable: true));
        writer.AddOrUpdate(Request(sandbox, source, redistributable: true));

        Assert.Single(sandbox.Library.Entries.Where(e => e.Brand == "HP" && e.Pdl == PrintDescriptionLanguage.PCL6));
    }

    [Fact]
    public void 源目录不存在时应拒绝()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));

        var result = new DriverLibraryWriter(sandbox.Library)
            .AddOrUpdate(Request(sandbox, Path.Combine(sandbox.Root, "nope"), redistributable: true));

        Assert.False(result.Success);
        Assert.Contains("源目录不存在", result.Message);
    }

    [Fact]
    public void 源目录缺少INF时应拒绝()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = Path.Combine(sandbox.Root, "empty");
        Directory.CreateDirectory(source);

        var result = new DriverLibraryWriter(sandbox.Library).AddOrUpdate(Request(sandbox, source, redistributable: true));

        Assert.False(result.Success);
        Assert.Contains("找不到 INF", result.Message);
    }

    [Fact]
    public void 驱动名为空时应拒绝()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);
        var request = Request(sandbox, source, redistributable: true) with { DriverName = "  " };

        var result = new DriverLibraryWriter(sandbox.Library).AddOrUpdate(request);

        Assert.False(result.Success);
        Assert.Contains("驱动名称不能为空", result.Message);
    }

    [Fact]
    public void 写回后的清单必须是合法JSON且可被重新解析()
    {
        using var sandbox = SandboxLibrary.Create(new string('A', 64));
        var source = CreateIncomingPackage(sandbox.Root);

        new DriverLibraryWriter(sandbox.Library).AddOrUpdate(Request(sandbox, source, redistributable: true));

        var manifestPath = Path.Combine(sandbox.Root, "Catalog", "drivers.json");
        var manifest = JsonSerializer.Deserialize<DriverManifest>(File.ReadAllText(manifestPath));

        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Entries, e => e.DriverName == "HP Universal Printing PCL 6");
        // 原有条目不能被冲掉
        Assert.Contains(manifest.Entries, e => e.Brand == "Test");
    }
}
