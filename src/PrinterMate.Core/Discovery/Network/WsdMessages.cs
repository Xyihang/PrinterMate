using System.ComponentModel;
using System.Xml;
using System.Xml.Linq;

namespace PrinterMate.Core.Discovery;

/// <summary>一条 WS-Discovery ProbeMatch。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record WsdDevice(
    string EndpointAddress,
    string Types,
    string Scopes,
    IReadOnlyList<string> XAddrs,
    int MetadataVersion);

/// <summary>WS-Discovery 的 SOAP 报文构造与解析。纯函数,可用字符串直接测试。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class WsdMessages
{
    public const string DiscoveryNamespace = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    public const string AddressingNamespace = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    public const string SoapNamespace = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>构造一个 Probe 报文。messageId 为空时自动生成 GUID。</summary>
    public static string BuildProbe(string? messageId = null)
    {
        var id = string.IsNullOrWhiteSpace(messageId) ? "urn:uuid:" + Guid.NewGuid().ToString("D") : messageId.Trim();

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="{SoapNamespace}" xmlns:a="{AddressingNamespace}">
              <s:Header>
                <a:Action s:mustUnderstand="1">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
                <a:MessageID>{id}</a:MessageID>
                <a:To s:mustUnderstand="1">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
              </s:Header>
              <s:Body>
                <d:Probe xmlns:d="{DiscoveryNamespace}">
                  <d:Types xmlns:dp0="http://schemas.microsoft.com/windows/2006/08/wdp/print">dp0:PrintDeviceType</d:Types>
                </d:Probe>
              </s:Body>
            </s:Envelope>
            """;
    }

    /// <summary>解析 ProbeMatches 报文;命名空间无关(按元素本地名匹配),非法 XML 返回空列表。</summary>
    public static IReadOnlyList<WsdDevice> ParseProbeMatches(string? xml)
    {
        var devices = new List<WsdDevice>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return devices;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return devices;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return devices;
        }

        foreach (var match in document.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var address = FirstValue(match, "Address") ?? string.Empty;
            var types = FirstValue(match, "Types") ?? string.Empty;
            var scopes = FirstValue(match, "Scopes") ?? string.Empty;
            var xaddrs = (FirstValue(match, "XAddrs") ?? string.Empty)
                .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            _ = int.TryParse(FirstValue(match, "MetadataVersion"), out var metadataVersion);

            devices.Add(new WsdDevice(address, types, scopes, xaddrs, metadataVersion));
        }

        return devices;
    }

    /// <summary>是否看起来是打印设备:Types/Scopes 含 print 或 wsdp:device。</summary>
    public static bool IsPrinterDevice(WsdDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var haystack = device.Types + " " + device.Scopes;
        return haystack.Contains("print", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("wsdp:device", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 XAddrs 中取出可用的主机名或 IP;取不到时返回 null。</summary>
    public static string? ExtractHost(WsdDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        foreach (var xaddr in device.XAddrs)
        {
            if (Uri.TryCreate(xaddr, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }
        }

        return null;
    }

    /// <summary>尽量从 Scopes 里猜一个友好的设备名;猜不到返回 null。</summary>
    public static string? GuessName(WsdDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var scopes = device.Scopes.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = scopes.Length - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            var candidate = scope;
            if (Uri.TryCreate(scope, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Scheme, "urn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (uri.Segments.Length > 0)
                {
                    candidate = Uri.UnescapeDataString(uri.Segments[^1]).Trim('/');
                }
            }

            if (candidate.Length is > 1 and <= 80 && candidate.Any(char.IsLetter))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FirstValue(XElement parent, string localName) =>
        DiscoveryText.NullIfBlank(parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value);
}
