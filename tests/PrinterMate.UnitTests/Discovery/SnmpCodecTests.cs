using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>SNMP v2c 手工 BER 编解码的纯函数测试(不依赖网络)。</summary>
public sealed class SnmpCodecTests
{
    /// <summary>
    /// request-id=0x11223344、community="public"、四个打印机 OID 的 GetRequest 规范字节序列。
    /// 结构:SEQUENCE { INTEGER version=1, OCTET STRING "public", [0] { INTEGER id, INTEGER 0, INTEGER 0,
    /// SEQUENCE { SEQUENCE { OID, NULL } × 4 } } }。
    /// </summary>
    private const string ExpectedGetRequestHex =
        "3059020101" +
        "04067075626C6963" +
        "A04C" +
        "020411223344" +
        "020100" +
        "020100" +
        "303E" +
        "300C06082B06010201010100" + "0500" +
        "300C06082B06010201010500" + "0500" +
        "300F060B2B06010201190302010301" + "0500" +
        "300F060B2B060102012B0501011001" + "0500";

    [Fact]
    public void EncodeGetRequest_固定输入_产生规范的BER字节序列()
    {
        var bytes = SnmpCodec.EncodeGetRequest(0x11223344, "public", SnmpCodec.PrinterOids);

        Assert.Equal(ExpectedGetRequestHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public void EncodeGetRequest_报文长度字段与实际内容一致()
    {
        var bytes = SnmpCodec.EncodeGetRequest(0x01020304, "public", SnmpCodec.PrinterOids);

        Assert.Equal(0x30, bytes[0]);
        Assert.Equal(0x59, bytes[1]);
        Assert.Equal(bytes.Length - 2, 0x59);
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(91, "5B")]
    [InlineData(127, "7F")]
    [InlineData(128, "8180")]
    [InlineData(255, "81FF")]
    [InlineData(256, "820100")]
    [InlineData(65535, "82FFFF")]
    public void EncodeLength_按BER规则编码(int length, string expectedHex) =>
        Assert.Equal(expectedHex, Convert.ToHexString(SnmpCodec.EncodeLength(length)));

    [Fact]
    public void EncodeOid_已知OID_字节序列正确()
    {
        Assert.Equal("2B06010201010100", Convert.ToHexString(SnmpCodec.EncodeOid("1.3.6.1.2.1.1.1.0")));
        Assert.Equal("2B06010201010500", Convert.ToHexString(SnmpCodec.EncodeOid("1.3.6.1.2.1.1.5.0")));
        Assert.Equal("2B06010201190302010301", Convert.ToHexString(SnmpCodec.EncodeOid("1.3.6.1.2.1.25.3.2.1.3.1")));
        Assert.Equal("2B060102012B0501011001", Convert.ToHexString(SnmpCodec.EncodeOid("1.3.6.1.2.1.43.5.1.1.16.1")));
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.1.1.0")]
    [InlineData("1.3.6.1.2.1.1.5.0")]
    [InlineData("1.3.6.1.2.1.25.3.2.1.3.1")]
    [InlineData("1.3.6.1.2.1.43.5.1.1.16.1")]
    [InlineData("1.3.6.1.4.1.367.1.1")]
    [InlineData("1.3.6.1.2.1.2.2.1.2.128")]
    public void EncodeOid_与DecodeOid_互为逆运算(string oid) =>
        Assert.Equal(oid, SnmpCodec.DecodeOid(SnmpCodec.EncodeOid(oid)));

    /// <summary>
    /// 手工构造的 GetResponse:sysDescr="HP LaserJet Pro M404dn"、sysName="OFFICE-HP"。
    /// </summary>
    [Fact]
    public void TryParseMessage_已知GetResponse_解析出sysDescr与厂商()
    {
        var datagram = Convert.FromHexString(
            "3056" +
            "020101" +
            "04067075626C6963" +
            "A249" +
            "020411223344" +
            "020100" +
            "020100" +
            "303B" +
            "3022" + "06082B06010201010100" + "0416" + "4850204C617365724A65742050726F204D343034646E" +
            "3015" + "06082B06010201010500" + "0409" + "4F46464943452D4850");

        Assert.True(SnmpCodec.TryParseMessage(datagram, out var message));
        Assert.NotNull(message);
        Assert.Equal(SnmpCodec.VersionV2C, message!.Version);
        Assert.Equal("public", message.Community);
        Assert.Equal(SnmpCodec.TagGetResponse, message.PduTag);
        Assert.Equal(0x11223344, message.RequestId);
        Assert.Equal(0, message.ErrorStatus);
        Assert.Equal(2, message.VarBinds.Count);
        Assert.Equal(0x04, message.VarBinds[0].ValueTag);

        Assert.Equal("HP LaserJet Pro M404dn", message.SysDescr);
        Assert.Equal("OFFICE-HP", message.SysName);
        Assert.Equal("HP", PrinterBrandParser.Identify(message.SysDescr));
    }

    [Fact]
    public void MapMessage_从解析结果映射出设备记录()
    {
        var datagram = Convert.FromHexString(
            "3056" +
            "020101" +
            "04067075626C6963" +
            "A249" +
            "020411223344" +
            "020100" +
            "020100" +
            "303B" +
            "3022" + "06082B06010201010100" + "0416" + "4850204C617365724A65742050726F204D343034646E" +
            "3015" + "06082B06010201010500" + "0409" + "4F46464943452D4850");

        Assert.True(SnmpCodec.TryParseMessage(datagram, out var message));
        var printer = SnmpDiscovery.MapMessage(message!, "192.168.1.50");

        Assert.NotNull(printer);
        Assert.Equal("HP", printer!.Vendor);
        Assert.Equal("LaserJet Pro M404dn", printer.Model);
        Assert.Equal("192.168.1.50", printer.Address);
        Assert.Equal("net:192.168.1.50", printer.Id);
        Assert.Equal("SNMP", printer.DiscoverySource);
        Assert.Equal(ConnectionKind.Unknown, printer.Connection);
    }

    [Fact]
    public void MapMessage_非打印机设备_返回null()
    {
        // 只有 sysDescr 且内容不是任何已知打印机品牌(交换机/路由器)。
        var message = new SnmpMessage(
            SnmpCodec.VersionV2C,
            "public",
            SnmpCodec.TagGetResponse,
            1,
            0,
            0,
            [new SnmpVarBind(SnmpCodec.OidSysDescr, SnmpCodec.TagOctetString, [], "Cisco IOS Software")]);

        Assert.Null(SnmpDiscovery.MapMessage(message, "10.0.0.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("FF00")]
    [InlineData("3059020101")]
    [InlineData("305602010104067075626C6963A249000000000000000000")]
    public void TryParseMessage_非法报文_返回false(string hex) =>
        Assert.False(SnmpCodec.TryParseMessage(Convert.FromHexString(hex), out var message));

    [Fact]
    public void TryParseMessage_空输入_返回false且不抛异常() =>
        Assert.False(SnmpCodec.TryParseMessage(null, out _));

    [Fact]
    public void RenderValue_通用类型_渲染为可读文本()
    {
        Assert.Equal("42", SnmpCodec.RenderValue(SnmpCodec.TagInteger, [0x2A]));
        Assert.Equal("4294967295", SnmpCodec.RenderValue(SnmpCodec.TagCounter64, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal("255.255.255.0", SnmpCodec.RenderValue(SnmpCodec.TagIpAddress, [255, 255, 255, 0]));
        Assert.Equal("1.3.6.1.2.1.1.1.0", SnmpCodec.RenderValue(SnmpCodec.TagObjectIdentifier, SnmpCodec.EncodeOid("1.3.6.1.2.1.1.1.0")));
        Assert.Null(SnmpCodec.RenderValue(SnmpCodec.TagNull, []));
        Assert.Equal("HP LaserJet", SnmpCodec.RenderValue(SnmpCodec.TagOctetString, "HP LaserJet"u8.ToArray()));
    }

    [Fact]
    public void BuildTargets_始终包含受限广播地址并且只使用161端口()
    {
        var targets = SnmpDiscovery.BuildTargets(new DiscoveryOptions(TimeSpan.FromSeconds(1)));

        Assert.Contains(targets, t => t.Address.Equals(System.Net.IPAddress.Broadcast));
        Assert.All(targets, t => Assert.Equal(161, t.Port));
    }

    [Fact]
    public void Name_为中文名() => Assert.Equal("SNMP 广播", new SnmpDiscovery().Name);
}
