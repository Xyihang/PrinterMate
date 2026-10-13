using System.Net;
using System.Text;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>mDNS PTR/SRV/TXT/A 记录解析与设备映射测试。报文由测试本地逐字节构造,不共用被测编码器。</summary>
public sealed class MdnsRecordTests
{
    [Fact]
    public void MapRecords_解析PTR_SRV_TXT_A并生成设备()
    {
        var datagram = BuildResponse();

        Assert.True(DnsWire.TryParse(datagram, out var message));
        var parsed = message!;
        Assert.True(parsed.IsResponse);
        Assert.Equal(3, parsed.Answers.Count);
        Assert.Single(parsed.Additional);

        Assert.Equal(DnsWire.TypePTR, parsed.Answers[0].Type);
        Assert.Equal("_ipp._tcp.local", parsed.Answers[0].Name);
        Assert.Equal("Brother HL-2270DW._ipp._tcp.local", parsed.Answers[0].DomainName);

        Assert.Equal(DnsWire.TypeSRV, parsed.Answers[1].Type);
        Assert.Equal("Brother HL-2270DW._ipp._tcp.local", parsed.Answers[1].Name);
        Assert.Equal(631, parsed.Answers[1].Port!.Value);
        Assert.Equal("BRN001122334455.local", parsed.Answers[1].Target);

        Assert.Equal(DnsWire.TypeTXT, parsed.Answers[2].Type);
        Assert.NotNull(parsed.Answers[2].Text);
        Assert.Contains("usb_MDL=HL-2270DW", parsed.Answers[2].Text!);
        Assert.Contains("URF=none", parsed.Answers[2].Text!);

        Assert.Equal(DnsWire.TypeA, parsed.Additional[0].Type);
        Assert.Equal("192.168.1.20", parsed.Additional[0].Address!.ToString());

        var printer = Assert.Single(MdnsDiscovery.MapRecords(parsed.AllRecords));
        Assert.Equal("192.168.1.20", printer.Address);
        Assert.Equal("net:192.168.1.20", printer.Id);
        Assert.Equal("Brother", printer.Vendor);
        Assert.Equal("HL-2270DW", printer.Model);
        Assert.Equal("Front Desk", printer.DisplayName);
        Assert.Equal("ipp://192.168.1.20:631/ipp/print", printer.IppUri);
        Assert.True(printer.SupportsIpp);
        Assert.True(printer.IsMopria);
        Assert.Equal(ConnectionKind.Ipp, printer.Connection);
        Assert.Equal(DriverReadiness.DriverFree, printer.Readiness);
        Assert.Equal("mDNS", printer.DiscoverySource);
    }

    [Fact]
    public void MapRecords_非打印服务_被忽略()
    {
        DnsResourceRecord[] records =
        [
            new("_http._tcp.local", DnsWire.TypePTR, DnsWire.ClassIN, 120, DomainName: "Web._http._tcp.local"),
        ];

        Assert.Empty(MdnsDiscovery.MapRecords(records));
    }

    [Fact]
    public void MapInstance_含私有PDL_不标记为Mopria()
    {
        var txt = new DnsResourceRecord(
            "P._ipp._tcp.local",
            DnsWire.TypeTXT,
            DnsWire.ClassIN,
            120,
            Text: ["ty=HP LaserJet Pro M404dn", "URF=none", "pdl=application/vnd.hp-PCL"]);

        var printer = MdnsDiscovery.MapInstance("P._ipp._tcp.local", "_ipp._tcp.local", null, txt, IPAddress.Parse("10.0.0.5"));

        Assert.False(printer.IsMopria);
        Assert.Equal(DriverReadiness.NeedsDriver, printer.Readiness);
        Assert.Equal("10.0.0.5", printer.Address);
    }

    [Fact]
    public void MapInstance_含URF且无私有PDL_标记为Mopria免驱()
    {
        var txt = new DnsResourceRecord(
            "P._ipp._tcp.local",
            DnsWire.TypeTXT,
            DnsWire.ClassIN,
            120,
            Text: ["ty=Brother HL-L2350DW", "URF=none", "pdl=application/octet-stream,image/urf", "rp=ipp/print"]);

        var printer = MdnsDiscovery.MapInstance("P._ipp._tcp.local", "_ipp._tcp.local", null, txt, IPAddress.Parse("10.0.0.5"));

        Assert.True(printer.IsMopria);
        Assert.Equal("Brother", printer.Vendor);
        Assert.Equal("ipp://10.0.0.5:631/ipp/print", printer.IppUri);
    }

    [Fact]
    public void MapInstance_printer服务_映射为LPD连接()
    {
        var srv = new DnsResourceRecord(
            "Legacy._printer._tcp.local",
            DnsWire.TypeSRV,
            DnsWire.ClassIN,
            120,
            Priority: 0,
            Weight: 0,
            Port: 515,
            Target: "legacy.local");

        var printer = MdnsDiscovery.MapInstance("Legacy._printer._tcp.local", "_printer._tcp.local", srv, null, IPAddress.Parse("10.0.0.6"));

        Assert.Equal(ConnectionKind.Lpd, printer.Connection);
        Assert.True(printer.SupportsLpd);
        Assert.False(printer.SupportsIpp);
        Assert.Null(printer.IppUri);
        Assert.Equal("legacy.local", printer.Address);
    }

    [Fact]
    public void ParseTxt_无等号条目_视为标志位()
    {
        var txt = new DnsResourceRecord("X._ipp._tcp.local", DnsWire.TypeTXT, DnsWire.ClassIN, 120, Text: ["URF=none", "air", "ty=Test"]);

        var values = MdnsDiscovery.ParseTxt(txt);

        Assert.True(values.ContainsKey("URF"));
        Assert.True(values.ContainsKey("urf"));
        Assert.Equal(string.Empty, values["air"]);
        Assert.Equal("Test", values["ty"]);
    }

    /// <summary>
    /// 组装一份完整的 mDNS 响应:PTR(服务→实例) + SRV + TXT + 附加 A 记录,
    /// 实例名使用逐标签编码、SRV/TXT 的 owner 使用压缩指针。
    /// </summary>
    private static byte[] BuildResponse()
    {
        const string instance = "Brother HL-2270DW._ipp._tcp.local";
        const string hostName = "BRN001122334455.local";

        var serviceName = EncodeName("_ipp._tcp.local");
        var instanceName = EncodeName(instance);

        var body = new List<byte>();

        var serviceOffset = 12 + body.Count;
        var instanceOffset = serviceOffset + serviceName.Length + 10;
        WriteRecord(body, serviceName, DnsWire.TypePTR, instanceName);
        Assert.Equal(instanceOffset, 12 + body.Count - instanceName.Length);

        var srvData = new List<byte>();
        srvData.AddRange([0x00, 0x00]); // priority
        srvData.AddRange([0x00, 0x00]); // weight
        srvData.AddRange([0x02, 0x77]); // port 631
        srvData.AddRange(EncodeName(hostName));
        WriteRecord(body, Pointer(instanceOffset), DnsWire.TypeSRV, srvData.ToArray());

        var txtData = new List<byte>();
        string[] entries =
        [
            "txtvers=1",
            "ty=Brother HL-2270DW",
            "usb_MDL=HL-2270DW",
            "usb_MFG=Brother",
            "rp=ipp/print",
            "URF=none",
            "note=Front Desk",
            "pdl=application/octet-stream,image/urf",
        ];
        foreach (var entry in entries)
        {
            var encoded = Encoding.UTF8.GetBytes(entry);
            txtData.Add((byte)encoded.Length);
            txtData.AddRange(encoded);
        }

        WriteRecord(body, Pointer(instanceOffset), DnsWire.TypeTXT, txtData.ToArray());

        WriteRecord(body, EncodeName(hostName), DnsWire.TypeA, [192, 168, 1, 20]);

        var packet = new List<byte>();
        packet.AddRange([0x00, 0x00]); // id
        packet.AddRange([0x84, 0x00]); // flags:response + authoritative
        packet.AddRange([0x00, 0x00]); // qdcount
        packet.AddRange([0x00, 0x03]); // ancount
        packet.AddRange([0x00, 0x00]); // nscount
        packet.AddRange([0x00, 0x01]); // arcount
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static void WriteRecord(List<byte> target, byte[] owner, int type, byte[] rdata)
    {
        target.AddRange(owner);
        target.Add((byte)(type >> 8));
        target.Add((byte)type);
        target.AddRange([0x00, 0x01]);            // class IN
        target.AddRange([0x00, 0x00, 0x00, 0x78]); // ttl 120
        target.Add((byte)(rdata.Length >> 8));
        target.Add((byte)rdata.Length);
        target.AddRange(rdata);
    }

    private static byte[] EncodeName(string dotted)
    {
        var bytes = new List<byte>();
        foreach (var label in dotted.Split('.'))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] Pointer(int offset) => [(byte)(0xC0 | (offset >> 8)), (byte)offset];
}
