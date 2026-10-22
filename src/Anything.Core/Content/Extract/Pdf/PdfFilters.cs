using System.Buffers;
using System.IO.Compression;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Bytes, possibly cut at a cap or broken midway (Truncated); Length bytes of Array are valid. Pooled bytes come from
/// <see cref="PdfPool"/> and belong to whoever holds them: the last reader gives them back once with <see cref="Release"/>.
/// </summary>
internal readonly record struct PdfBytes(byte[] Array, int Length, bool Truncated, bool Pooled = false)
{
    /// <summary>No bytes.</summary>
    public static PdfBytes Empty { get; } = new([], 0, false);
    /// <summary>The valid bytes.</summary>
    public ReadOnlySpan<byte> Span => Array.AsSpan(0, Length);
    /// <summary>Gives a pooled buffer back (call once, when nothing reads it any more); does nothing for other bytes.</summary>
    public void Release()
    {
        if (Pooled) PdfPool.Return(Array);
    }
}

/// <summary>Stream buffers from ArrayPool, cleared when they go back so no document text stays behind in the pool.</summary>
internal static class PdfPool
{
    /// <summary>A buffer of at least length bytes (an empty array for 0).</summary>
    public static byte[] Rent(int length) => length <= 0 ? [] : ArrayPool<byte>.Shared.Rent(length);

    /// <summary>Gives a rented buffer back, cleared; it must not be used afterwards.</summary>
    public static void Return(byte[] array)
    {
        if (array.Length > 0) ArrayPool<byte>.Shared.Return(array, clearArray: true);
    }
}

/// <summary>
/// Flate (DeflateStream after the 2-byte zlib header, no Adler check, output kept up to an error or a cut), LZW,
/// ASCII85, ASCIIHex, RunLength and the PNG/TIFF predictors. Every stage writes into a pooled buffer capped at the
/// caller's limit (truncated, never more) and charges its output to the budget. Image codecs (DCT, JPX, CCITT, JBIG2)
/// are refused.
/// </summary>
internal static class PdfFilters
{
    /// <summary>A growing output buffer from the pool that stops at a cap.</summary>
    sealed class Output(int cap, int hint)
    {
        public byte[] Buffer = PdfPool.Rent(Math.Clamp(hint, 256, Math.Max(256, cap)));
        public int Length;
        public bool Full;

        public void Add(byte b)
        {
            if (Length >= cap) { Full = true; return; }
            if (Length == Buffer.Length) Grow(1);
            Buffer[Length++] = b;
        }

        public void Add(ReadOnlySpan<byte> bytes)
        {
            int n = Math.Min(bytes.Length, cap - Length);
            if (n < bytes.Length) Full = true;
            if (n <= 0) return;
            if (Length + n > Buffer.Length) Grow(n);
            bytes[..n].CopyTo(Buffer.AsSpan(Length));
            Length += n;
        }

        public Span<byte> Reserve(int want)
        {
            int n = Math.Min(want, cap - Length);
            if (n <= 0) { Full = true; return default; }
            if (Length + n > Buffer.Length) Grow(n);
            return Buffer.AsSpan(Length, n);
        }

        void Grow(int need)
        {
            long size = Math.Max((long)Buffer.Length * 2, (long)Length + need);
            var bigger = PdfPool.Rent((int)Math.Min(size, Math.Max(cap, Length + need)));
            Buffer.AsSpan(0, Length).CopyTo(bigger);
            PdfPool.Return(Buffer);
            Buffer = bigger;
        }

        public PdfBytes Result(bool truncated) => new(Buffer, Length, truncated || Full, Pooled: true);
    }

    /// <summary>
    /// Decodes data through a stream's /Filter and /DecodeParms (single values or arrays). Takes over raw: it is given
    /// back once a stage has read it, or returned as the result when there is no filter. Returns null when a filter is
    /// an image codec or unknown; returns what was decoded, Truncated, when a stage hit the cap, broke or was cut midway.
    /// </summary>
    public static PdfBytes? Decode(PdfBytes raw, PdfObject? filter, PdfObject? parms, int cap, ExtractBudget budget)
    {
        var names = new List<string>(2);
        var options = new List<PdfDict?>(2);
        switch (filter)
        {
            case null or PdfNull: break;
            case PdfName n: names.Add(n.Value); options.Add(parms as PdfDict ?? (parms as PdfArray)?[0] as PdfDict); break;
            case PdfArray a:
                for (int i = 0; i < a.Count && i < 8; i++)
                {
                    if (a[i] is not PdfName fn) { raw.Release(); return null; }
                    names.Add(fn.Value);
                    options.Add(parms is PdfArray pa ? pa[i] as PdfDict : i == 0 ? parms as PdfDict : null);
                }
                break;
            default: raw.Release(); return null;
        }
        var data = raw;
        bool truncated = raw.Truncated;
        for (int i = 0; i < names.Count; i++)
        {
            budget.Check();
            PdfBytes? next = names[i] switch
            {
                "FlateDecode" or "Fl" => Then(Flate(data.Array, 0, data.Length, cap), options[i], cap),
                "LZWDecode" or "LZW" => Then(Lzw(data.Span, cap, options[i]), options[i], cap),
                "ASCII85Decode" or "A85" => Ascii85(data.Span, cap),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data.Span, cap),
                "RunLengthDecode" or "RL" => RunLength(data.Span, cap),
                "Crypt" => IsIdentityCrypt(options[i]) ? data : null,
                _ => null,
            };
            if (next is not { } value) { data.Release(); return null; }
            if (!ReferenceEquals(value.Array, data.Array)) data.Release();
            data = value;
            budget.Decoded(value.Length);
            truncated |= value.Truncated;
        }
        return data with { Truncated = truncated };
    }

    /// <summary>The predictor stage after Flate or LZW; the decoded bytes go back to the pool when it made new ones.</summary>
    static PdfBytes Then(PdfBytes decoded, PdfDict? parms, int cap)
    {
        var predicted = Predict(decoded, parms, cap);
        if (!ReferenceEquals(predicted.Array, decoded.Array)) decoded.Release();
        return predicted;
    }

    static bool IsIdentityCrypt(PdfDict? parms) => parms?.Name("Name") is null or "Identity";

    /// <summary>The filter names are only image codecs this reader never decodes.</summary>
    public static bool IsImageCodec(string name) => name is "DCTDecode" or "DCT" or "JPXDecode" or "CCITTFaxDecode" or "CCF" or "JBIG2Decode";

    /// <summary>
    /// Inflates zlib or raw deflate data; keeps what came out before an error; never checks Adler-32. Data that ends
    /// before the deflate stream's last block (a cut file, a /Length too short) is Truncated: the inflater then asked
    /// for more input than there was, which it never does once a stream is complete.
    /// </summary>
    public static PdfBytes Flate(byte[] array, int offset, int length, int cap)
    {
        var data = array.AsSpan(offset, length);
        int skip = 0;
        if (data.Length >= 2 && (data[0] & 0x0F) == 8 && (data[0] >> 4) <= 7 && ((data[0] << 8) | data[1]) % 31 == 0)
            skip = (data[1] & 0x20) != 0 ? 6 : 2; // a preset dictionary id follows when FDICT is set
        var output = new Output(cap, Math.Min(cap, (int)Math.Min(int.MaxValue / 2, (long)data.Length * 4 + 1024)));
        if (skip >= data.Length) return output.Result(false);
        bool broken = false;
        var input = new WatchedInput(array, offset + skip, length - skip);
        try
        {
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            while (!output.Full)
            {
                var span = output.Reserve(16 << 10);
                if (span.IsEmpty) break;
                int n = inflate.Read(span);
                if (n <= 0) break;
                output.Length += n;
            }
        }
        catch (InvalidDataException) { broken = true; }
        catch (IOException) { broken = true; }
        return output.Result(broken || input.RanOut && !output.Full);
    }

    /// <summary>Compressed bytes for the inflater, noting whether it asked for more after the last of them.</summary>
    sealed class WatchedInput(byte[] array, int offset, int length) : Stream
    {
        int _position;

        /// <summary>A read came after the end of the data: the deflate stream wasn't finished.</summary>
        public bool RanOut { get; private set; }

        public override int Read(byte[] buffer, int offset1, int count) => Read(buffer.AsSpan(offset1, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, length - _position);
            if (n <= 0)
            {
                if (!buffer.IsEmpty) RanOut = true;
                return 0;
            }
            array.AsSpan(offset + _position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset1, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset1, int count) => throw new NotSupportedException();
    }

    /// <summary>LZW with the PDF's variable code width (9-12 bits) and EarlyChange (default 1); a code that can't be ends it, Truncated.</summary>
    public static PdfBytes Lzw(ReadOnlySpan<byte> data, int cap, PdfDict? parms)
    {
        bool broken = false;
        int early = parms?["EarlyChange"] is PdfNumber e ? (int)e.Value : 1;
        early = early == 0 ? 0 : 1;
        var output = new Output(cap, Math.Min(cap, data.Length * 3 + 256));
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var lengths = new short[4096];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; lengths[i] = 1; }
        int next = 258, width = 9, previous = -1;
        int bitBuffer = 0, bits = 0;
        Span<byte> word = stackalloc byte[4096];
        int pos = 0;
        while (!output.Full)
        {
            while (bits < width && pos < data.Length) { bitBuffer = (bitBuffer << 8) | data[pos++]; bits += 8; }
            if (bits < width) break;
            int code = (bitBuffer >> (bits - width)) & ((1 << width) - 1);
            bits -= width;
            bitBuffer &= (1 << bits) - 1;
            if (code == 256) { next = 258; width = 9; previous = -1; continue; }
            if (code == 257) break;
            int length;
            if (code < next && (code < 256 || lengths[code] > 0))
            {
                length = Expand(code, prefix, suffix, lengths, word);
                if (previous >= 0 && next < 4096)
                {
                    prefix[next] = (short)previous;
                    suffix[next] = word[0];
                    lengths[next] = (short)(lengths[previous] + 1);
                    next++;
                }
            }
            else if (code == next && previous >= 0)
            {
                // The KwKwK case: the new entry is previous + previous's first byte.
                int pl = Expand(previous, prefix, suffix, lengths, word);
                if (pl == 0 || pl >= word.Length) { broken = true; break; }
                word[pl] = word[0];
                length = pl + 1;
                if (next < 4096)
                {
                    prefix[next] = (short)previous;
                    suffix[next] = word[0];
                    lengths[next] = (short)length;
                    next++;
                }
            }
            else { broken = true; break; } // a code that can't be: stop with what was decoded
            output.Add(word[..length]);
            previous = code;
            if (next + early >= (1 << width) && width < 12) width++;
        }
        return output.Result(broken);
    }

    static int Expand(int code, short[] prefix, byte[] suffix, short[] lengths, Span<byte> word)
    {
        int length = lengths[code];
        if (length <= 0 || length > word.Length) return 0;
        for (int i = length - 1, c = code; i >= 0 && c >= 0; i--, c = prefix[c]) word[i] = suffix[c];
        return length;
    }

    /// <summary>ASCII base-85 ("z" for four zero bytes, "~>" ends, a short last group padded).</summary>
    public static PdfBytes Ascii85(ReadOnlySpan<byte> data, int cap)
    {
        var output = new Output(cap, Math.Min(cap, data.Length));
        int i = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~') i = 2;
        uint group = 0;
        int count = 0;
        Span<byte> four = stackalloc byte[4];
        for (; i < data.Length && !output.Full; i++)
        {
            byte c = data[i];
            if (PdfLexer.IsWhite(c)) continue;
            if (c == '~') break;
            if (c == 'z' && count == 0) { output.Add([0, 0, 0, 0]); continue; }
            if (c is < (byte)'!' or > (byte)'u') continue;
            group = unchecked(group * 85 + (uint)(c - '!'));
            if (++count == 5)
            {
                four[0] = (byte)(group >> 24); four[1] = (byte)(group >> 16); four[2] = (byte)(group >> 8); four[3] = (byte)group;
                output.Add(four);
                group = 0;
                count = 0;
            }
        }
        if (count > 1)
        {
            for (int k = count; k < 5; k++) group = unchecked(group * 85 + 84);
            four[0] = (byte)(group >> 24); four[1] = (byte)(group >> 16); four[2] = (byte)(group >> 8); four[3] = (byte)group;
            output.Add(four[..(count - 1)]);
        }
        return output.Result(false);
    }

    /// <summary>Hex pairs up to "&gt;"; white space ignored; an odd last digit is followed by 0.</summary>
    public static PdfBytes AsciiHex(ReadOnlySpan<byte> data, int cap)
    {
        var output = new Output(cap, Math.Min(cap, data.Length / 2 + 1));
        int high = -1;
        foreach (byte c in data)
        {
            if (c == '>') break;
            int v = PdfLexer.HexValue(c);
            if (v < 0) continue;
            if (high < 0) high = v;
            else { output.Add((byte)(high * 16 + v)); high = -1; if (output.Full) break; }
        }
        if (high >= 0) output.Add((byte)(high * 16));
        return output.Result(false);
    }

    /// <summary>Run-length: n &lt; 128 copies n+1 bytes, n &gt; 128 repeats the next byte 257-n times, 128 ends.</summary>
    public static PdfBytes RunLength(ReadOnlySpan<byte> data, int cap)
    {
        var output = new Output(cap, Math.Min(cap, data.Length * 2 + 16));
        int i = 0;
        while (i < data.Length && !output.Full)
        {
            int n = data[i++];
            if (n == 128) break;
            if (n < 128)
            {
                int take = Math.Min(n + 1, data.Length - i);
                output.Add(data.Slice(i, take));
                i += take;
            }
            else if (i < data.Length)
            {
                byte b = data[i++];
                for (int k = 0; k < 257 - n && !output.Full; k++) output.Add(b);
            }
        }
        return output.Result(false);
    }

    /// <summary>Undoes a PNG (10-15) or TIFF (2) predictor from /DecodeParms; other values leave the data as it is.</summary>
    public static PdfBytes Predict(PdfBytes data, PdfDict? parms, int cap)
    {
        int predictor = parms?["Predictor"] is PdfNumber p ? (int)p.Value : 1;
        if (predictor < 2) return data;
        int colors = Math.Clamp(parms?["Colors"] is PdfNumber c ? (int)c.Value : 1, 1, 32);
        int bpc = parms?["BitsPerComponent"] is PdfNumber b ? (int)b.Value : 8;
        if (bpc is not (1 or 2 or 4 or 8 or 16)) bpc = 8;
        long columnsLong = parms?["Columns"] is PdfNumber cols ? (long)cols.Value : 1;
        if (columnsLong is < 1 or > 1 << 24) columnsLong = 1;
        int columns = (int)columnsLong;
        int pixelBytes = Math.Max(1, (colors * bpc + 7) / 8);
        long rowBytesLong = ((long)colors * bpc * columns + 7) / 8;
        if (rowBytesLong <= 0) return data;
        var src = data.Span;
        // A row can't be longer than the data: a lying /Columns never allocates more than the data's size.
        int rowBytes = (int)Math.Min(rowBytesLong, Math.Max(1, src.Length));
        if (predictor == 2)
        {
            if (bpc != 8) return data;
            var copy = PdfPool.Rent(src.Length);
            src.CopyTo(copy);
            for (int row = 0; row < src.Length; row += rowBytes)
                for (int i = row + pixelBytes; i < Math.Min(row + rowBytes, src.Length); i++)
                    copy[i] = unchecked((byte)(copy[i] + copy[i - pixelBytes]));
            return new PdfBytes(copy, src.Length, data.Truncated, Pooled: true);
        }
        var output = new Output(cap, Math.Min(cap, src.Length));
        var prior = PdfPool.Rent(rowBytes);
        var current = PdfPool.Rent(rowBytes);
        try
        {
            prior.AsSpan(0, rowBytes).Clear();
            int pos = 0;
            while (pos < src.Length && !output.Full)
            {
                int type = src[pos++];
                int take = Math.Min(rowBytes, src.Length - pos);
                src.Slice(pos, take).CopyTo(current);
                if (take < rowBytes) current.AsSpan(take, rowBytes - take).Clear();
                pos += take;
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= pixelBytes ? current[i - pixelBytes] : 0;
                    int up = prior[i];
                    int upLeft = i >= pixelBytes ? prior[i - pixelBytes] : 0;
                    current[i] = type switch
                    {
                        1 => unchecked((byte)(current[i] + left)),
                        2 => unchecked((byte)(current[i] + up)),
                        3 => unchecked((byte)(current[i] + ((left + up) >> 1))),
                        4 => unchecked((byte)(current[i] + Paeth(left, up, upLeft))),
                        _ => current[i],
                    };
                }
                output.Add(current.AsSpan(0, take));
                (prior, current) = (current, prior);
            }
        }
        finally
        {
            PdfPool.Return(prior);
            PdfPool.Return(current);
        }
        return output.Result(data.Truncated);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
