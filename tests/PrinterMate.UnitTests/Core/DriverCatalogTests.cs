using PrinterMate.Catalog;
using PrinterMate.Core;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Infrastructure;
using Xunit;

namespace PrinterMate.UnitTests.Core;

/// <summary>测试用 WPP 检测器,避免测试结果受运行机器的组策略影响。</summary>
internal sealed class StubWppDetector(bool enabled) : WppModeDetector
{
    public override WppModeStatus Detect() => enabled
        ? new WppModeStatus(true, "测试", "测试:强制启用 WPP")
        : WppModeStatus.Disabled("测试:强制关闭 WPP");
}

public class DriverCatalogTests
{
    private static DriverLibrary Library => DriverLibrary.Load(policy: TestPolicies.Isolated());

    [Fact]
    public void 内置清单_应包含全部目标品牌()
    {
        var brands = Library.Brands;

        foreach (var expected in new[] { "HP", "Canon", "Epson", "Brother", "Kyocera", "Ricoh", "Lexmark", "Fujifilm", "Pantum", "Lenovo" })
        {
            Assert.Contains(expected, brands);
        }

        Assert.True(brands.Count >= 15, $"内置品牌数量过少:{brands.Count}");
    }

    [Fact]
    public void 内置清单_应包含微软类驱动条目()
    {
        var inbox = Library.Entries
            .Where(e => e.Pdl == PrintDescriptionLanguage.ClassDriver)
            .ToList();

        Assert.Contains(inbox, e => e.DriverName == InboxClassDrivers.Ipp);
        Assert.All(inbox, e => Assert.True(e.Redistributable, "系统内置类驱动必须标记为可分发"));
        Assert.All(inbox, e => Assert.True(e.IsInboxDriver));
    }

    [Fact]
    public void 未入库的厂商驱动_必须被判定为不可安装并给出中文原因()
    {
        var hp = Library.Entries.First(e => e.Brand == "HP" && e.Pdl == PrintDescriptionLanguage.PCL6);

        var availability = Library.CheckAvailability(hp, "x64", "win11");

        Assert.False(availability.Available);
        Assert.False(string.IsNullOrWhiteSpace(availability.Reason));
        // 允许的原因:授权未确认 / 驱动文件缺失 / 哈希缺失,三者都必须是中文可读原因
        Assert.Matches("[\u4e00-\u9fa5]", availability.Reason);
    }

    [Fact]
    public void 驱动库状态_应区分内置条目与待入库条目()
    {
        var status = Library.GetStatus("x64");

        Assert.True(status.TotalEntries > 0);
        Assert.True(status.InboxEntries >= 2);
        Assert.True(status.PackagedEntries > 0);
        // 当前仓库不含厂商二进制,因此全部厂商条目都应在"不可用"清单里
        Assert.True(status.MissingPackages.Count >= status.PackagedEntries - 2);
    }

    [Fact]
    public void 驱动包已入库但清单缺少SHA256时必须拒绝()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: string.Empty);

        var availability = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");

        Assert.False(availability.Available);
        Assert.Contains("SHA256", availability.Reason);
    }

    [Fact]
    public void 驱动包与SHA256齐备时应判定为可离线安装()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: new string('A', 64));

        var availability = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");

        Assert.True(availability.Available);
        Assert.NotNull(availability.InfPath);
        Assert.EndsWith("test.inf", availability.InfPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 授权未确认时必须禁止本地安装并说明原因()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: new string('A', 64), redistributable: false);

        var availability = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");

        Assert.False(availability.Available);
        Assert.Contains("授权", availability.Reason);
    }

    [Fact]
    public void 架构不匹配时必须拒绝()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: new string('A', 64));

        var availability = sandbox.Library.CheckAvailability(sandbox.Entry, "arm64");

        Assert.False(availability.Available);
        Assert.Contains("arm64", availability.Reason);
    }
}

/// <summary>在临时目录里搭一个最小的驱动库,用于验证可安装性判定。</summary>
internal sealed class SandboxLibrary : IDisposable
{
    private SandboxLibrary(string root, DriverLibrary library, DriverCatalogEntry entry)
    {
        Root = root;
        Library = library;
        Entry = entry;
    }

    public string Root { get; }

    public DriverLibrary Library { get; }

    public DriverCatalogEntry Entry { get; }

    public static SandboxLibrary Create(string sha256X64, bool redistributable = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "pm-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Catalog"));
        Directory.CreateDirectory(Path.Combine(root, "Drivers", "Test"));
        File.WriteAllText(
            Path.Combine(root, "Drivers", "Test", "test.inf"),
            "[Version]\r\nSignature=\"$Windows NT$\"\r\n");

        var manifest = $$"""
            {
              "schemaVersion": "1.0",
              "libraryVersion": "test",
              "entries": [
                {
                  "brand": "Test",
                  "family": "Test UPD",
                  "pdl": "PCL6",
                  "driverName": "Test Universal Driver",
                  "infRelPath": "Drivers/Test/test.inf",
                  "arch": ["x64"],
                  "os": ["win10", "win11"],
                  "version": "1.0",
                  "signerSubject": "CN=Test",
                  "redistributable": {{(redistributable ? "true" : "false")}},
                  "modelPatterns": ["Test*"],
                  "hardwareIdPatterns": ["USBPRINT\\Test*"],
                  "sha256": { "x64": "{{sha256X64}}", "arm64": "" },
                  "priority": 100
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(root, "Catalog", "drivers.json"), manifest);

        var library = DriverLibrary.Load(root, TestPolicies.Isolated());
        return new SandboxLibrary(root, library, library.Entries.Single());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

public class CatalogMatcherTests
{
    private static DriverLibrary Library => DriverLibrary.Load(policy: TestPolicies.Isolated());

    [Theory]
    [InlineData("HP LaserJet Pro M404dn", "HP LASERJET PRO M404DN")]
    [InlineData("  hp   officejet  (USB001) ", "HP OFFICEJET")]
    [InlineData("Canon MG6300 series Printer", "CANON MG6300 SERIES PRINTER")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 型号归一化(string? input, string expected)
    {
        Assert.Equal(expected, CatalogMatcher.NormalizeModel(input));
    }

    [Theory]
    [InlineData("USBPRINT\\HPDESKJET_2520_SERIES", "USBPRINT\\HP*", true)]
    [InlineData("USBPRINT\\SAMSUNGML-2160_SERIES", "USBPRINT\\Samsung*", true)]
    [InlineData("USBPRINT\\EPSONL3150", "USBPRINT\\HP*", false)]
    [InlineData("WSDPRINT\\CANON_MG6300", "WSDPRINT\\*Canon*", true)]
    [InlineData("anything", "*", true)]
    public void 硬件ID通配符匹配(string hardwareId, string pattern, bool expected)
    {
        Assert.Equal(expected, CatalogMatcher.WildcardMatch(hardwareId, pattern));
    }

    [Fact]
    public void USB硬件ID命中HP时_必须返回高置信度的HP候选()
    {
        var input = new DeviceMatchInput(
            Model: "HP Deskjet 2520 series",
            Vendor: "HP",
            HardwareIds: new[] { "USBPRINT\\HPDESKJET_2520_SERIES" },
            CompatibleIds: Array.Empty<string>());

        var matches = CatalogMatcher.Match(Library, input);

        Assert.NotEmpty(matches);
        Assert.Equal("HP", matches[0].Entry.Brand);
        Assert.Equal(MatchKind.HardwareId, matches[0].Kind);
        Assert.Equal(MatchConfidence.High, matches[0].Confidence);
    }

    [Fact]
    public void 仅凭型号也应能匹配到HP并优先PCL6()
    {
        var input = new DeviceMatchInput(
            Model: "HP LaserJet Pro M404dn",
            Vendor: null,
            HardwareIds: Array.Empty<string>(),
            CompatibleIds: Array.Empty<string>());

        var matches = CatalogMatcher.Match(Library, input);

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Equal("HP", m.Entry.Brand));
        Assert.Equal(PrintDescriptionLanguage.PCL6, matches[0].Entry.Pdl);
    }

    [Fact]
    public void 三星机型应由三星条目处理_并回退到HP_UPD()
    {
        var input = new DeviceMatchInput(
            Model: "Samsung ML-2160 Series",
            Vendor: "Samsung",
            HardwareIds: new[] { "USBPRINT\\SAMSUNGML-2160_SERIES" },
            CompatibleIds: Array.Empty<string>());

        var matches = CatalogMatcher.Match(Library, input);

        Assert.NotEmpty(matches);
        Assert.Equal("Samsung", matches[0].Entry.Brand);
    }

    [Fact]
    public void 未知品牌不应命中内置清单()
    {
        var input = new DeviceMatchInput(
            Model: "Zebra ZD421",
            Vendor: "Zebra",
            HardwareIds: new[] { "USBPRINT\\ZEBRA_ZD421" },
            CompatibleIds: Array.Empty<string>());

        Assert.Empty(CatalogMatcher.Match(Library, input));
    }

    [Fact]
    public void 归一化硬件ID应去掉多余的空白与参数()
    {
        Assert.Equal("USBPRINT\\HPXXX", CatalogMatcher.NormalizeHardwareId("  USBPRINT\\HPXXX  "));
        Assert.Equal("USBPRINT\\HPXXX", CatalogMatcher.NormalizeHardwareId("usbprint\\hpXXX\0extra"));
    }
}

public class DriverMatchEngineTests
{
    private static DriverMatchEngine Engine(bool wpp = false) => new(DriverLibrary.Load(policy: TestPolicies.Isolated()), new StubWppDetector(wpp));

    private static DiscoveredPrinter IppMopriaPrinter() => new()
    {
        Id = "net:1",
        DisplayName = "会议室打印机",
        Model = "HP Color LaserJet Pro M479fdw",
        Vendor = "HP",
        Connection = ConnectionKind.Ipp,
        Address = "192.168.1.50",
        IppUri = "ipp://192.168.1.50:631/ipp/print",
        IsMopria = true,
        SupportsIpp = true,
        DiscoverySource = "mDNS",
    };

    private static DiscoveredPrinter UsbPrinter() => new()
    {
        Id = "pnp:usb",
        DisplayName = "HP Deskjet 2520 series",
        Model = "HP Deskjet 2520 series",
        Vendor = "HP",
        Connection = ConnectionKind.Usb,
        PortName = "USB003",
        HardwareIds = new[] { "USBPRINT\\HPDESKJET_2520_SERIES" },
        DiscoverySource = "PnP",
    };

    [Fact]
    public void Mopria设备应走L0免驱通道()
    {
        var decision = Engine().Decide(IppMopriaPrinter());

        Assert.Equal(OperationStatus.Success, decision.Status);
        Assert.Equal("L0", decision.Layer);
        Assert.False(decision.RequiresDownload);
        Assert.NotNull(decision.Selected);
        Assert.Equal(DriverSource.ClassDriver, decision.Selected!.Source);
        Assert.Equal(InboxClassDrivers.Ipp, decision.Selected.DriverName);
    }

    [Fact]
    public void 未入库的USB打印机应给出L2在线候选_而不是直接失败()
    {
        var decision = Engine().Decide(UsbPrinter());

        Assert.NotNull(decision.Selected);
        Assert.Equal("L2", decision.Layer);
        Assert.True(decision.RequiresDownload);
        Assert.Equal(DriverSource.Online, decision.Selected!.Source);
        // 必须给出中文的可操作建议
        Assert.Matches("[\u4e00-\u9fa5]", decision.Suggestion);

        // 同时应保留内置候选,并说明其不可用原因
        Assert.Contains(decision.Candidates, c => c.Source == DriverSource.BuiltinUpd && !c.Installable && c.BlockReason is not null);
    }

    [Fact]
    public void WPP模式下_非免驱设备必须被拒绝并给出降级说明()
    {
        var decision = Engine(wpp: true).Decide(UsbPrinter());

        Assert.Equal(OperationStatus.NotSupported, decision.Status);
        Assert.Contains("Windows Protected Print Mode", decision.Message);
        Assert.Matches("[\u4e00-\u9fa5]", decision.Suggestion);
    }

    [Fact]
    public void WPP模式下_Mopria设备仍应可用()
    {
        var decision = Engine(wpp: true).Decide(IppMopriaPrinter());

        Assert.Equal(OperationStatus.Success, decision.Status);
        Assert.Equal("L0", decision.Layer);
        Assert.Contains("Protected Print Mode", decision.Message);
    }

    [Fact]
    public void 未知品牌应返回未解决并提示手动选择()
    {
        var printer = new DiscoveredPrinter
        {
            Id = "pnp:zebra",
            DisplayName = "Zebra ZD421",
            Model = "Zebra ZD421",
            Vendor = "Zebra",
            Connection = ConnectionKind.Usb,
            PortName = "USB005",
            HardwareIds = new[] { "USBPRINT\\ZEBRA_ZD421" },
        };

        var decision = Engine().Decide(printer);

        Assert.Null(decision.Selected);
        Assert.Equal(OperationStatus.Failed, decision.Status);
        Assert.Contains("Zebra", decision.Message);
    }

    [Fact]
    public void L2兜底必须优先选择PCL6而不是PS()
    {
        // 回归用例:HP 的 PS 条目 priority(110)高于 PCL6(100),
        // 早期实现按 priority 排序导致默认选了 PS,违反规格"同品牌并存时默认 PCL6"。
        var decision = Engine().Decide(UsbPrinter());

        Assert.NotNull(decision.Selected);
        Assert.Equal(PrintDescriptionLanguage.PCL6, decision.Selected!.Pdl);
        Assert.Equal("HP Universal Printing PCL 6", decision.Selected.DriverName);
    }

    [Fact]
    public void 人工候选列表应支持按品牌过滤()
    {
        var candidates = Engine().ListManualCandidates("HP");

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal("HP", c.Brand));
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.PdlText)));
    }

    [Fact]
    public void 人工候选列表_模型关键词应能过滤()
    {
        var all = Engine().ListManualCandidates();
        var filtered = Engine().ListManualCandidates(modelKeyword: "LaserJet");

        Assert.NotEmpty(filtered);
        Assert.True(filtered.Count < all.Count);
    }
}
