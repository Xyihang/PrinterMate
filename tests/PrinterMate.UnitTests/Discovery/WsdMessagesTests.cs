using System.Xml.Linq;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>WS-Discovery SOAP 报文构造与 ProbeMatches 解析测试(直接喂 XML 字符串)。</summary>
public sealed class WsdMessagesTests
{
    private const string ProbeMatchesXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                    xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">
          <s:Header />
          <s:Body>
            <d:ProbeMatches xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <d:ProbeMatch>
                <a:EndpointReference>
                  <a:Address>urn:uuid:12345678-1234-1234-1234-1234567890ab</a:Address>
                </a:EndpointReference>
                <d:Types>wsdp:Device http://schemas.microsoft.com/windows/2006/08/wdp/print:PrintDeviceType</d:Types>
                <d:Scopes>ldap:///ou=engineering,o=example</d:Scopes>
                <d:XAddrs>http://192.168.1.30:5357/8a1b/</d:XAddrs>
                <d:MetadataVersion>1</d:MetadataVersion>
              </d:ProbeMatch>
            </d:ProbeMatches>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void ParseProbeMatches_已知报文_解析出地址类型范围与XAddrs()
    {
        var devices = WsdMessages.ParseProbeMatches(ProbeMatchesXml);

        var device = Assert.Single(devices);
        Assert.Equal("urn:uuid:12345678-1234-1234-1234-1234567890ab", device.EndpointAddress);
        Assert.Contains("PrintDeviceType", device.Types);
        Assert.Equal("ldap:///ou=engineering,o=example", device.Scopes);
        Assert.Equal("http://192.168.1.30:5357/8a1b/", Assert.Single(device.XAddrs));
        Assert.Equal(1, device.MetadataVersion);
        Assert.True(WsdMessages.IsPrinterDevice(device));
        Assert.Equal("192.168.1.30", WsdMessages.ExtractHost(device));
    }

    [Fact]
    public void MapDevices_打印设备_映射为WSD连接()
    {
        var device = Assert.Single(WsdMessages.ParseProbeMatches(ProbeMatchesXml));

        var printer = Assert.Single(WsdDiscovery.MapDevices([device]));

        Assert.Equal("192.168.1.30", printer.Address);
        Assert.Equal("net:192.168.1.30", printer.Id);
        Assert.Equal(ConnectionKind.Wsd, printer.Connection);
        Assert.Equal("WS-Discovery", printer.DiscoverySource);
    }

    [Fact]
    public void MapDevices_非打印设备_被过滤()
    {
        var device = new WsdDevice("urn:uuid:1", "scan:ScanDeviceType", string.Empty, ["http://10.0.0.9:5357/x/"], 1);

        Assert.False(WsdMessages.IsPrinterDevice(device));
        Assert.Empty(WsdDiscovery.MapDevices([device]));
    }

    [Fact]
    public void MapDevices_无XAddrs_被过滤()
    {
        var device = new WsdDevice("urn:uuid:1", "print:PrintDeviceType", string.Empty, [], 1);

        Assert.Null(WsdMessages.ExtractHost(device));
        Assert.Empty(WsdDiscovery.MapDevices([device]));
    }

    [Fact]
    public void ParseProbeMatches_多个ProbeMatch_全部解析()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <s:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <a:EndpointReference><a:Address>urn:uuid:aaa</a:Address></a:EndpointReference>
                    <d:Types>print:PrintDeviceType</d:Types>
                    <d:XAddrs>http://10.0.0.1:5357/a/</d:XAddrs>
                  </d:ProbeMatch>
                  <d:ProbeMatch>
                    <a:EndpointReference><a:Address>urn:uuid:bbb</a:Address></a:EndpointReference>
                    <d:Types>print:PrintDeviceType</d:Types>
                    <d:XAddrs>http://10.0.0.2:5357/b/</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </s:Body>
            </s:Envelope>
            """;

        var devices = WsdMessages.ParseProbeMatches(xml);

        Assert.Equal(2, devices.Count);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, devices.Select(WsdMessages.ExtractHost).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<a><b></a>")]
    [InlineData("<Envelope><Body><ProbeMatches>")]
    public void ParseProbeMatches_非法XML_返回空列表(string xml) =>
        Assert.Empty(WsdMessages.ParseProbeMatches(xml));

    [Fact]
    public void BuildProbe_可解析且含必要元素()
    {
        var probe = WsdMessages.BuildProbe("urn:uuid:test-id");
        var document = XDocument.Parse(probe);

        Assert.Equal("Envelope", document.Root!.Name.LocalName);
        Assert.Equal(
            "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe",
            document.Descendants().First(e => e.Name.LocalName == "Action").Value);
        Assert.Equal(
            "urn:uuid:test-id",
            document.Descendants().First(e => e.Name.LocalName == "MessageID").Value);
        Assert.Contains("Probe", document.Descendants().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void BuildProbe_未指定消息号_自动生成GUID() =>
        Assert.Contains("urn:uuid:", WsdMessages.BuildProbe());

    [Fact]
    public void Name_为协议中文名()
    {
        Assert.Equal("WS-Discovery", new WsdDiscovery().Name);
        Assert.Equal("mDNS / DNS-SD", new MdnsDiscovery().Name);
    }
}
