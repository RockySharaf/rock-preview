using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Outlook .msg files through CompoundFile: subject (Subject: subject: finds it), sender (Sender: from: and author:),
/// recipients (to:), body (text, else compressed RTF, else
/// HTML) and attachment names (as body lines; their contents are never read). 8-bit string properties and the HTML body
/// are decoded with the message's own code page (PidTagMessageCodepage 3FFD, PidTagInternetCodepage 3FDE). Each stream is
/// read on its own: a broken one is skipped and the document marked Truncated.
/// </summary>
internal sealed class MsgReader : IContentReader
{
    public ContentKind Kind => ContentKind.Mail;
    /// <summary>2: the subject moved from Title to Subject (read again, ExtractorChanges).</summary>
    public byte Version => 2;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "msg";

    const int PtString8 = 0x001E, PtUnicode = 0x001F, PtBinary = 0x0102, PtLong = 0x0003;

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        var cfb = CompoundFile.Open(source, budget);
        var message = new Storage(cfb, cfb.Root, budget, headerBytes: 32);
        int codePage = message.Long(0x3FFD) ?? message.Long(0x3FDE) ?? 0;
        int internetCodePage = message.Long(0x3FDE) ?? codePage;

        message.Field(sink, DocField.Subject, codePage, 0x0037, 0x0E1D, 0x0070);
        string? senderName = message.String(codePage, 0x0C1A) ?? message.String(codePage, 0x0042);
        string? senderAddress = message.String(codePage, 0x5D01) ?? message.String(codePage, 0x0C1F) ?? message.String(codePage, 0x0065);
        if (Join(senderName, senderAddress) is { } sender) sink.Field(DocField.Sender, sender);

        var recipients = new List<string>();
        foreach (var prop in (ReadOnlySpan<int>)[0x0E04, 0x0E03])
            if (message.String(codePage, prop) is { Length: > 0 } list) recipients.Add(list);
        var attachments = new List<string>();
        // Without the display lists, every recipient storage gives one name.
        bool fromStorages = recipients.Count == 0;
        foreach (var child in cfb.Children(cfb.Root))
        {
            budget.Check();
            if (cfb.Type(child) != CompoundFile.EntryType.Storage) continue;
            string name = cfb.Name(child);
            if (name.StartsWith("__recip_version1.0_", StringComparison.OrdinalIgnoreCase) && fromStorages)
            {
                var r = new Storage(cfb, child, budget, headerBytes: 8);
                if (Join(r.String(codePage, 0x3001), r.String(codePage, 0x39FE) ?? r.String(codePage, 0x3003)) is { } one && recipients.Count < 1000)
                    recipients.Add(one);
                message.Partial |= r.Partial;
            }
            else if (name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase) && attachments.Count < 256)
            {
                var a = new Storage(cfb, child, budget, headerBytes: 8);
                if ((a.String(codePage, 0x3707) ?? a.String(codePage, 0x3704) ?? a.String(codePage, 0x3001)) is { Length: > 0 } file) attachments.Add(file);
                message.Partial |= a.Partial;
            }
        }
        if (recipients.Count > 0) sink.Field(DocField.Recipients, string.Join("; ", recipients));

        Body(message, sink, budget, codePage, internetCodePage);
        foreach (var name in attachments)
        {
            sink.LineBreak();
            sink.Text(name);
        }
        return message.Partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    static void Body(Storage message, ExtractSink sink, ExtractBudget budget, int codePage, int internetCodePage)
    {
        if (message.String(codePage, 0x1000) is { Length: > 0 } text)
        {
            sink.Text(text);
            return;
        }
        if (message.Binary(0x1009) is { Length: > 16 } compressed)
        {
            try
            {
                var rtf = CompressedRtf.Decompress(compressed, budget.Limits.MaxStreamBytes);
                if (rtf != null)
                {
                    budget.Decoded(rtf.Length);
                    using var memory = new MemoryByteSource(rtf, budget);
                    var parser = new RtfReader.Parser(memory, sink, budget);
                    if (parser.Start()) { parser.Run(); if (sink.HasWords) return; }
                }
                else message.Partial = true;
            }
            catch (InvalidDataException) { message.Partial = true; }
        }
        byte[]? html = message.Binary(0x1013);
        string? htmlText = html == null ? message.String(internetCodePage, 0x1013) : null;
        if (html != null)
        {
            var encoding = (internetCodePage > 0 ? TextDecoding.ForCharset("windows-" + internetCodePage) : null)
                ?? TextDecoding.HtmlCharset(html) ?? TextDecoding.Detect(html) ?? TextDecoding.Ansi();
            if (internetCodePage == 65001) encoding = new UTF8Encoding(false);
            htmlText = TextDecoding.Decode(html, encoding);
        }
        if (!string.IsNullOrEmpty(htmlText)) HtmlText.Extract(htmlText, sink, budget);
    }

    static string? Join(string? name, string? address)
    {
        name = name?.Trim();
        address = address?.Trim();
        if (string.IsNullOrEmpty(address) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return string.IsNullOrEmpty(name) ? null : name;
        return string.IsNullOrEmpty(name) ? address : $"{name} <{address}>";
    }

    /// <summary>The property streams of one storage (message, recipient or attachment) and its fixed-size properties.</summary>
    sealed class Storage
    {
        readonly CompoundFile _cfb;
        readonly long _max;
        readonly Dictionary<(int Id, int Type), int> _streams = [];
        readonly Dictionary<int, int> _longs = [];

        public bool Partial { get; set; }

        public Storage(CompoundFile cfb, int storage, ExtractBudget budget, int headerBytes)
        {
            _cfb = cfb;
            _max = budget.Limits.MaxStreamBytes;
            int properties = -1;
            foreach (var id in cfb.Children(storage))
            {
                string name = cfb.Name(id);
                if (name.Equals("__properties_version1.0", StringComparison.OrdinalIgnoreCase)) { properties = id; continue; }
                if (name.Length == 20 && name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.AsSpan(12, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int prop)
                    && int.TryParse(name.AsSpan(16, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int type))
                    _streams.TryAdd((prop, type), id);
            }
            if (properties >= 0)
            {
                var bytes = cfb.ReadStream(properties, 1 << 20);
                if (bytes == null) Partial = true;
                else
                    for (int at = headerBytes; at + 16 <= bytes.Length; at += 16)
                    {
                        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                        if ((tag & 0xFFFF) == PtLong) _longs.TryAdd((int)(tag >> 16), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 8)));
                    }
            }
        }

        public int? Long(int prop) => _longs.TryGetValue(prop, out int v) && v > 0 ? v : null;

        public byte[]? Binary(int prop)
        {
            if (!_streams.TryGetValue((prop, PtBinary), out int id)) return null;
            var b = _cfb.ReadStream(id, _max);
            if (b == null) Partial = true;
            return b;
        }

        /// <summary>A string property: Unicode (001F) when there, else 8-bit (001E) in the message's code page.</summary>
        public string? String(int codePage, int prop)
        {
            if (_streams.TryGetValue((prop, PtUnicode), out int id))
            {
                var b = _cfb.ReadStream(id, _max);
                if (b == null) { Partial = true; return null; }
                return Encoding.Unicode.GetString(b, 0, b.Length & ~1).TrimEnd('\0');
            }
            if (_streams.TryGetValue((prop, PtString8), out id))
            {
                var b = _cfb.ReadStream(id, _max);
                if (b == null) { Partial = true; return null; }
                var encoding = codePage == 65001 ? new UTF8Encoding(false)
                    : (codePage > 0 ? TextDecoding.ForCharset("windows-" + codePage) : null) ?? TextDecoding.Ansi();
                return encoding.GetString(b).TrimEnd('\0');
            }
            return null;
        }

        /// <summary>The first of the properties that has a value, as a field.</summary>
        public void Field(ExtractSink sink, DocField field, int codePage, params int[] props)
        {
            foreach (var p in props)
                if (String(codePage, p) is { Length: > 0 } v) { sink.Field(field, v); return; }
        }
    }
}

/// <summary>
/// Compressed RTF (MS-OXRTFCP): LZFu with its 4,096-byte dictionary preloaded with the standard RTF prefix, or MELA
/// (stored). The CRC is not checked, so a slightly damaged body still reads; output stops at the declared raw size and
/// at the cap.
/// </summary>
internal static class CompressedRtf
{
    const uint Lzfu = 0x75465A4C, Mela = 0x414C454D;

    /// <summary>The dictionary's initial contents (207 bytes).</summary>
    static readonly byte[] Prefix = Encoding.ASCII.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

    /// <summary>The RTF bytes; null when the header is not compressed RTF.</summary>
    public static byte[]? Decompress(ReadOnlySpan<byte> data, long max)
    {
        if (data.Length < 16) return null;
        uint compSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        long end = Math.Min(data.Length, (long)compSize + 4);
        long outMax = Math.Min(rawSize, max);
        if (type == Mela)
        {
            int n = (int)Math.Min(outMax, data.Length - 16);
            return data.Slice(16, n).ToArray();
        }
        if (type != Lzfu) return null;
        var dict = new byte[4096];
        Prefix.CopyTo(dict, 0);
        int write = Prefix.Length;
        var output = new List<byte>((int)Math.Min(outMax, 1 << 20));
        int pos = 16;
        while (pos < end && output.Count < outMax)
        {
            int control = data[pos++];
            for (int bit = 0; bit < 8 && output.Count < outMax; bit++)
            {
                if (pos >= end) return [.. output];
                if ((control & (1 << bit)) == 0)
                {
                    byte b = data[pos++];
                    output.Add(b);
                    dict[write] = b;
                    write = (write + 1) & 4095;
                    continue;
                }
                if (pos + 1 >= end) return [.. output];
                int reference = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                int offset = reference >> 4, length = (reference & 0xF) + 2;
                if (offset == write) return [.. output]; // the end marker
                for (int i = 0; i < length && output.Count < outMax; i++)
                {
                    byte b = dict[(offset + i) & 4095];
                    output.Add(b);
                    dict[write] = b;
                    write = (write + 1) & 4095;
                }
            }
        }
        return [.. output];
    }

    /// <summary>The CRC MS-OXRTFCP uses (CRC-32, initial value 0, no final inversion), for tests.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc;
    }
}
