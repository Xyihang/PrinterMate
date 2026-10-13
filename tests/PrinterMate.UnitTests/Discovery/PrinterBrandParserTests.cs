using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>品牌/型号识别纯函数测试:覆盖 sysDescr、hrDeviceDescr 与 mDNS TXT 中常见的自述文本。</summary>
public sealed class PrinterBrandParserTests
{
    [Theory]
    [InlineData("HP LaserJet Pro M404dn", "HP")]
    [InlineData("Hewlett-Packard HP Color LaserJet MFP M281fdw", "HP")]
    [InlineData("HP OfficeJet Pro 9010", "HP")]
    [InlineData("Canon i-SENSYS LBP6030", "Canon")]
    [InlineData("Canon PIXMA TS3180 series", "Canon")]
    [InlineData("EPSON L3150 Series", "Epson")]
    [InlineData("Epson WorkForce WF-2860", "Epson")]
    [InlineData("Brother HL-L2350DW series", "Brother")]
    [InlineData("Brother MFC-J5330DW", "Brother")]
    [InlineData("RICOH MP C3004", "Ricoh")]
    [InlineData("Kyocera ECOSYS P5026cdn", "Kyocera")]
    [InlineData("KYOCERA TASKalfa 2553ci", "Kyocera")]
    [InlineData("Lexmark MX317", "Lexmark")]
    [InlineData("SHARP MX-3050N", "Sharp")]
    [InlineData("OKI MC363dn", "OKI")]
    [InlineData("TOSHIBA e-STUDIO 2515AC", "Toshiba")]
    [InlineData("FUJI XEROX DocuCentre-V C2265", "Fujifilm")]
    [InlineData("Fujifilm ApeosPort-VII C3373", "Fujifilm")]
    [InlineData("KONICA MINOLTA bizhub C368", "Konica Minolta")]
    [InlineData("Samsung SCX-3405 Series", "Samsung")]
    [InlineData("Pantum P2500W Series", "Pantum")]
    [InlineData("Lenovo M7206W", "Lenovo")]
    [InlineData("Deli DL-886A", "Deli")]
    [InlineData("Jolimark CP-220", "Jolimark")]
    [InlineData("Panasonic KX-MB2170", null)]
    [InlineData("Generic PostScript Level 3 Printer", null)]
    [InlineData("Ghostscript PDF Printer", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Identify_自述文本_映射到品牌规范名(string? text, string? expected) =>
        Assert.Equal(expected, PrinterBrandParser.Identify(text));

    [Fact]
    public void Identify_大小写不敏感() =>
        Assert.Equal("HP", PrinterBrandParser.Identify("hp laserjet 1020"));

    [Fact]
    public void IdentifyFrom_按顺序取第一个可识别品牌()
    {
        Assert.Equal("Brother", PrinterBrandParser.IdentifyFrom(null, "   ", "Brother HL-2270DW", "HP LaserJet"));
        Assert.Null(PrinterBrandParser.IdentifyFrom(null, "Unknown device"));
    }

    [Theory]
    [InlineData("HP LaserJet Pro M404dn", "LaserJet Pro M404dn")]
    [InlineData("Brother HL-2270DW series", "HL-2270DW series")]
    [InlineData("EPSON L3150 Series", "L3150 Series")]
    [InlineData("KYOCERA TASKalfa 2553ci", "TASKalfa 2553ci")]
    public void ExtractModel_去掉品牌前缀后保留型号(string text, string expected) =>
        Assert.Equal(expected, PrinterBrandParser.ExtractModel(text));

    [Fact]
    public void ExtractModel_无法识别品牌时_返回原文()
    {
        Assert.Equal("Unknown Network Device", PrinterBrandParser.ExtractModel("Unknown Network Device"));
        Assert.Equal(string.Empty, PrinterBrandParser.ExtractModel(null));
    }

    [Fact]
    public void DiscoveryText_Id与来源合并规则稳定()
    {
        Assert.Equal("net:192.168.1.20", DiscoveryText.BuildNetworkId("192.168.1.20"));
        Assert.Equal("net:printer.local", DiscoveryText.BuildNetworkId("[printer.local]"));
        Assert.Equal("SNMP+mDNS", DiscoveryText.MergeSources("mDNS", "SNMP"));
        Assert.Equal("SNMP+mDNS", DiscoveryText.MergeSources("SNMP+mDNS", "SNMP"));
    }
}
