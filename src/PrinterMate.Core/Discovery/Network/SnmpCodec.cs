using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;

namespace PrinterMate.Core.Discovery;

/// <summary>一条 SNMP varbind:OID + 原始值字节 + 便于展示的文本形式。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record SnmpVarBind(string Oid, byte ValueTag, byte[] RawValue, string? Text);

/// <summary>解析后的 SNMP 报文(请求或响应)。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record SnmpMessage(
    int Version,
    string Community,
    byte PduTag,
    int RequestId,
    int ErrorStatus,
    int ErrorIndex,
    IReadOnlyList<SnmpVarBind> VarBinds)
{
    private const string OidSysDescr = "1.3.6.1.2.1.1.1.0";
    private const string OidSysName = "1.3.6.1.2.1.1.5.0";
    private const string OidHrDeviceDescrPrefix = "1.3.6.1.2.1.25.3.2.1.3.";
    private const string OidPrtGeneralPrinterNamePrefix = "1.3.6.1.2.1.43.5.1.1.16.";

    /// <summary>设备描述。</summary>
    public string? SysDescr => FindText(OidSysDescr);

    /// <summary>设备名。</summary>
    public string? SysName => FindText(OidSysName);

    /// <summary>硬件设备描述(hrDeviceDescr 表)。</summary>
    public string? HrDeviceDescr => FindTextByPrefix(OidHrDeviceDescrPrefix);

    /// <summary>打印机名称(prtGeneralPrinterName 表)。</summary>
    public string? PrinterName => FindTextByPrefix(OidPrtGeneralPrinterNamePrefix);

    public string? FindText(string oid)
    {
        foreach (var bind in VarBinds)
        {
            if (string.Equals(bind.Oid, oid, StringComparison.Ordinal))
            {
                return bind.Text;
            }
        }

        return null;
    }

    public string? FindTextByPrefix(string oidPrefix)
    {
        foreach (var bind in VarBinds)
        {
            if (bind.Oid.StartsWith(oidPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(bind.Text))
            {
                return bind.Text;
            }
        }

        return null;
    }
}

/// <summary>
/// SNMP v2c 的 BER/ASN.1 手工编解码(无第三方依赖)。全部为纯函数,便于单元测试。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SnmpCodec
{
    public const byte TagInteger = 0x02;
    public const byte TagOctetString = 0x04;
    public const byte TagNull = 0x05;
    public const byte TagObjectIdentifier = 0x06;
    public const byte TagIpAddress = 0x40;
    public const byte TagCounter32 = 0x41;
    public const byte TagGauge32 = 0x42;
    public const byte TagTimeTicks = 0x43;
    public const byte TagOpaque = 0x44;
    public const byte TagCounter64 = 0x45;

    public const byte TagSequence = 0x30;
    public const byte TagGetRequest = 0xA0;
    public const byte TagGetNextRequest = 0xA1;
    public const byte TagGetResponse = 0xA2;

    /// <summary>SNMP v2c 的 version 字段取值。</summary>
    public const int VersionV2C = 1;

    public const string OidSysDescr = "1.3.6.1.2.1.1.1.0";
    public const string OidSysName = "1.3.6.1.2.1.1.5.0";
    public const string OidHrDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3.1";
    public const string OidPrtGeneralPrinterName = "1.3.6.1.2.1.43.5.1.1.16.1";

    /// <summary>一次 GetRequest 要读取的四个打印机相关 OID(sysDescr/sysName/hrDeviceDescr/prtGeneralPrinterName)。</summary>
    public static IReadOnlyList<string> PrinterOids { get; } =
        [OidSysDescr, OidSysName, OidHrDeviceDescr, OidPrtGeneralPrinterName];

    /// <summary>
    /// 编码一个 SNMP v2c GetRequest。request-id 固定用 4 字节大端整数编码,保证相同输入得到相同字节序列。
    /// </summary>
    public static byte[] EncodeGetRequest(int requestId, string community, IReadOnlyList<string> oids)
    {
        ArgumentNullException.ThrowIfNull(oids);

        var varBindList = new List<byte>();
        foreach (var oid in oids)
        {
            var varBind = new List<byte>();
            varBind.AddRange(EncodeTlv(TagObjectIdentifier, EncodeOid(oid)));
            varBind.AddRange(EncodeTlv(TagNull, []));
            varBindList.AddRange(EncodeTlv(TagSequence, varBind.ToArray()));
        }

        var pduBody = new List<byte>();
        pduBody.AddRange(EncodeTlv(TagInteger, EncodeInt32(requestId)));
        pduBody.AddRange(EncodeTlv(TagInteger, [0])); // error-status
        pduBody.AddRange(EncodeTlv(TagInteger, [0])); // error-index
        pduBody.AddRange(EncodeTlv(TagSequence, varBindList.ToArray()));

        var message = new List<byte>();
        message.AddRange(EncodeTlv(TagInteger, [VersionV2C]));
        message.AddRange(EncodeTlv(TagOctetString, Encoding.ASCII.GetBytes(community ?? string.Empty)));
        message.AddRange(EncodeTlv(TagGetRequest, pduBody.ToArray()));

        return EncodeTlv(TagSequence, message.ToArray());
    }

    /// <summary>编码 TLV:tag + BER 长度 + 内容。</summary>
    public static byte[] EncodeTlv(byte tag, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lengthBytes = EncodeLength(content.Length);
        var buffer = new byte[1 + lengthBytes.Length + content.Length];
        buffer[0] = tag;
        lengthBytes.CopyTo(buffer, 1);
        content.CopyTo(buffer, 1 + lengthBytes.Length);
        return buffer;
    }

    /// <summary>BER 长度编码:&lt;128 用单字节,否则 0x80|字节数 + 大端长度。</summary>
    public static byte[] EncodeLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "BER 长度不能为负数。");
        }

        if (length < 0x80)
        {
            return [(byte)length];
        }

        var stack = new Stack<byte>();
        var value = length;
        while (value > 0)
        {
            stack.Push((byte)(value & 0xFF));
            value >>= 8;
        }

        var body = stack.ToArray();
        var result = new byte[body.Length + 1];
        result[0] = (byte)(0x80 | body.Length);
        body.CopyTo(result, 1);
        return result;
    }

    /// <summary>request-id 固定编码为 4 字节大端整数。</summary>
    public static byte[] EncodeInt32(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>把点分 OID 编码为 BER 内容字节(不含 tag 与长度)。</summary>
    public static byte[] EncodeOid(string dottedOid)
    {
        if (string.IsNullOrWhiteSpace(dottedOid))
        {
            throw new FormatException("OID 不能为空。");
        }

        var text = dottedOid.Trim().TrimStart('.');
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new FormatException($"OID “{dottedOid}” 至少需要两个子标识符。");
        }

        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
            {
                throw new FormatException($"OID “{dottedOid}” 含非法的子标识符“{parts[i]}”。");
            }
        }

        if (numbers[0] > 2)
        {
            throw new FormatException($"OID “{dottedOid}” 的首个子标识符必须在 0..2 之间。");
        }

        var output = new List<byte> { (byte)((numbers[0] * 40) + numbers[1]) };
        for (var i = 2; i < numbers.Length; i++)
        {
            var value = numbers[i];
            var stack = new Stack<byte>();
            stack.Push((byte)(value & 0x7F));
            value >>= 7;
            while (value > 0)
            {
                stack.Push((byte)(0x80 | (value & 0x7F)));
                value >>= 7;
            }

            output.AddRange(stack);
        }

        return output.ToArray();
    }

    /// <summary>把 BER 内容字节解码回点分 OID。</summary>
    public static string DecodeOid(byte[] content)
    {
        if (content is null || content.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            (content[0] / 40).ToString(CultureInfo.InvariantCulture),
            (content[0] % 40).ToString(CultureInfo.InvariantCulture),
        };

        long value = 0;
        var pending = false;
        for (var i = 1; i < content.Length; i++)
        {
            value = (value << 7) | (uint)(content[i] & 0x7F);
            pending = (content[i] & 0x80) != 0;
            if (!pending)
            {
                parts.Add(value.ToString(CultureInfo.InvariantCulture));
                value = 0;
            }
        }

        if (pending)
        {
            parts.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join('.', parts);
    }

    /// <summary>解析一段完整的 SNMP 报文;格式非法时返回 false 而不是抛异常。</summary>
    public static bool TryParseMessage(byte[]? datagram, out SnmpMessage? message)
    {
        message = null;
        if (datagram is null || datagram.Length < 4)
        {
            return false;
        }

        try
        {
            var offset = 0;
            if (!TryReadTlv(datagram, ref offset, out var outerTag, out var outer) || outerTag != TagSequence)
            {
                return false;
            }

            var cursor = 0;
            if (!TryReadTlv(outer, ref cursor, out var versionTag, out var versionBytes) || versionTag != TagInteger)
            {
                return false;
            }

            if (!TryReadTlv(outer, ref cursor, out var communityTag, out var communityBytes) || communityTag != TagOctetString)
            {
                return false;
            }

            if (!TryReadTlv(outer, ref cursor, out var pduTag, out var pduBody))
            {
                return false;
            }

            if (pduTag is not (TagGetRequest or TagGetNextRequest or TagGetResponse))
            {
                return false;
            }

            var pduCursor = 0;
            if (!TryReadTlv(pduBody, ref pduCursor, out var idTag, out var idBytes) || idTag != TagInteger)
            {
                return false;
            }

            if (!TryReadTlv(pduBody, ref pduCursor, out var statusTag, out var statusBytes) || statusTag != TagInteger)
            {
                return false;
            }

            if (!TryReadTlv(pduBody, ref pduCursor, out var indexTag, out var indexBytes) || indexTag != TagInteger)
            {
                return false;
            }

            if (!TryReadTlv(pduBody, ref pduCursor, out var listTag, out var listBody) || listTag != TagSequence)
            {
                return false;
            }

            var binds = new List<SnmpVarBind>();
            var listCursor = 0;
            while (listCursor < listBody.Length)
            {
                if (!TryReadTlv(listBody, ref listCursor, out var bindTag, out var bindBody) || bindTag != TagSequence)
                {
                    return false;
                }

                var bindCursor = 0;
                if (!TryReadTlv(bindBody, ref bindCursor, out var oidTag, out var oidBytes) || oidTag != TagObjectIdentifier)
                {
                    return false;
                }

                if (!TryReadTlv(bindBody, ref bindCursor, out var valueTag, out var valueBytes))
                {
                    return false;
                }

                var oid = DecodeOid(oidBytes);
                binds.Add(new SnmpVarBind(oid, valueTag, valueBytes, RenderValue(valueTag, valueBytes)));
            }

            message = new SnmpMessage(
                (int)DecodeLong(versionBytes),
                Encoding.UTF8.GetString(communityBytes),
                pduTag,
                (int)DecodeLong(idBytes),
                (int)DecodeLong(statusBytes),
                (int)DecodeLong(indexBytes),
                binds);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>按 ASN.1 类型把 varbind 原始值渲染成可读文本(OCTET STRING 之外的类型也支持)。</summary>
    public static string? RenderValue(byte tag, byte[] raw)
    {
        raw ??= [];
        switch (tag)
        {
            case TagNull:
                return null;
            case TagOctetString:
                return Sanitize(Encoding.UTF8.GetString(raw));
            case TagObjectIdentifier:
                return DecodeOid(raw);
            case TagInteger:
            case TagCounter32:
            case TagGauge32:
            case TagTimeTicks:
                return DecodeLong(raw).ToString(CultureInfo.InvariantCulture);
            case TagCounter64:
                return DecodeUnsigned(raw).ToString(CultureInfo.InvariantCulture);
            case TagIpAddress:
                return raw.Length == 4 ? new IPAddress(raw).ToString() : Convert.ToHexString(raw);
            default:
                return Convert.ToHexString(raw);
        }
    }

    /// <summary>从缓冲区读取一个 TLV;内容以新数组返回。</summary>
    public static bool TryReadTlv(byte[] buffer, ref int offset, out byte tag, out byte[] content)
    {
        tag = 0;
        content = [];
        if (buffer is null || offset < 0 || offset >= buffer.Length)
        {
            return false;
        }

        tag = buffer[offset++];
        if (offset >= buffer.Length)
        {
            return false;
        }

        var first = buffer[offset++];
        int length;
        if ((first & 0x80) == 0)
        {
            length = first;
        }
        else
        {
            var count = first & 0x7F;
            if (count == 0 || count > 4 || offset + count > buffer.Length)
            {
                return false;
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | buffer[offset++];
            }
        }

        if (length < 0 || offset + length > buffer.Length)
        {
            return false;
        }

        content = new byte[length];
        Array.Copy(buffer, offset, content, 0, length);
        offset += length;
        return true;
    }

    /// <summary>大端有符号整数解码(支持 1..8 字节)。</summary>
    public static long DecodeLong(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return 0;
        }

        var value = (bytes[0] & 0x80) != 0 ? -1L : 0L;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>大端无符号整数解码(Counter64 等)。</summary>
    public static ulong DecodeUnsigned(byte[] bytes)
    {
        if (bytes is null)
        {
            return 0;
        }

        ulong value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static string? Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().Trim();
        while (result.Contains("  ", StringComparison.Ordinal))
        {
            result = result.Replace("  ", " ", StringComparison.Ordinal);
        }

        return result.Length == 0 ? null : result;
    }
}
