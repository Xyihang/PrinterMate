using System.ComponentModel;
using System.Net;
using System.Text;

namespace PrinterMate.Core.Discovery;

/// <summary>DNS 查询问题段。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record DnsQuestion(string Name, ushort Type, ushort Class);

/// <summary>DNS 资源记录。按记录类型填充对应的可选字段。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record DnsResourceRecord(
    string Name,
    ushort Type,
    ushort Class,
    uint Ttl,
    string? DomainName = null,
    ushort? Priority = null,
    ushort? Weight = null,
    ushort? Port = null,
    string? Target = null,
    IReadOnlyList<string>? Text = null,
    IPAddress? Address = null,
    byte[]? Raw = null);

/// <summary>解析后的 DNS 报文。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record DnsMessage(
    ushort Id,
    ushort Flags,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsResourceRecord> Answers,
    IReadOnlyList<DnsResourceRecord> Authority,
    IReadOnlyList<DnsResourceRecord> Additional)
{
    public bool IsResponse => (Flags & 0x8000) != 0;

    /// <summary>全部资源记录(Answer + Authority + Additional)。</summary>
    public IReadOnlyList<DnsResourceRecord> AllRecords =>
        [.. Answers, .. Authority, .. Additional];
}

/// <summary>
/// DNS 报文的手工编解码(无第三方依赖),含 DNS 名称压缩指针解析。全部为纯函数,便于单元测试。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeNS = 2;
    public const ushort TypeCNAME = 5;
    public const ushort TypePTR = 12;
    public const ushort TypeTXT = 16;
    public const ushort TypeAAAA = 28;
    public const ushort TypeSRV = 33;
    public const ushort ClassIN = 1;

    private const int HeaderLength = 12;
    private const int MaxSectionRecords = 4096;
    private const int MaxCompressionJumps = 64;

    /// <summary>编码一个标准查询报文(ID + 问题段)。</summary>
    public static byte[] EncodeQuery(ushort id, IReadOnlyList<DnsQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var body = new List<byte>();
        foreach (var question in questions)
        {
            body.AddRange(EncodeName(question.Name));
            body.AddRange(BigEndian(question.Type));
            body.AddRange(BigEndian(question.Class));
        }

        var packet = new byte[HeaderLength + body.Count];
        packet[0] = (byte)(id >> 8);
        packet[1] = (byte)id;
        packet[2] = 0; // flags:标准查询
        packet[3] = 0;
        packet[4] = (byte)(questions.Count >> 8);
        packet[5] = (byte)questions.Count;
        body.CopyTo(packet, HeaderLength);
        return packet;
    }

    /// <summary>编码 DNS 名称:长度前缀标签序列 + 根标签 0。</summary>
    public static byte[] EncodeName(string name)
    {
        var output = new List<byte>();
        var text = (name ?? string.Empty).Trim();
        if (text is "." or "")
        {
            output.Add(0);
            return output.ToArray();
        }

        text = text.TrimEnd('.');
        foreach (var label in text.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new FormatException($"DNS 标签“{label}”长度非法。");
            }

            output.Add((byte)bytes.Length);
            output.AddRange(bytes);
        }

        output.Add(0);
        return output.ToArray();
    }

    /// <summary>
    /// 读取一个 DNS 名称并推进 <paramref name="offset"/>。压缩指针(0xC0)会被跟随,
    /// 但 offset 只按原始流长度推进。指针成环或越界时抛 <see cref="FormatException"/>。
    /// </summary>
    public static string ReadName(byte[] packet, ref int offset)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var labels = new List<string>();
        var cursor = offset;
        var consumedEnd = -1;
        var jumps = 0;

        while (true)
        {
            if (cursor < 0 || cursor >= packet.Length)
            {
                throw new FormatException("DNS 名称越界。");
            }

            var length = packet[cursor];
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length)
                {
                    throw new FormatException("DNS 压缩指针不完整。");
                }

                var pointer = ((length & 0x3F) << 8) | packet[cursor + 1];
                if (consumedEnd < 0)
                {
                    consumedEnd = cursor + 2;
                }

                if (++jumps > MaxCompressionJumps)
                {
                    throw new FormatException("DNS 名称压缩指针成环。");
                }

                if (pointer >= packet.Length)
                {
                    throw new FormatException("DNS 压缩指针越界。");
                }

                cursor = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new FormatException("不支持的 DNS 标签类型。");
            }

            cursor++;
            if (length == 0)
            {
                break;
            }

            if (cursor + length > packet.Length)
            {
                throw new FormatException("DNS 标签越界。");
            }

            labels.Add(Encoding.UTF8.GetString(packet, cursor, length));
            cursor += length;
        }

        offset = consumedEnd >= 0 ? consumedEnd : cursor;
        return string.Join('.', labels);
    }

    /// <summary>解析一整段 DNS 报文;格式非法时返回 false 而不是抛异常。</summary>
    public static bool TryParse(byte[]? packet, out DnsMessage? message)
    {
        message = null;
        if (packet is null || packet.Length < HeaderLength)
        {
            return false;
        }

        try
        {
            var offset = 0;
            var id = ReadUInt16(packet, ref offset);
            var flags = ReadUInt16(packet, ref offset);
            var questionCount = ReadUInt16(packet, ref offset);
            var answerCount = ReadUInt16(packet, ref offset);
            var authorityCount = ReadUInt16(packet, ref offset);
            var additionalCount = ReadUInt16(packet, ref offset);

            if (questionCount > MaxSectionRecords || answerCount > MaxSectionRecords ||
                authorityCount > MaxSectionRecords || additionalCount > MaxSectionRecords)
            {
                return false;
            }

            var questions = new List<DnsQuestion>(questionCount);
            for (var i = 0; i < questionCount; i++)
            {
                var name = ReadName(packet, ref offset);
                var type = ReadUInt16(packet, ref offset);
                var cls = ReadUInt16(packet, ref offset);
                questions.Add(new DnsQuestion(name, type, cls));
            }

            var answers = ReadRecords(packet, ref offset, answerCount);
            var authority = ReadRecords(packet, ref offset, authorityCount);
            var additional = ReadRecords(packet, ref offset, additionalCount);

            message = new DnsMessage(id, flags, questions, answers, authority, additional);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            message = null;
            return false;
        }
    }

    private static List<DnsResourceRecord> ReadRecords(byte[] packet, ref int offset, int count)
    {
        var records = new List<DnsResourceRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(packet, ref offset);
            var type = ReadUInt16(packet, ref offset);
            var cls = ReadUInt16(packet, ref offset);
            var ttl = ReadUInt32(packet, ref offset);
            var rdLength = ReadUInt16(packet, ref offset);

            var dataStart = offset;
            if (dataStart + rdLength > packet.Length)
            {
                throw new FormatException("DNS 记录数据越界。");
            }

            string? domainName = null;
            ushort? priority = null;
            ushort? weight = null;
            ushort? port = null;
            string? target = null;
            List<string>? text = null;
            IPAddress? address = null;

            switch (type)
            {
                case TypeA:
                    if (rdLength == 4)
                    {
                        address = new IPAddress(packet[dataStart..(dataStart + 4)]);
                    }

                    break;

                case TypeAAAA:
                    if (rdLength == 16)
                    {
                        address = new IPAddress(packet[dataStart..(dataStart + 16)]);
                    }

                    break;

                case TypePTR:
                case TypeNS:
                case TypeCNAME:
                    var nameCursor = dataStart;
                    domainName = ReadName(packet, ref nameCursor);
                    break;

                case TypeSRV:
                    var srvCursor = dataStart;
                    priority = ReadUInt16(packet, ref srvCursor);
                    weight = ReadUInt16(packet, ref srvCursor);
                    port = ReadUInt16(packet, ref srvCursor);
                    target = ReadName(packet, ref srvCursor);
                    break;

                case TypeTXT:
                    var txtCursor = dataStart;
                    var txtEnd = dataStart + rdLength;
                    text = [];
                    while (txtCursor < txtEnd)
                    {
                        int length = packet[txtCursor++];
                        if (txtCursor + length > txtEnd)
                        {
                            break;
                        }

                        text.Add(Encoding.UTF8.GetString(packet, txtCursor, length));
                        txtCursor += length;
                    }

                    break;
            }

            var raw = new byte[rdLength];
            Array.Copy(packet, dataStart, raw, 0, rdLength);

            records.Add(new DnsResourceRecord(name, type, cls, ttl, domainName, priority, weight, port, target, text, address, raw));
            offset = dataStart + rdLength;
        }

        return records;
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        if (offset + 2 > buffer.Length)
        {
            throw new FormatException("DNS 报文越界。");
        }

        var value = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] buffer, ref int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            throw new FormatException("DNS 报文越界。");
        }

        var value = ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
                    ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
        offset += 4;
        return value;
    }

    private static byte[] BigEndian(ushort value) => [(byte)(value >> 8), (byte)value];
}
