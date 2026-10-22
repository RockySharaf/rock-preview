using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Picture details from headers only, never pixels: size, DPI, colour mode, EXIF, GPS, IPTC and XMP. The format comes
/// from the file's magic, not its name. Every reader seeks to the structures it needs with exact-range reads and stops at
/// its byte cap; a broken structure (segment, IFD, chunk, box) is skipped and what was read stays.
/// </summary>
internal sealed class PictureReader : IContentReader
{
    public ContentKind Kind => ContentKind.Picture;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "jpg" or "jpeg" or "jpe" or "jfif" or "tif" or "tiff" or "png" or "gif" or "bmp" or "webp" or "heic" or "heif" or "avif";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        source.PreferExactRanges();
        Span<byte> head = stackalloc byte[16];
        int n = source.ReadFully(0, head);
        head = head[..n];
        var parts = new PictureParts(budget);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) Jpeg.Read(source, sink, budget, parts);
        else if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) Png.Read(source, sink, budget, parts);
        else if (head.StartsWith("II"u8) || head.StartsWith("MM"u8)) Tiff.Read(source, sink, budget, TiffMode.Image, parts);
        else if (head.StartsWith("GIF8"u8)) GifBmp.ReadGif(source, sink);
        else if (head.StartsWith("BM"u8)) GifBmp.ReadBmp(source, sink);
        else if (head.StartsWith("RIFF"u8) && n >= 12 && head[8..12].SequenceEqual("WEBP"u8)) Riff.Read(source, sink, budget, parts);
        else if (n >= 12 && head[4..8].SequenceEqual("ftyp"u8)) Heif.Read(source, sink, budget, parts);
        else return DocStatus.Unsupported;
        return parts.Partial ? DocStatus.Truncated : DocStatus.Ok;
    }
}

/// <summary>Bookkeeping shared by one picture's readers: units that failed (the result is partial).</summary>
internal sealed class PictureParts(ExtractBudget budget)
{
    public ExtractBudget Budget { get; } = budget;

    /// <summary>Some unit (segment, IFD, chunk, box, packet) was skipped as damaged.</summary>
    public bool Partial { get; set; }

    /// <summary>
    /// Runs one unit, keeping its damage to itself: a format fault skips the unit (marking the picture partial); limits,
    /// cancellation and I/O errors go on up.
    /// </summary>
    public void Unit(Action read)
    {
        try { read(); }
        catch (Exception e) when (IsFormatFault(e)) { Partial = true; }
    }

    /// <summary>
    /// An exception that means bad data in one unit (not a limit, a cancellation or an I/O error, also not one wrapping
    /// an I/O error); the mail readers use the same rule for one MIME part.
    /// </summary>
    public static bool IsFormatFault(Exception e) => e is not (ContentLimitException or OperationCanceledException or ContentIoException or OutOfMemoryException)
        && e is InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException
            or DecoderFallbackException or InvalidOperationException or IOException or System.Xml.XmlException or EndOfStreamException
        && IoErrors.Wrapped(e) == null;
}

/// <summary>Value helpers for picture metadata: EXIF dates, rationals, resolution, text in unknown encodings, ICC names.</summary>
internal static class PictureValues
{
    /// <summary>The sane range of a resolution in dots per inch.</summary>
    public static bool SaneDpi(double dpi) => double.IsFinite(dpi) && dpi >= 1 && dpi <= 100_000;

    /// <summary>A resolution in a TIFF/EXIF unit (2 inch, 3 centimetre, 1 none = unknown) as dots per inch; 0 when unknown.</summary>
    public static int Dpi(double value, int unit)
    {
        double dpi = unit switch { 2 => value, 3 => value * 2.54, _ => 0 };
        return SaneDpi(dpi) ? (int)Math.Round(dpi) : 0;
    }

    /// <summary>
    /// An EXIF date ("2023:07:14 15:30:12", also with '-' or '/' separators, NULs and spaces trimmed) as FILETIME UTC: the
    /// offset ("+02:00", OffsetTimeOriginal) when given, else the PC's time zone; 0 when unknown ("0000:00:00 00:00:00",
    /// out of range). A local time in a daylight-saving gap uses the zone's standard offset instead of failing.
    /// </summary>
    public static long ExifDate(string? text, string? offset)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim('\0', ' ');
        if (s.Length < 10) return 0;
        if (!Int(s, 0, 4, out int year) || !Int(s, 5, 2, out int month) || !Int(s, 8, 2, out int day)) return 0;
        int hour = 0, minute = 0, second = 0;
        if (s.Length >= 16 && (!Int(s, 11, 2, out hour) || !Int(s, 14, 2, out minute))) return 0;
        if (s.Length >= 19 && !Int(s, 17, 2, out second)) return 0;
        if (year is < 1601 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return 0;
        if (hour > 24 || minute > 59 || second > 60) return 0;
        if (second == 60) second = 59;
        var local = new DateTime(year, month, day, Math.Min(hour, 23), minute, second, DateTimeKind.Unspecified);
        if (hour == 24) local = local.AddHours(1);
        TimeSpan utcOffset;
        if (Offset(offset) is { } given) utcOffset = given;
        else
        {
            var zone = TimeZoneInfo.Local;
            utcOffset = zone.IsInvalidTime(local) ? zone.BaseUtcOffset : zone.GetUtcOffset(local);
        }
        long ticks = local.Ticks - utcOffset.Ticks;
        if (ticks < new DateTime(1601, 1, 1).Ticks || ticks > DateTime.MaxValue.Ticks) return 0;
        return new DateTime(ticks, DateTimeKind.Utc).ToFileTimeUtc();
    }

    static bool Int(string s, int at, int length, out int value)
    {
        value = 0;
        return at + length <= s.Length && int.TryParse(s.AsSpan(at, length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"+02:00" / "-05:30" / "Z" as a UTC offset; null when absent or unparsable.</summary>
    static TimeSpan? Offset(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim('\0', ' ');
        if (s is "Z" or "z") return TimeSpan.Zero;
        if (s.Length < 6 || s[0] is not ('+' or '-') || s[3] != ':') return null;
        if (!Int(s, 1, 2, out int h) || !Int(s, 4, 2, out int m) || h > 14 || m > 59) return null;
        var t = new TimeSpan(h, m, 0);
        return s[0] == '-' ? -t : t;
    }

    /// <summary>Text of unknown 8-bit encoding: UTF-8 when it is valid, else the ANSI code page; NULs and spaces trimmed.</summary>
    public static string Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        var s = Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : TextDecoding.Ansi().GetString(bytes);
        return s.Trim();
    }

    /// <summary>
    /// An ICC profile's description ('desc' tag: textDescriptionType or v4 multiLocalizedUnicodeType, English preferred);
    /// null when there is none. Every offset is checked against the profile.
    /// </summary>
    public static string? IccDescription(ReadOnlySpan<byte> p)
    {
        if (p.Length < 132) return null;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(p[128..]);
        for (int i = 0; i < Math.Min(count, 256u); i++)
        {
            int at = 132 + i * 12;
            if (at + 12 > p.Length) return null;
            if (BinaryPrimitives.ReadUInt32BigEndian(p[at..]) != 0x64657363) continue; // 'desc'
            long offset = BinaryPrimitives.ReadUInt32BigEndian(p[(at + 4)..]), size = BinaryPrimitives.ReadUInt32BigEndian(p[(at + 8)..]);
            if (offset + 12 > p.Length || size < 12) return null;
            var tag = p.Slice((int)offset, (int)Math.Min(size, p.Length - offset));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(tag);
            if (type == 0x64657363 && tag.Length >= 12) // textDescriptionType
            {
                long length = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (length <= 0 || 12 + length > tag.Length) return null;
                return Clean(Encoding.Latin1.GetString(tag.Slice(12, (int)length)));
            }
            if (type == 0x6D6C7563 && tag.Length >= 16) // 'mluc'
            {
                long records = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]), recordSize = BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
                if (recordSize < 12) return null;
                string? first = null;
                for (long r = 0; r < Math.Min(records, 64); r++)
                {
                    long rec = 16 + r * recordSize;
                    if (rec + 12 > tag.Length) break;
                    long length = BinaryPrimitives.ReadUInt32BigEndian(tag[(int)(rec + 4)..]), off = BinaryPrimitives.ReadUInt32BigEndian(tag[(int)(rec + 8)..]);
                    if (off + length > tag.Length) continue;
                    string text = Clean(Encoding.BigEndianUnicode.GetString(tag.Slice((int)off, (int)(length & ~1L))));
                    if (tag[(int)rec] == 'e' && tag[(int)rec + 1] == 'n') return text;
                    first ??= text;
                }
                return first;
            }
            return null;
        }
        return null;
    }

    static string Clean(string s) => s.TrimEnd('\0').Trim();
}
