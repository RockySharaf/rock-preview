using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Pdf;

/// <summary>
/// Writes small PDFs for the reader's tests (the pattern of tools/PreviewHarness/SampleGenerator.WritePdf, grown):
/// objects as text, streams through any filter chain, a cross-reference table, stream or both (hybrid), object
/// streams, and the standard security handler with an empty user password (RC4 40/128, AES-128, AES-256 R6).
/// Strings that must be encrypted are written with <see cref="Str"/>; everything else is plain PDF syntax.
/// </summary>
internal sealed class PdfFixtureWriter
{
    public enum XrefKind { Table, Stream, Hybrid }
    public enum CryptKind { None, Rc4_40, Rc4_128, Aes128, Aes256 }

    sealed class Obj
    {
        public string Body = "null";
        public byte[]? Data;
        public bool Raw; // data written as given (no /Length fix-up, no encryption)
        public bool KeepOutOfObjStm;
        public bool Plain; // never encrypted (an unencrypted metadata stream)
    }

    readonly List<Obj> _objects = [];

    /// <summary>The catalog's object number (set by the caller, or by <see cref="SimpleDocument"/>).</summary>
    public int Root { get; set; }
    /// <summary>The /Info dictionary's object number, 0 for none.</summary>
    public int Info { get; set; }
    /// <summary>Text written before "%PDF-" (junk some writers leave).</summary>
    public string Preamble { get; set; } = "";
    /// <summary>/EncryptMetadata false (R4).</summary>
    public bool EncryptMetadata { get; set; } = true;
    /// <summary>Objects per object stream (several streams when fewer than the objects).</summary>
    public int ObjectsPerStream { get; set; } = int.MaxValue;
    /// <summary>Object streams and the xref stream are Flate-compressed (off: written plainly, for structured fuzzing).</summary>
    public bool CompressStructure { get; set; } = true;

    /// <summary>A string to encrypt with its object (ASCII or, with a non-ASCII character, UTF-16BE with FE FF).</summary>
    public static string Str(string text) => "\u0001" + Convert.ToBase64String(TextBytes(text)) + "\u0002";
    /// <summary>A string of raw bytes to encrypt with its object.</summary>
    public static string Str(byte[] bytes) => "\u0001" + Convert.ToBase64String(bytes) + "\u0002";

    static byte[] TextBytes(string text)
    {
        foreach (char c in text)
            if (c > 0x7E) return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>Reserves an object number, set later with <see cref="Set"/>.</summary>
    public int Reserve()
    {
        _objects.Add(new Obj());
        return _objects.Count;
    }

    public int Add(string body)
    {
        int n = Reserve();
        Set(n, body);
        return n;
    }

    public void Set(int number, string body) => _objects[number - 1].Body = body;

    /// <summary>A stream whose data is encoded through the filters (first filter = outermost, as /Filter lists them).</summary>
    public int AddStream(string dict, byte[] data, params string[] filters)
    {
        int n = Reserve();
        SetStream(n, dict, data, filters);
        return n;
    }

    public void SetStream(int number, string dict, byte[] data, params string[] filters)
    {
        var encoded = data;
        var parms = new List<string>();
        for (int i = filters.Length - 1; i >= 0; i--) encoded = Encode(filters[i], encoded);
        string filterEntry = filters.Length switch
        {
            0 => "",
            1 => $"/Filter /{FilterName(filters[0])}",
            _ => "/Filter [" + string.Join(' ', filters.Select(f => "/" + FilterName(f))) + "]",
        };
        foreach (var f in filters) parms.Add(f == "FlatePng" ? "<< /Predictor 12 /Columns 4 >>" : "null");
        string parmsEntry = filters.Any(f => f == "FlatePng")
            ? filters.Length == 1 ? $"/DecodeParms {parms[0]}" : "/DecodeParms [" + string.Join(' ', parms) + "]" : "";
        var o = _objects[number - 1];
        o.Body = $"<< {Inner(dict)} {filterEntry} {parmsEntry} >>";
        o.Data = encoded;
    }

    /// <summary>A stream written byte for byte: its dictionary as given (with whatever /Length it says), never encrypted.</summary>
    public int AddRawStream(string dict, byte[] data)
    {
        int n = Reserve();
        _objects[n - 1].Body = dict;
        _objects[n - 1].Data = data;
        _objects[n - 1].Raw = true;
        return n;
    }

    /// <summary>The XMP or other stream is left unencrypted (with /EncryptMetadata false).</summary>
    public void LeavePlain(int number) => _objects[number - 1].Plain = true;
    /// <summary>The object stays a top-level object even when object streams are written.</summary>
    public void KeepTopLevel(int number) => _objects[number - 1].KeepOutOfObjStm = true;

    static string Inner(string dict)
    {
        string d = dict.Trim();
        return d.StartsWith("<<", StringComparison.Ordinal) && d.EndsWith(">>", StringComparison.Ordinal) ? d[2..^2] : d;
    }

    static string FilterName(string f) => f switch
    {
        "Flate" or "FlatePng" => "FlateDecode",
        "LZW" => "LZWDecode",
        "A85" => "ASCII85Decode",
        "AHx" => "ASCIIHexDecode",
        "RL" => "RunLengthDecode",
        _ => f,
    };

    /// <summary>Encodes data with one filter (test-side encoders; LZW without early-change surprises).</summary>
    public static byte[] Encode(string filter, byte[] data) => filter switch
    {
        "Flate" => Zlib(data),
        "FlatePng" => Zlib(PngUp(data, 4)),
        "LZW" => LzwEncode(data),
        "A85" => Ascii85(data),
        "AHx" => Encoding.ASCII.GetBytes(Convert.ToHexString(data) + ">"),
        "RL" => RunLength(data),
        _ => throw new ArgumentException(filter),
    };

    public static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    static byte[] PngUp(byte[] data, int columns)
    {
        var output = new List<byte>();
        var prior = new byte[columns];
        for (int i = 0; i < data.Length; i += columns)
        {
            output.Add(2); // Up
            for (int k = 0; k < columns; k++)
            {
                byte b = i + k < data.Length ? data[i + k] : (byte)0;
                output.Add(unchecked((byte)(b - prior[k])));
                prior[k] = b;
            }
        }
        return [.. output];
    }

    static byte[] LzwEncode(byte[] data)
    {
        var bits = new List<bool>();
        void Put(int code, int width) { for (int i = width - 1; i >= 0; i--) bits.Add(((code >> i) & 1) != 0); }
        var table = new Dictionary<string, int>();
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table[((char)i).ToString()] = i; }
        Reset();
        int next = 258, width = 9;
        Put(256, width);
        string w = "";
        // The decoder's table is one entry behind the encoder's, so with EarlyChange 1 the encoder widens once its
        // own count reaches the next power of two.
        void Emitted() { next++; if (next >= 1 << width && width < 12) width++; }
        foreach (byte b in data)
        {
            string wc = w + (char)b;
            if (table.ContainsKey(wc)) { w = wc; continue; }
            Put(table[w], width);
            table[wc] = next;
            Emitted();
            if (next >= 4094) { Put(256, width); Reset(); next = 258; width = 9; }
            w = ((char)b).ToString();
        }
        if (w.Length > 0) { Put(table[w], width); Emitted(); }
        Put(257, width);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    static byte[] Ascii85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : 0u);
            if (v == 0 && n == 4) { sb.Append('z'); continue; }
            var c = new char[5];
            for (int k = 4; k >= 0; k--) { c[k] = (char)('!' + v % 85); v /= 85; }
            sb.Append(c, 0, n + 1);
        }
        return Encoding.ASCII.GetBytes(sb.Append("~>").ToString());
    }

    static byte[] RunLength(byte[] data)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 128) run++;
            if (run >= 2) { o.Add((byte)(257 - run)); o.Add(data[i]); i += run; continue; }
            int start = i;
            while (i < data.Length && i - start < 128 && !(i + 1 < data.Length && data[i + 1] == data[i])) i++;
            if (i == start) i++;
            o.Add((byte)(i - start - 1));
            for (int k = start; k < i; k++) o.Add(data[k]);
        }
        o.Add(128);
        return [.. o];
    }

    // ── Documents ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A catalog and a page tree with one page per content (null = a page with no fonts and an image only).</summary>
    public static PdfFixtureWriter SimpleDocument(params string?[] contents) => SimpleDocument(true, contents);

    /// <summary>As above; compress false writes the content streams unfiltered (for structured fuzzing).</summary>
    public static PdfFixtureWriter SimpleDocument(bool compress, params string?[] contents)
    {
        var w = new PdfFixtureWriter();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var kids = new List<int>();
        foreach (var c in contents)
        {
            int page = w.Reserve();
            kids.Add(page);
            if (c == null)
            {
                int image = w.AddStream("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 >>", [1, 2, 3, 4]);
                int content = w.AddStream("<< >>", Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm /Im0 Do Q"));
                w.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>");
            }
            else
            {
                int content = compress ? w.AddStream("<< >>", Encoding.Latin1.GetBytes(c), "Flate") : w.AddStream("<< >>", Encoding.Latin1.GetBytes(c));
                w.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
            }
        }
        w.Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        return w;
    }

    /// <summary>Content that shows each line with Helvetica 12 at its own baseline.</summary>
    public static string Lines(params string[] lines)
    {
        var sb = new StringBuilder("BT /F1 12 Tf 72 720 Td 14 TL\n");
        foreach (var line in lines) sb.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        return sb.Append("ET").ToString();
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The file. objectStreams puts every plain object (not streams, not the encryption dictionary) in one object stream.</summary>
    public byte[] Write(XrefKind xref = XrefKind.Table, bool objectStreams = false, CryptKind crypt = CryptKind.None, string? extraTrailer = null)
    {
        if (objectStreams && xref == XrefKind.Table) xref = XrefKind.Stream;
        byte[] id = MD5.HashData(Encoding.ASCII.GetBytes("fixture" + _objects.Count));
        var security = crypt == CryptKind.None ? null : new Security(crypt, id, EncryptMetadata);
        // Object numbers: the given objects, then (optionally) the encryption dictionary, the object stream, the xref stream.
        int count = _objects.Count;
        int encryptNumber = security != null ? ++count : 0;
        var packed = new List<int>();
        if (objectStreams)
            for (int i = 1; i <= _objects.Count; i++)
                if (_objects[i - 1].Data == null && !_objects[i - 1].KeepOutOfObjStm) packed.Add(i);
        var groups = packed.Chunk(Math.Max(1, ObjectsPerStream)).ToList();
        var stmNumbers = groups.Select(_ => ++count).ToArray();
        int xrefNumber = xref != XrefKind.Table ? ++count : 0;

        var file = new MemoryStream();
        void Text(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        Text(Preamble);
        Text("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[count + 1];
        var inStream = new Dictionary<int, (int Stream, int Index)>();

        for (int i = 1; i <= _objects.Count; i++)
        {
            if (packed.Contains(i)) continue;
            var o = _objects[i - 1];
            offsets[i] = file.Position;
            bool encrypt = security != null && !o.Raw && !o.Plain;
            string body = Strings(o.Body, encrypt ? b => security!.Encrypt(b, i) : null);
            Text($"{i} 0 obj\n");
            if (o.Data != null)
            {
                byte[] data = encrypt ? security!.Encrypt(o.Data, i) : o.Data;
                Text(o.Raw ? body : $"<< {Inner(body)} /Length {data.Length} >>");
                Text("\nstream\n");
                file.Write(data);
                Text("\nendstream\nendobj\n");
            }
            else Text(body + "\nendobj\n");
        }
        if (security != null)
        {
            offsets[encryptNumber] = file.Position;
            Text($"{encryptNumber} 0 obj\n{security.Dictionary}\nendobj\n");
        }
        for (int g = 0; g < groups.Count; g++)
        {
            int objStmNumber = stmNumbers[g];
            var members = groups[g];
            var header = new StringBuilder();
            var bodies = new MemoryStream();
            for (int k = 0; k < members.Length; k++)
            {
                header.Append(members[k]).Append(' ').Append(bodies.Position).Append(' ');
                bodies.Write(Encoding.Latin1.GetBytes(Strings(_objects[members[k] - 1].Body, null) + "\n"));
                inStream[members[k]] = (objStmNumber, k);
            }
            byte[] head = Encoding.ASCII.GetBytes(header.ToString());
            byte[] data = [.. head, .. bodies.ToArray()];
            if (CompressStructure) data = Zlib(data);
            if (security != null) data = security.Encrypt(data, objStmNumber);
            offsets[objStmNumber] = file.Position;
            Text($"{objStmNumber} 0 obj\n<< /Type /ObjStm /N {members.Length} /First {head.Length} {(CompressStructure ? "/Filter /FlateDecode " : "")}/Length {data.Length} >>\nstream\n");
            file.Write(data);
            Text("\nendstream\nendobj\n");
        }
        string trailerKeys = $"/Root {Root} 0 R" + (Info > 0 ? $" /Info {Info} 0 R" : "")
            + (security != null ? $" /Encrypt {encryptNumber} 0 R" : "")
            + $" /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]" + (extraTrailer ?? "");
        long startxref;
        if (xref == XrefKind.Table)
        {
            startxref = file.Position;
            WriteTable(file, offsets, count, null);
            Text($"trailer\n<< /Size {count + 1} {trailerKeys} >>\nstartxref\n{startxref}\n%%EOF\n");
        }
        else
        {
            long streamAt = file.Position;
            offsets[xrefNumber] = streamAt;
            var rows = new MemoryStream();
            for (int i = 0; i <= count; i++)
            {
                byte type; long f2; int f3;
                if (i == 0) { type = 0; f2 = 0; f3 = 65535; }
                else if (inStream.TryGetValue(i, out var place)) { type = 2; f2 = place.Stream; f3 = place.Index; }
                else { type = 1; f2 = offsets[i]; f3 = 0; }
                rows.WriteByte(type);
                for (int s = 24; s >= 0; s -= 8) rows.WriteByte((byte)(f2 >> s));
                rows.WriteByte((byte)(f3 >> 8)); rows.WriteByte((byte)f3);
            }
            byte[] xdata = CompressStructure ? Zlib(PngUp(rows.ToArray(), 7)) : rows.ToArray();
            string coding = CompressStructure ? "/Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >> " : "";
            Text($"{xrefNumber} 0 obj\n<< /Type /XRef /Size {count + 1} /W [1 4 2] {trailerKeys} {coding}/Length {xdata.Length} >>\nstream\n");
            file.Write(xdata);
            Text("\nendstream\nendobj\n");
            if (xref == XrefKind.Hybrid)
            {
                startxref = file.Position;
                // The table lists the top-level objects; those in the object stream are only in the stream.
                WriteTable(file, offsets, count, inStream);
                Text($"trailer\n<< /Size {count + 1} {trailerKeys} /XRefStm {streamAt} >>\nstartxref\n{startxref}\n%%EOF\n");
            }
            else
            {
                startxref = streamAt;
                Text($"startxref\n{startxref}\n%%EOF\n");
            }
        }
        return file.ToArray();
    }

    static void WriteTable(MemoryStream file, long[] offsets, int count, Dictionary<int, (int, int)>? skip)
    {
        var sb = new StringBuilder($"xref\n0 {count + 1}\n0000000000 65535 f\r\n");
        for (int i = 1; i <= count; i++)
            sb.Append(skip != null && skip.ContainsKey(i) || offsets[i] == 0 ? "0000000000 65535 f\r\n" : $"{offsets[i]:D10} 00000 n\r\n");
        file.Write(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Replaces Str markers with hex strings, encrypted when asked.</summary>
    static string Strings(string body, Func<byte[], byte[]>? encrypt)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < body.Length)
        {
            int open = body.IndexOf('\u0001', i);
            if (open < 0) { sb.Append(body, i, body.Length - i); break; }
            int close = body.IndexOf('\u0002', open);
            sb.Append(body, i, open - i);
            byte[] bytes = Convert.FromBase64String(body[(open + 1)..close]);
            if (encrypt != null) bytes = encrypt(bytes);
            sb.Append('<').Append(Convert.ToHexString(bytes)).Append('>');
            i = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>The standard security handler's side: O and U (UE, OE, Perms for R6) and per-object encryption.</summary>
    sealed class Security
    {
        static readonly byte[] Pad =
        [
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        ];

        readonly CryptKind _kind;
        readonly byte[] _key;
        public string Dictionary { get; }

        public Security(CryptKind kind, byte[] id, bool encryptMetadata)
        {
            _kind = kind;
            const int P = -3904;
            var owner = Encoding.ASCII.GetBytes("owner");
            if (kind == CryptKind.Aes256)
            {
                _key = RandomNumberGenerator.GetBytes(32);
                byte[] vs = RandomNumberGenerator.GetBytes(8), ks = RandomNumberGenerator.GetBytes(8);
                byte[] u = [.. PdfCrypt.Hash2B([], vs, []), .. vs, .. ks];
                byte[] ue = AesNoPad(PdfCrypt.Hash2B([], ks, []), _key);
                byte[] ovs = RandomNumberGenerator.GetBytes(8), oks = RandomNumberGenerator.GetBytes(8);
                byte[] o = [.. PdfCrypt.Hash2B(owner, ovs, u), .. ovs, .. oks];
                byte[] oe = AesNoPad(PdfCrypt.Hash2B(owner, oks, u), _key);
                byte[] perms = [.. BitConverter.GetBytes(P), 0xFF, 0xFF, 0xFF, 0xFF, (byte)'T', (byte)'a', (byte)'d', (byte)'b', 1, 2, 3, 4];
                using var aes = Aes.Create();
                aes.Key = _key;
                byte[] permsEnc = aes.EncryptEcb(perms, PaddingMode.None);
                Dictionary = $"<< /Filter /Standard /V 5 /R 6 /Length 256 /P {P} /O <{Hex(o)}> /U <{Hex(u)}> /OE <{Hex(oe)}> /UE <{Hex(ue)}> /Perms <{Hex(permsEnc)}>"
                    + " /CF << /StdCF << /CFM /AESV3 /AuthEvent /DocOpen /Length 32 >> >> /StmF /StdCF /StrF /StdCF >>";
                return;
            }
            int r = kind switch { CryptKind.Rc4_40 => 2, CryptKind.Rc4_128 => 3, _ => 4 };
            int n = r == 2 ? 5 : 16;
            // Algorithm 3: O from the owner password.
            byte[] ownerKey = MD5.HashData([.. owner, .. Pad.AsSpan(0, 32 - owner.Length).ToArray()]);
            if (r >= 3) for (int i = 0; i < 50; i++) ownerKey = MD5.HashData(ownerKey);
            ownerKey = ownerKey[..n];
            byte[] oValue = PdfCrypt.Rc4(ownerKey, Pad);
            if (r >= 3)
                for (int i = 1; i <= 19; i++) oValue = PdfCrypt.Rc4(ownerKey.Select(b => (byte)(b ^ i)).ToArray(), oValue);
            // Algorithm 2: the file key from the empty user password.
            byte[] input = [.. Pad, .. oValue, .. BitConverter.GetBytes(P), .. id, .. (r >= 4 && !encryptMetadata ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } : [])];
            byte[] key = MD5.HashData(input);
            if (r >= 3) for (int i = 0; i < 50; i++) key = MD5.HashData(key.AsSpan(0, n));
            _key = key[..n];
            byte[] uValue;
            if (r == 2) uValue = PdfCrypt.Rc4(_key, Pad);
            else
            {
                byte[] x = PdfCrypt.Rc4(_key, MD5.HashData([.. Pad, .. id]));
                for (int i = 1; i <= 19; i++) x = PdfCrypt.Rc4(_key.Select(b => (byte)(b ^ i)).ToArray(), x);
                uValue = [.. x, .. new byte[16]];
            }
            string common = $"/Filter /Standard /P {P} /O <{Hex(oValue)}> /U <{Hex(uValue)}>";
            Dictionary = kind switch
            {
                CryptKind.Rc4_40 => $"<< {common} /V 1 /R 2 >>",
                CryptKind.Rc4_128 => $"<< {common} /V 2 /R 3 /Length 128 >>",
                _ => $"<< {common} /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> /StmF /StdCF /StrF /StdCF"
                    + (encryptMetadata ? "" : " /EncryptMetadata false") + " >>",
            };
        }

        static string Hex(byte[] b) => Convert.ToHexString(b);

        static byte[] AesNoPad(byte[] key, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
        }

        public byte[] Encrypt(byte[] data, int number)
        {
            if (_kind == CryptKind.Aes256) return AesWithIv(_key, data);
            byte[] objectKey = MD5.HashData([.. _key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0,
                .. (_kind == CryptKind.Aes128 ? "sAlT"u8.ToArray() : [])])[..Math.Min(_key.Length + 5, 16)];
            return _kind == CryptKind.Aes128 ? AesWithIv(objectKey, data) : PdfCrypt.Rc4(objectKey, data);
        }

        static byte[] AesWithIv(byte[] key, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
        }
    }
}
