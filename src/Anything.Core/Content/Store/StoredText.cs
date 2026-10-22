using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Store;

/// <summary>
/// Stored-text blob codec: varint fieldCount; per field (u8 DocField, varint byteLength, UTF-8); then the UTF-8 body.
/// The blob is Brotli q5 compressed (window 22, or the smallest that holds the blob: the same bytes, set up faster), or
/// kept as it is when shorter than 128 bytes or when compressing doesn't
/// make it smaller; the row tells them apart (stored length == raw length means "as it is"). A document with no fields and
/// no body stores nothing (raw length 0). Blobs are capped at 8 MB: longer bodies are cut at a character boundary.
/// </summary>
internal static class StoredText
{
    /// <summary>Longest field value kept, in UTF-8 bytes (the sink caps far below: 4 KB, keywords 64 KB).</summary>
    const int MaxFieldBytes = 256 << 10;
    const int MaxFields = 255;

    /// <summary>Encodes a document's fields and body; rawLength = the UTF-8 blob's length before compression.</summary>
    public static byte[] Encode(StoredDoc doc, out int rawLength)
    {
        byte[] blob = ArrayPool<byte>.Shared.Rent(Math.Max(16, Math.Min(SegmentFormat.MaxBlob, MaxBlobEstimate(doc))));
        try
        {
            rawLength = WriteBlob(doc, ref blob);
            return Compress(blob.AsSpan(0, rawLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blob);
        }
    }

    /// <summary>The raw length Encode would give, without compressing.</summary>
    public static int BlobLength(StoredDoc doc)
    {
        var (_, fields, body) = Plan(doc);
        return fields + body;
    }

    /// <summary>
    /// The blob holds the whole document: no field or body is cut by the caps (the writer then tokenizes the stored
    /// version instead, so terms only ever come from stored text). Counts bytes only when lengths come near a cap.
    /// </summary>
    internal static bool FitsWhole(StoredDoc doc)
    {
        long chars = doc.Body.Length;
        int count = 0;
        bool near = false;
        foreach (var (_, value) in doc.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            count++;
            chars += value.Length + 2 * VarInt.MaxLength;
            near |= value.Length * 3L > MaxFieldBytes;
        }
        if (!near && count <= MaxFields && chars * 3 <= SegmentFormat.MaxBlob / 2) return true;

        long fields = 0;
        int kept = 0;
        foreach (var (_, value) in doc.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            int n = Encoding.UTF8.GetByteCount(value);
            if (n > MaxFieldBytes || kept == MaxFields || fields + 1 + VarInt.MaxLength + n > SegmentFormat.MaxBlob / 2) return false;
            fields += 1 + VarInt.Size((ulong)n) + n;
            kept++;
        }
        return fields + VarInt.Size((ulong)kept) + Encoding.UTF8.GetByteCount(doc.Body) <= SegmentFormat.MaxBlob;
    }

    /// <summary>Compresses a blob, or returns it as it is when that is no larger (or it is short).</summary>
    internal static byte[] Compress(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < SegmentFormat.RawUnder) return blob.ToArray();
        var dest = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(blob.Length));
        try
        {
            if (BrotliEncoder.TryCompress(blob, dest, out int written, SegmentFormat.BrotliQuality, WindowFor(blob.Length))
                && written < blob.Length)
                return dest.AsSpan(0, written).ToArray();
            return blob.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dest);
        }
    }

    /// <summary>
    /// The smallest Brotli window (16-22) that holds the whole blob: the output is the same as with window 22 for any blob
    /// up to 4 MB, and a small window sets the encoder up about four times faster (250 µs → 65 µs a document measured).
    /// </summary>
    internal static int WindowFor(int length)
    {
        int needed = length <= 1 ? 1 : System.Numerics.BitOperations.Log2((uint)(length + 15)) + 1; // 2^w - 16 >= length
        return Math.Clamp(needed, 16, SegmentFormat.BrotliWindow);
    }

    /// <summary>Decodes a blob; null when it is damaged.</summary>
    public static StoredDoc? Decode(ReadOnlySpan<byte> compressed, int rawLength)
    {
        if (rawLength == 0) return compressed.IsEmpty ? new StoredDoc("", [], null) : null;
        if (rawLength < 0 || rawLength > SegmentFormat.MaxBlob || compressed.IsEmpty || compressed.Length > rawLength) return null;
        if (compressed.Length == rawLength) return ParseBlob(compressed);
        var blob = ArrayPool<byte>.Shared.Rent(rawLength);
        try
        {
            if (!TryDecompress(compressed, rawLength, blob)) return null;
            return ParseBlob(blob.AsSpan(0, rawLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blob);
        }
    }

    /// <summary>Decompresses stored bytes into exactly rawLength bytes of dest; false when they don't decode to that.</summary>
    internal static bool TryDecompress(ReadOnlySpan<byte> stored, int rawLength, Span<byte> dest)
    {
        if (stored.Length == rawLength)
        {
            stored.CopyTo(dest);
            return true;
        }
        return BrotliDecoder.TryDecompress(stored, dest[..rawLength], out int written) && written == rawLength;
    }

    /// <summary>Parses a raw blob; null when it is malformed (bad counts, lengths or UTF-8).</summary>
    internal static StoredDoc? ParseBlob(ReadOnlySpan<byte> blob)
    {
        int pos = 0;
        if (!VarInt.TryRead(blob, ref pos, MaxFields, out int count)) return null;
        var fields = count == 0 ? [] : new List<(DocField, string)>(count);
        for (int i = 0; i < count; i++)
        {
            if (pos >= blob.Length) return null;
            var field = (DocField)blob[pos++];
            if (!VarInt.TryRead(blob, ref pos, MaxFieldBytes, out int length) || length > blob.Length - pos) return null;
            var value = blob.Slice(pos, length);
            if (!Utf8.IsValid(value)) return null;
            fields.Add((field, Encoding.UTF8.GetString(value)));
            pos += length;
        }
        var body = blob[pos..];
        if (!Utf8.IsValid(body)) return null;
        return new StoredDoc(Encoding.UTF8.GetString(body), fields, null);
    }

    static int MaxBlobEstimate(StoredDoc doc)
    {
        long n = 16 + doc.Body.Length * 3L;
        foreach (var (_, value) in doc.Fields) n += 16 + value.Length * 3L;
        return (int)Math.Min(n, SegmentFormat.MaxBlob);
    }

    /// <summary>A field value's UTF-8 length after the cap.</summary>
    static int FieldBytes(string value)
    {
        int n = Encoding.UTF8.GetByteCount(value);
        return n <= MaxFieldBytes ? n : Fit(value, MaxFieldBytes).Bytes;
    }

    /// <summary>Which fields are kept (the first count non-empty ones), the bytes of the fields part and of the body part.</summary>
    static (int Count, int Fields, int Body) Plan(StoredDoc doc)
    {
        int count = 0;
        long fields = 0;
        foreach (var (_, value) in doc.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (count == MaxFields) break;
            int n = FieldBytes(value);
            if (fields + 1 + VarInt.MaxLength + n > SegmentFormat.MaxBlob / 2) break;
            fields += 1 + VarInt.Size((ulong)n) + n;
            count++;
        }
        if (count == 0 && doc.Body.Length == 0) return (0, 0, 0);
        fields += VarInt.Size((ulong)count);
        long body = Encoding.UTF8.GetByteCount(doc.Body);
        if (fields + body > SegmentFormat.MaxBlob) body = Fit(doc.Body, SegmentFormat.MaxBlob - (int)fields).Bytes;
        return (count, (int)fields, (int)body);
    }

    /// <summary>Writes the blob into buffer (grown when needed); returns its length.</summary>
    static int WriteBlob(StoredDoc doc, ref byte[] buffer)
    {
        var (count, fieldsLength, bodyLength) = Plan(doc);
        int total = fieldsLength + bodyLength;
        if (total == 0) return 0;
        if (buffer.Length < total)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(total);
        }
        var d = buffer.AsSpan(0, total);
        int pos = VarInt.Write(d, (ulong)count);
        int written = 0;
        foreach (var (field, value) in doc.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (written == count) break;
            int n = FieldBytes(value);
            d[pos++] = (byte)field;
            pos += VarInt.Write(d[pos..], (ulong)n);
            pos += Write(value, n, d[pos..]);
            written++;
        }
        if (bodyLength > 0) pos += Write(doc.Body, bodyLength, d[pos..]);
        return pos;
    }

    /// <summary>Writes the first bytes UTF-8 bytes of text (a whole-character prefix, as Fit measured it).</summary>
    static int Write(string text, int bytes, Span<byte> dest)
    {
        if (bytes == Encoding.UTF8.GetByteCount(text)) return Encoding.UTF8.GetBytes(text, dest);
        Utf8.FromUtf16(text, dest[..bytes], out _, out int written, replaceInvalidSequences: true, isFinalBlock: true);
        return written;
    }

    /// <summary>The longest prefix of text whose UTF-8 fits in maxBytes, cut between whole characters.</summary>
    static FitResult Fit(string text, int maxBytes)
    {
        if (maxBytes <= 0) return new FitResult(0, 0);
        var scratch = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            Utf8.FromUtf16(text, scratch.AsSpan(0, maxBytes), out int chars, out int bytes, replaceInvalidSequences: true, isFinalBlock: true);
            return new FitResult(chars, bytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    readonly record struct FitResult(int Chars, int Bytes);
}
