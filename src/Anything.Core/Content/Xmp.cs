using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Anything.Core.Content.Extract;

namespace Anything.Core.Content;

/// <summary>
/// XMP packet → fields and picture details (dc:, xmp:, photoshop:, tiff:, exif:, xmpTPg:); XmlReader, DTDs prohibited,
/// no recursion. dc:description is the caption (IPTC 2:120), so it becomes Comment.
/// </summary>
internal static class Xmp
{
    const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    const string Dc = "http://purl.org/dc/elements/1.1/";
    const string XmpNs = "http://ns.adobe.com/xap/1.0/";
    const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    const string Tiff = "http://ns.adobe.com/tiff/1.0/";
    const string Exif = "http://ns.adobe.com/exif/1.0/";
    const string Tpg = "http://ns.adobe.com/xap/1.0/t/pg/";
    const string Font = "http://ns.adobe.com/xap/1.0/sType/Font#";
    const string Mm = "http://ns.adobe.com/xap/1.0/mm/";
    const string GImg = "http://ns.adobe.com/xap/1.0/g/img/";
    const string XmlNs = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Most characters of one packet parsed (Photoshop's document ancestors can make packets tens of MB; they are skipped as they stream by).</summary>
    const int MaxChars = 64 << 20;
    const int MaxDepth = 256;

    enum Prop
    {
        None, Title, Description, Subject, Creator, CreatorTool, Rating, ColorMode, IccProfile, City, State, Country, DateTaken,
        Latitude, Longitude, Make, Model, Orientation, XResolution, YResolution, ResolutionUnit, Iso, FNumber, Exposure, Focal,
        ColorSpace, Fonts, Plates, NPages,
    }

    static Prop PropOf(string ns, string local) => ns switch
    {
        Dc => local switch { "title" => Prop.Title, "description" => Prop.Description, "subject" => Prop.Subject, "creator" => Prop.Creator, _ => Prop.None },
        XmpNs => local switch { "CreatorTool" => Prop.CreatorTool, "Rating" => Prop.Rating, _ => Prop.None },
        Photoshop => local switch
        {
            "ColorMode" => Prop.ColorMode, "ICCProfile" => Prop.IccProfile, "City" => Prop.City, "State" => Prop.State,
            "Country" => Prop.Country, _ => Prop.None,
        },
        Tiff => local switch
        {
            "Make" => Prop.Make, "Model" => Prop.Model, "Orientation" => Prop.Orientation, "XResolution" => Prop.XResolution,
            "YResolution" => Prop.YResolution, "ResolutionUnit" => Prop.ResolutionUnit, _ => Prop.None,
        },
        Exif => local switch
        {
            "DateTimeOriginal" => Prop.DateTaken, "GPSLatitude" => Prop.Latitude, "GPSLongitude" => Prop.Longitude,
            "ISOSpeedRatings" => Prop.Iso, "FNumber" => Prop.FNumber, "ExposureTime" => Prop.Exposure, "FocalLength" => Prop.Focal,
            "ColorSpace" => Prop.ColorSpace, _ => Prop.None,
        },
        Tpg => local switch { "Fonts" => Prop.Fonts, "PlateNames" => Prop.Plates, "NPages" => Prop.NPages, _ => Prop.None },
        _ => Prop.None,
    };

    /// <summary>Subtrees never read: edit history, placed documents' metadata, thumbnails, swatches.</summary>
    static bool IsSkipped(string ns, string local) => ns switch
    {
        Photoshop => local is "DocumentAncestors" or "TextLayers",
        Mm => local is "History" or "Pantry" or "Ingredients" or "Manifest" or "DerivedFrom",
        XmpNs => local is "Thumbnails",
        GImg => local is "image",
        Tpg => local is "SwatchGroups",
        _ => false,
    };

    /// <summary>
    /// Reads a packet (UTF-8 or UTF-16) into the sink; never throws. Bad XML stops the read but keeps the fields of
    /// elements completed before it; a DTD is refused, so a packet that declares one adds nothing. False when the packet's
    /// shape was refused part-way (<see cref="XmlShape"/>: nested too deep, too many attributes, a tag too long), so the
    /// caller can count the document as partly read.
    /// </summary>
    public static bool Read(ReadOnlySpan<byte> packet, ExtractSink sink)
        => packet.IsEmpty || Read(new ReadOnlyMemory<byte>(packet.ToArray()), sink);

    /// <summary>
    /// Reads a packet without copying it: the XML is decoded as it is parsed, so skipped history costs no memory, and text
    /// is taken a chunk at a time up to the field caps, so a huge value never becomes one string. Every packet passes
    /// through the <see cref="XmlShape"/> check as it is parsed (whoever calls: PDF, Office, pictures, design files), so
    /// XmlReader never spends hundreds of MB on a hostile one.
    /// </summary>
    public static bool Read(ReadOnlyMemory<byte> packet, ExtractSink sink)
    {
        if (packet.IsEmpty) return true;
        var state = new State(sink);
        bool allowed = true;
        try
        {
            using var reader = Open(packet);
            state.Run(reader);
        }
        catch (InvalidDataException) { allowed = false; } // the shape check stopped it
        catch (Exception e) when (e is not OutOfMemoryException) { }
        finally
        {
            try { state.Finish(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        return allowed;
    }

    /// <summary>
    /// The packet's dc:format (a MIME type such as "application/pdf" or "image/jpeg"); null when it has none or isn't
    /// readable. A cheap pass that stops at the first dc:format and adds nothing anywhere, so a reader can pick the
    /// document's own packet before reading one (EPS and AI files also carry their placed images' packets).
    /// </summary>
    public static string? Format(ReadOnlySpan<byte> packet) => packet.IsEmpty ? null : Format(new ReadOnlyMemory<byte>(packet.ToArray()));

    /// <summary>The packet's dc:format, reading the packet in place.</summary>
    public static string? Format(ReadOnlyMemory<byte> packet)
    {
        if (packet.IsEmpty) return null;
        try
        {
            using var r = Open(packet);
            bool more = r.Read();
            while (more)
            {
                if (r.Depth > MaxDepth) return null;
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (IsSkipped(r.NamespaceURI, r.LocalName)) { r.Skip(); more = !r.EOF; continue; }
                    if (r.NamespaceURI == Rdf && r.LocalName == "Description" && r.GetAttribute("format", Dc) is { } attribute)
                        return attribute.Trim() is { Length: > 0 } a ? a : null;
                    if (r.NamespaceURI == Dc && r.LocalName == "format")
                        return r.IsEmptyElement ? null : ElementText(r, 256) is { Length: > 0 } t ? t : null;
                }
                more = r.Read();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>The text inside the current element (rdf:Alt and other wrappers included), at most max characters, trimmed.</summary>
    static string ElementText(XmlReader r, int max)
    {
        int depth = r.Depth;
        var sb = new StringBuilder();
        var chunk = new char[256];
        while (r.Read() && r.Depth > depth)
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                AppendCapped(r, sb, max, chunk);
        return sb.ToString().Trim();
    }

    /// <summary>Appends a text node's value up to max characters in all, reading it in chunks and skipping the rest unread into memory.</summary>
    static void AppendCapped(XmlReader r, StringBuilder sb, int max, char[] chunk)
    {
        if (!r.CanReadValueChunk)
        {
            if (sb.Length < max) { string v = r.Value; sb.Append(v, 0, Math.Min(v.Length, max - sb.Length)); }
            return;
        }
        int n;
        while ((n = r.ReadValueChunk(chunk, 0, chunk.Length)) > 0)
            if (sb.Length < max) sb.Append(chunk, 0, Math.Min(n, max - sb.Length));
    }

    /// <summary>
    /// An XmlReader over a packet (UTF-8 or UTF-16 by its byte order mark or zero bytes), DTDs prohibited, nothing
    /// resolved, its bytes checked by <see cref="XmlShape"/> as they are read (InvalidDataException past its limits).
    /// </summary>
    static XmlReader Open(ReadOnlyMemory<byte> packet)
    {
        var (skip, encoding) = EncodingOf(packet.Span);
        var bytes = packet[skip..];
        var stream = MemoryMarshal.TryGetArray(bytes, out var array)
            ? new MemoryStream(array.Array!, array.Offset, array.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);
        var text = new StreamReader(new XmlShapeStream(stream), encoding, detectEncodingFromByteOrderMarks: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxChars,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            CheckCharacters = false,
            CloseInput = true,
            NameTable = new CappedNameTable(),
        };
        return XmlReader.Create(text, settings);
    }

    /// <summary>
    /// Finds the first packet in a byte window: "&lt;x:xmpmeta" or the older "&lt;x:xapmeta" … its end tag, or a bare
    /// "&lt;rdf:RDF" … "&lt;/rdf:RDF&gt;", in UTF-8 or UTF-16 (either byte order). A packet cut off by the window's end runs
    /// to the end. (-1, 0) when none.
    /// </summary>
    public static (int Start, int Length) FindPacket(ReadOnlySpan<byte> window)
    {
        int best = -1, bestEnd = 0;
        foreach (var (open, close) in Tags)
        {
            int at = Find(window, open, out int enc);
            if (at < 0 || (best >= 0 && at >= best)) continue;
            int end = window[at..].IndexOf(close[enc]);
            best = at;
            bestEnd = end < 0 ? window.Length : at + end + close[enc].Length;
        }
        return best < 0 ? (-1, 0) : (best, bestEnd - best);
    }

    /// <summary>
    /// Earliest start of a tag in UTF-8, UTF-16 LE or UTF-16 BE. A BE hit one byte before an LE hit is the same bytes read
    /// both ways: the byte order mark of the packet header before it decides, else little-endian.
    /// </summary>
    static int Find(ReadOnlySpan<byte> window, byte[][] forms, out int encoding)
    {
        Span<int> at = [window.IndexOf(forms[0]), window.IndexOf(forms[1]), window.IndexOf(forms[2])];
        if (at[2] >= 0 && at[1] == at[2] + 1)
        {
            var before = window[Math.Max(0, at[2] - 256)..at[2]];
            if (before.LastIndexOf((ReadOnlySpan<byte>)[0xFE, 0xFF]) > before.LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xFE])) at[1] = -1;
            else at[2] = -1;
        }
        int best = -1;
        encoding = 0;
        for (int e = 0; e < at.Length; e++)
            if (at[e] >= 0 && (best < 0 || at[e] < best)) { best = at[e]; encoding = e; }
        return best;
    }

    // Per tag: [open forms (UTF-8, UTF-16 LE, UTF-16 BE)], [close forms in the same order].
    static readonly (byte[][] Open, byte[][] Close)[] Tags = BuildTags();

    static (byte[][], byte[][])[] BuildTags()
    {
        var encodings = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode };
        var list = new List<(byte[][], byte[][])>();
        foreach (var (open, close) in (ReadOnlySpan<(string, string)>)[("<x:xmpmeta", "</x:xmpmeta>"), ("<x:xapmeta", "</x:xapmeta>"), ("<rdf:RDF", "</rdf:RDF>")])
            list.Add(([.. encodings.Select(e => e.GetBytes(open))], [.. encodings.Select(e => e.GetBytes(close))]));
        return [.. list];
    }

    /// <summary>The packet's encoding and the length of its byte order mark: a BOM, else zero bytes tell UTF-16, else UTF-8.</summary>
    static (int Skip, Encoding Encoding) EncodingOf(ReadOnlySpan<byte> p)
    {
        if (p.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (3, Encoding.UTF8);
        if (p.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (2, Encoding.Unicode);
        if (p.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (2, Encoding.BigEndianUnicode);
        if (p.Length >= 2 && p[0] != 0 && p[1] == 0) return (0, Encoding.Unicode);
        if (p.Length >= 2 && p[0] == 0 && p[1] != 0) return (0, Encoding.BigEndianUnicode);
        return (0, Encoding.UTF8);
    }

    /// <summary>One packet's walk: the property being read, its values, and details that need several properties.</summary>
    sealed class State(ExtractSink sink)
    {
        readonly StringBuilder _text = new();
        readonly List<(string Value, string? Lang)> _values = [];
        readonly char[] _chunk = new char[2048];
        int _valuesChars;
        Prop _active;
        int _activeDepth = -1, _liDepth = -1, _fontPartDepth = -1;
        string? _liLang, _fontName, _fontFamily;
        bool _fontPartIsName;
        string? _city, _state, _country, _latitude, _longitude, _xRes, _yRes, _resUnit;

        public void Run(XmlReader r)
        {
            bool more = r.Read();
            while (more)
            {
                if (r.Depth > MaxDepth) return;
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        if (IsSkipped(r.NamespaceURI, r.LocalName)) { r.Skip(); more = !r.EOF; continue; }
                        StartElement(r);
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        // One value is capped like a field; the node's value is never read whole.
                        if (_active != Prop.None) AppendCapped(r, _text, ExtractSink.MaxFieldChars, _chunk);
                        break;
                    case XmlNodeType.EndElement:
                        EndElement(r.Depth, r.NamespaceURI, r.LocalName);
                        break;
                }
                more = r.Read();
            }
        }

        void StartElement(XmlReader r)
        {
            int depth = r.Depth;
            bool empty = r.IsEmptyElement;
            if (_active == Prop.None)
            {
                if (r.NamespaceURI == Rdf && r.LocalName == "Description") { DescriptionAttributes(r); return; }
                var prop = PropOf(r.NamespaceURI, r.LocalName);
                if (prop == Prop.None) return;
                _active = prop;
                _activeDepth = depth;
                ClearValues();
                _text.Clear();
                if (empty) EndElement(depth, r.NamespaceURI, r.LocalName);
                return;
            }
            if (_active == Prop.Fonts)
            {
                // Font structures: stFnt:fontName as an element or as an attribute of rdf:li / rdf:Description.
                if (r.NamespaceURI == Rdf && r.LocalName == "li") { _liDepth = depth; _fontName = _fontFamily = null; }
                if (r.MoveToAttribute("fontName", Font)) _fontName ??= r.Value;
                if (r.MoveToAttribute("fontFamily", Font)) _fontFamily ??= r.Value;
                r.MoveToElement();
                if (r.NamespaceURI == Font && r.LocalName is "fontName" or "fontFamily" && !empty)
                {
                    _fontPartDepth = depth;
                    _fontPartIsName = r.LocalName == "fontName";
                    _text.Clear();
                }
                if (empty && depth == _liDepth) EndElement(depth, Rdf, "li");
                return;
            }
            if (r.NamespaceURI == Rdf && r.LocalName == "li")
            {
                _liDepth = depth;
                _liLang = r.GetAttribute("lang", XmlNs);
                _text.Clear();
                if (empty) EndElement(depth, Rdf, "li");
            }
        }

        void EndElement(int depth, string ns, string local)
        {
            if (_active == Prop.None) return;
            if (_active == Prop.Fonts)
            {
                if (depth == _fontPartDepth)
                {
                    string v = _text.ToString().Trim();
                    if (_fontPartIsName) _fontName ??= v; else _fontFamily ??= v;
                    _fontPartDepth = -1;
                    _text.Clear();
                }
                else if (depth == _liDepth && ns == Rdf && local == "li")
                {
                    if ((_fontName is { Length: > 0 } ? _fontName : _fontFamily) is { Length: > 0 } name) AddValue(name, null);
                    _liDepth = -1;
                }
            }
            else if (depth == _liDepth && ns == Rdf && local == "li")
            {
                AddValue(_text.ToString(), _liLang);
                _liDepth = -1;
                _text.Clear();
            }
            if (depth != _activeDepth) return;
            if (_values.Count == 0 && _active != Prop.Fonts && _text.Length > 0) AddValue(_text.ToString(), null);
            var prop = _active;
            _active = Prop.None;
            _activeDepth = _liDepth = _fontPartDepth = -1;
            Emit(prop, _values);
            ClearValues();
            _text.Clear();
        }

        /// <summary>Keeps one more value of a list (bag, seq, alt) while the list's values fit in the keywords' cap; later ones are dropped.</summary>
        void AddValue(string value, string? lang)
        {
            if (_valuesChars >= ExtractSink.MaxKeywordChars) return;
            _values.Add((value, lang));
            _valuesChars += value.Length + 2;
        }

        void ClearValues()
        {
            _values.Clear();
            _valuesChars = 0;
        }

        void DescriptionAttributes(XmlReader r)
        {
            if (!r.MoveToFirstAttribute()) return;
            do
            {
                var prop = PropOf(r.NamespaceURI, r.LocalName);
                if (prop != Prop.None) Emit(prop, [(r.Value, null)]);
            }
            while (r.MoveToNextAttribute());
            r.MoveToElement();
        }

        void Emit(Prop prop, List<(string Value, string? Lang)> values)
        {
            var list = new List<string>(values.Count);
            string? chosen = null;
            foreach (var (value, lang) in values)
            {
                string v = value.Trim();
                if (v.Length == 0) continue;
                list.Add(v);
                if (lang is { } l && l.Equals("x-default", StringComparison.OrdinalIgnoreCase)) chosen ??= v;
            }
            if (list.Count == 0) return;
            string first = chosen ?? list[0];
            var p = sink.Picture;
            switch (prop)
            {
                case Prop.Title: sink.Field(DocField.Title, first); break;
                case Prop.Description: sink.Field(DocField.Comment, first); break;
                case Prop.Subject: foreach (var v in list) sink.Field(DocField.Keywords, v); break;
                case Prop.Creator: sink.Field(DocField.Author, string.Join("; ", list)); break;
                case Prop.CreatorTool: sink.Field(DocField.CreatorTool, first); break;
                case Prop.IccProfile: sink.Field(DocField.ColorProfile, first); break;
                case Prop.Make: sink.Field(DocField.CameraMake, first); break;
                case Prop.Model: sink.Field(DocField.CameraModel, first); break;
                case Prop.Fonts: sink.Field(DocField.Fonts, string.Join("; ", list.Distinct(StringComparer.OrdinalIgnoreCase))); break;
                case Prop.Plates: sink.Field(DocField.Plates, string.Join("; ", list)); break;
                case Prop.City: _city ??= first; break;
                case Prop.State: _state ??= first; break;
                case Prop.Country: _country ??= first; break;
                case Prop.Latitude: _latitude ??= first; break;
                case Prop.Longitude: _longitude ??= first; break;
                case Prop.XResolution: _xRes ??= first; break;
                case Prop.YResolution: _yRes ??= first; break;
                case Prop.ResolutionUnit: _resUnit ??= first; break;
                case Prop.Rating:
                    if (Number(first) is { } stars && Math.Round(stars) is >= 1 and <= 5) p.Rating = (byte)Math.Round(stars);
                    break;
                case Prop.ColorMode:
                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)) p.ColorMode = PhotoshopMode(mode);
                    break;
                case Prop.DateTaken: p.DateTakenUtc = Date(first); break;
                case Prop.Orientation:
                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) && o is >= 1 and <= 8) p.Orientation = (byte)o;
                    break;
                case Prop.Iso:
                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iso)) p.Iso = iso;
                    break;
                case Prop.FNumber: p.FNumber = (float)(Number(first) ?? 0); break;
                case Prop.Exposure: p.ExposureSeconds = (float)(Number(first) ?? 0); break;
                case Prop.Focal: p.FocalLengthMm = (float)(Number(first) ?? 0); break;
                case Prop.ColorSpace:
                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cs)) p.ExifColorSpace = cs == 1 ? (byte)1 : cs == 65535 ? (byte)2 : (byte)0;
                    break;
                case Prop.NPages:
                    if (sink.Pages == 0 && int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages) && pages > 0) sink.Pages = pages;
                    break;
            }
        }

        /// <summary>Details made of several properties, kept even when the XML broke off later.</summary>
        public void Finish()
        {
            var parts = new[] { _city, _state, _country }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray();
            if (parts.Length > 0) sink.Field(DocField.Location, string.Join(", ", parts));
            if (Coordinate(_latitude, 'N', 'S') is { } lat && Coordinate(_longitude, 'E', 'W') is { } lon) sink.Picture.SetGps(lat, lon);
            double scale = _resUnit?.Trim() switch { null or "" or "2" => 1, "3" => 2.54, _ => 0 };
            if (scale > 0)
            {
                if (Number(_xRes) is { } x && x > 0 && x * scale < 100_000) sink.Picture.DpiX = (int)Math.Round(x * scale);
                if (Number(_yRes) is { } y && y > 0 && y * scale < 100_000) sink.Picture.DpiY = (int)Math.Round(y * scale);
            }
        }
    }

    static ColorMode PhotoshopMode(int mode) => mode switch
    {
        0 => ColorMode.Bitmap, 1 => ColorMode.Gray, 2 => ColorMode.Indexed, 3 => ColorMode.Rgb, 4 => ColorMode.Cmyk,
        7 => ColorMode.Multichannel, 8 => ColorMode.Duotone, 9 => ColorMode.Lab, _ => ColorMode.Unknown,
    };

    /// <summary>A number or a rational ("28/10"); null when unparsable or the denominator is 0.</summary>
    internal static double? Number(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        int slash = s.IndexOf('/');
        if (slash < 0) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        if (!double.TryParse(s.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return null;
        if (!double.TryParse(s.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double b) || b == 0) return null;
        double r = a / b;
        return double.IsFinite(r) ? r : null;
    }

    /// <summary>An XMP date as FILETIME UTC: its own offset when given, else the PC's time zone (like dm:); 0 when unparsable.</summary>
    internal static long Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        // Some writers put EXIF's "2023:07:14 15:30:12" in XMP.
        if (s.Length >= 10 && s[4] == ':' && s[7] == ':') s = s[..4] + "-" + s[5..7] + "-" + s[8..];
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var dt)) return 0;
        DateTime utc;
        if (dt.Kind == DateTimeKind.Unspecified)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
            long ticks = dt.Ticks - offset.Ticks;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return 0;
            utc = new DateTime(ticks, DateTimeKind.Utc);
        }
        else utc = dt.ToUniversalTime();
        return utc.Year is >= 1601 and <= 9999 ? utc.ToFileTimeUtc() : 0;
    }

    /// <summary>An XMP GPS coordinate ("47,30.123N" or "47,30,15N") in degrees; null without a reference letter or when unparsable.</summary>
    internal static double? Coordinate(string? s, char positive, char negative)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        char reference = char.ToUpperInvariant(s[^1]);
        if (reference != positive && reference != negative) return null;
        var parts = s[..^1].Split(',');
        if (parts.Length is < 1 or > 3) return null;
        double value = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < 0) return null;
            value += v / scale;
            scale *= 60;
        }
        return reference == negative ? -value : value;
    }
}
