using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Core;

public class DeviceNameParserTests
{
    [Theory]
    [InlineData("HP Deskjet 2520 series (USB003)", "HP Deskjet 2520")]
    [InlineData("Samsung ML-2160 Series (USB007)", "Samsung ML-2160")]
    [InlineData("Canon MG6300 series Printer WSD-07c8d46c", "Canon MG6300 WSD-07c8d46c")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 清洗型号名(string? input, string expected)
    {
        Assert.Equal(expected, DeviceNameParser.CleanModelName(input));
    }

    [Theory]
    [InlineData("USB003", ConnectionKind.Usb)]
    [InlineData("USBPRINT", ConnectionKind.Usb)]
    [InlineData("WSD-07c8d46c-ea73-450a-834e-8646c7eb6931", ConnectionKind.Wsd)]
    [InlineData("192.168.1.20", ConnectionKind.RawTcp)]
    [InlineData("IP_192.168.1.20", ConnectionKind.RawTcp)]
    [InlineData("ipp://192.168.1.20/ipp/print", ConnectionKind.Ipp)]
    [InlineData(@"\\SERVER\HP LaserJet", ConnectionKind.Shared)]
    [InlineData("PORTPROMPT:", ConnectionKind.LocalQueue)]
    [InlineData("LPT1:", ConnectionKind.LocalQueue)]
    [InlineData("", ConnectionKind.Unknown)]
    public void 端口名推断连接方式(string port, ConnectionKind expected)
    {
        Assert.Equal(expected, DeviceNameParser.GuessConnection(port));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("IP_10.0.0.5", "10.0.0.5")]
    [InlineData("USB003", null)]
    [InlineData(null, null)]
    public void 从端口名提取地址(string? port, string? expected)
    {
        Assert.Equal(expected, DeviceNameParser.ExtractAddress(port));
    }

    [Theory]
    [InlineData("HP", null, "HP")]
    [InlineData(null, "Seiko Epson Corporation", "Epson")]
    [InlineData(null, "Brother Industries, Ltd.", "Brother")]
    [InlineData(null, "KYOCERA Document Solutions", "Kyocera")]
    [InlineData(null, "奔图打印", "Pantum")]
    [InlineData(null, "映美打印机", "Jolimark")]
    [InlineData(null, "完全未知的厂商", "")]
    public void 品牌识别(string? vendor, string? name, string expected)
    {
        Assert.Equal(expected, DeviceNameParser.GuessVendor(vendor, name));
    }

    [Fact]
    public void 型号拼接不应重复厂商名()
    {
        Assert.Equal("HP Deskjet 2520", DeviceNameParser.BuildModelForMatching("HP", "HP Deskjet 2520 series"));
        Assert.Equal("HP Deskjet 2520", DeviceNameParser.BuildModelForMatching("HP", "Deskjet 2520 series"));
    }
}

public class UsbPrinterLocatorTests
{
    [Theory]
    [InlineData(@"USBPRINT\HPDESKJET_2520_SERIES\7&22D0F0D4&0&USB003", "USB003")]
    [InlineData(@"USBPRINT\SAMSUNGML-2160_SERIES\6&1A2B3C4D&0&USB007", "USB007")]
    [InlineData(@"USBPRINT\HPXXX\7&1&0&usb012", "USB012")]
    [InlineData(@"WSDPRINT\CANONMG6300\ABC", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 从实例ID解析USB端口(string? instanceId, string? expected)
    {
        Assert.Equal(expected, UsbPrinterLocator.ResolvePortName(instanceId));
    }

    [Theory]
    [InlineData(@"USBPRINT\HPXXX\7&1&0&USB001", true)]
    [InlineData(@"WSDPRINT\CANONXXX\ABC", false)]
    [InlineData("", false)]
    public void 判定是否为USB打印机(string instanceId, bool expected)
    {
        Assert.Equal(expected, UsbPrinterLocator.IsUsbPrinter(instanceId));
    }

    [Fact]
    public void USB端口已存在时应沿用现有端口_而不是重复创建()
    {
        var spec = UsbPrinterLocator.BuildPortSpec("USB003", portExists: true);

        Assert.Equal(PortKind.Existing, spec.Kind);
        Assert.Equal("USB003", spec.PortName);
    }

    [Fact]
    public void USB端口缺失时应标记为USB类型_由PnP创建()
    {
        var spec = UsbPrinterLocator.BuildPortSpec("USB003", portExists: false);

        Assert.Equal(PortKind.Usb, spec.Kind);
        Assert.Equal("USB003", spec.PortName);
    }

    [Fact]
    public void USB端口类型的文案应说明由PnP自动创建()
    {
        var spec = new PortSpec(PortKind.Usb, "USB003");

        Assert.Contains("PnP", spec.KindText);
    }
}
