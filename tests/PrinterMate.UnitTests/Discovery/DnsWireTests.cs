using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>DNS 报文编解码与名称压缩指针解析的测试。</summary>
public sealed class DnsWireTests
{
    /// <summary>
    /// 问题段 "_ipp._tcp.local" PTR IN;回答段名称用 0xC0 指针回指偏移 12,
    /// 其 PTR 数据里 "_ipp._tcp.local" 同样用指针压缩。
    /// </summary>
    private const string CompressedPacketHex =
        "1234" + "8400" + "0001" + "0001" + "0000" + "0000" +
        "045F697070" + "045F746370" + "056C6F63616C" + "00" +
        "000C" + "0001" +
        "C00C" + "000C" + "0001" + "00000078" + "000C" +
        "09" + "4D795072696E746572" + "C00C";

    [Fact]
    public void ReadName_含压缩指针_解析出完整域名并正确推进偏移()
    {
        var packet = Convert.FromHexString(CompressedPacketHex);
        var offset = 12;

        Assert.Equal("_ipp._tcp.local", DnsWire.ReadName(packet, ref offset));
        Assert.Equal(29, offset);
    }

    [Fact]
    public void TryParse_含压缩指针的报文_解析出问题段与资源记录()
    {
        var packet = Convert.FromHexString(CompressedPacketHex);

        Assert.True(DnsWire.TryParse(packet, out var message));
        Assert.NotNull(message);

        Assert.True(message!.IsResponse);
        Assert.Equal(0x1234, message.Id);
        Assert.Single(message.Questions);
        Assert.Equal("_ipp._tcp.local", message.Questions[0].Name);
        Assert.Equal(DnsWire.TypePTR, message.Questions[0].Type);
        Assert.Equal(DnsWire.ClassIN, message.Questions[0].Class);

        var answer = Assert.Single(message.Answers);
        Assert.Equal("_ipp._tcp.local", answer.Name);           // 通过 0xC0 指针解析
        Assert.Equal(DnsWire.TypePTR, answer.Type);
        Assert.Equal(120u, answer.Ttl);
        Assert.Equal("MyPrinter._ipp._tcp.local", answer.DomainName); // 数据段内部同样使用指针
    }

    [Fact]
    public void ReadName_压缩指针成环_抛出格式异常()
    {
        var packet = new byte[] { 0xC0, 0x00 };
        var offset = 0;

        Assert.Throws<FormatException>(() => DnsWire.ReadName(packet, ref offset));
    }

    [Fact]
    public void ReadName_指针越界_抛出格式异常()
    {
        var packet = new byte[] { 0xC0, 0x7F };
        var offset = 0;

        Assert.Throws<FormatException>(() => DnsWire.ReadName(packet, ref offset));
    }

    [Fact]
    public void EncodeName_点分名称_按长度前缀标签编码()
    {
        Assert.Equal("045F697070045F746370056C6F63616C00", Convert.ToHexString(DnsWire.EncodeName("_ipp._tcp.local")));
        Assert.Equal("00", Convert.ToHexString(DnsWire.EncodeName(".")));
        Assert.Equal("00", Convert.ToHexString(DnsWire.EncodeName(string.Empty)));
    }

    [Fact]
    public void EncodeQuery_再解析_问题段一致()
    {
        var packet = DnsWire.EncodeQuery(
            0x1234,
            [
                new DnsQuestion("_ipp._tcp.local", DnsWire.TypePTR, DnsWire.ClassIN),
                new DnsQuestion("_printer._tcp.local", DnsWire.TypePTR, DnsWire.ClassIN),
            ]);

        Assert.True(DnsWire.TryParse(packet, out var message));
        Assert.NotNull(message);
        Assert.False(message!.IsResponse);
        Assert.Equal(2, message.Questions.Count);
        Assert.Equal("_ipp._tcp.local", message.Questions[0].Name);
        Assert.Equal("_printer._tcp.local", message.Questions[1].Name);
        Assert.Equal(DnsWire.TypePTR, message.Questions[1].Type);
        Assert.Empty(message.Answers);
    }

    [Fact]
    public void TryParse_任意截断的报文_不抛异常()
    {
        var full = DnsWire.EncodeQuery(0x0000, [new DnsQuestion("_ipp._tcp.local", DnsWire.TypePTR, DnsWire.ClassIN)]);

        for (var length = 0; length < full.Length; length++)
        {
            Assert.False(DnsWire.TryParse(full[..length], out _));
        }
    }

    [Fact]
    public void TryParse_空输入_返回false且不抛异常() => Assert.False(DnsWire.TryParse(null, out _));
}
