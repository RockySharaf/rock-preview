using System.Globalization;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Adobe Glyph List subset (about 1,000 names: ASCII, Latin-1, Latin Extended-A by composition, Greek, Cyrillic afii
/// names, punctuation and symbols), plus uniXXXX (several groups), uXXXX[XX], suffixes (a.sc, one.oldstyle) and
/// ligature names (f_f_i). Ligature and small-capital glyphs map to plain letters, so their words can be found.
/// </summary>
internal static class PdfGlyphNames
{
    static readonly Dictionary<string, string> s_names = Build();

    /// <summary>The text a glyph name stands for, or null when the name is unknown (subset names such as g12 or cid45).</summary>
    public static string? ToUnicode(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (s_names.TryGetValue(name, out var hit)) return hit;
        // a.sc, one.oldstyle, A.swash: the part before the first dot.
        int dot = name.IndexOf('.');
        if (dot == 0) return null;
        if (dot > 0) name = name[..dot];
        if (s_names.TryGetValue(name, out hit)) return hit;
        if (name.Contains('_'))
        {
            var sb = new StringBuilder();
            foreach (var part in name.Split('_'))
            {
                var p = Single(part);
                if (p == null) return null;
                sb.Append(p);
            }
            return sb.ToString();
        }
        return Single(name);
    }

    static string? Single(string name)
    {
        if (s_names.TryGetValue(name, out var hit)) return hit;
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v)) return null;
                if (v is >= 0xD800 and <= 0xDFFF) return null;
                sb.Append((char)v);
            }
            return sb.ToString();
        }
        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
            && cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            return char.ConvertFromUtf32(cp);
        return null;
    }

    static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(1400, StringComparer.Ordinal);
        // Names, one per code point of chars, in order.
        void Add(string names, string chars)
        {
            var list = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            foreach (var rune in chars.EnumerateRunes())
            {
                if (i >= list.Length) break;
                d.TryAdd(list[i++], rune.ToString());
            }
        }

        // ASCII.
        Add("space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus comma hyphen period slash",
            " !\"#$%&'()*+,-./");
        Add("zero one two three four five six seven eight nine colon semicolon less equal greater question at", "0123456789:;<=>?@");
        for (char c = 'A'; c <= 'Z'; c++) { d[c.ToString()] = c.ToString(); d[char.ToLowerInvariant(c).ToString()] = char.ToLowerInvariant(c).ToString(); }
        Add("bracketleft backslash bracketright asciicircum underscore grave braceleft bar braceright asciitilde", "[\\]^_`{|}~");
        Add("quoteleft quoteright quotesinglbase quotedblleft quotedblright quotedblbase", "‘’‚“”„");

        // Latin-1 and Windows-1252 extras.
        Add("exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine guillemotleft logicalnot registered macron degree plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters questiondown",
            "¡¢£¤¥¦§¨©ª«¬®¯°±²³´µ¶·¸¹º»¼½¾¿");
        Add("AE Eth multiply Oslash Thorn germandbls ae eth divide oslash thorn", "ÆÐ×ØÞßæð÷øþ");
        Add("Euro florin ellipsis dagger daggerdbl circumflex perthousand Scaron guilsinglleft OE Zcaron scaron guilsinglright oe zcaron Ydieresis",
            "€ƒ…†‡ˆ‰Š‹ŒŽš›œžŸ");
        Add("bullet endash emdash tilde trademark nbspace nonbreakingspace sfthyphen softhyphen figuredash minus fraction",
            "•–—˜™  ­­‒−⁄");
        Add("breve dotaccent ring ogonek caron hungarumlaut dotlessi dotlessj Lslash lslash Dcroat dcroat Hbar hbar Tbar tbar Eng eng IJ ij kgreenlandic napostrophe Ldot ldot longs",
            "˘˙˚˛ˇ˝ıȷŁłĐđĦħŦŧŊŋĲĳĸŉĿŀſ");
        Add("Idotaccent Gcommaaccent gcommaaccent Kcommaaccent kcommaaccent Lcommaaccent lcommaaccent Ncommaaccent ncommaaccent Rcommaaccent rcommaaccent Tcommaaccent tcommaaccent Scommaaccent scommaaccent",
            "İĢģĶķĻļŅņŖŗŢţȘș");
        Add("Schwa schwa Ohorn ohorn Uhorn uhorn florin ff fi fl ffi ffl st", "ƏəƠơƯưƒ\0\0\0\0\0\0");
        d["ff"] = "ff"; d["fi"] = "fi"; d["fl"] = "fl"; d["ffi"] = "ffi"; d["ffl"] = "ffl"; d["st"] = "st"; d["longs_t"] = "st";
        d["f_f"] = "ff"; d["f_i"] = "fi"; d["f_l"] = "fl"; d["f_f_i"] = "ffi"; d["f_f_l"] = "ffl";

        // Accented letters by composition: Aacute, ccaron, uogonek …
        (string Name, char Mark)[] accents =
        [
            ("acute", '́'), ("grave", '̀'), ("circumflex", '̂'), ("tilde", '̃'), ("dieresis", '̈'),
            ("ring", '̊'), ("cedilla", '̧'), ("caron", '̌'), ("breve", '̆'), ("macron", '̄'),
            ("ogonek", '̨'), ("dotaccent", '̇'), ("hungarumlaut", '̋'), ("commaaccent", '̦'),
            ("dotbelow", '̣'), ("hookabove", '̉'),
        ];
        for (char c = 'A'; c <= 'z'; c++)
        {
            if (c is > 'Z' and < 'a') continue;
            foreach (var (name, mark) in accents)
            {
                string composed = (c.ToString() + mark).Normalize(NormalizationForm.FormC);
                if (composed.Length == 1) d.TryAdd(c + name, composed);
            }
        }

        // Greek.
        Add("Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota Kappa Lambda Mu Nu Xi Omicron Pi Rho Sigma Tau Upsilon Phi Chi Psi Omega",
            "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ");
        Add("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega",
            "αβγδεζηθικλμνξοπρστυφχψω");
        Add("sigma1 sigmafinal theta1 phi1 omega1 Upsilon1 Alphatonos Epsilontonos Etatonos Iotatonos Omicrontonos Upsilontonos Omegatonos alphatonos epsilontonos etatonos iotatonos omicrontonos upsilontonos omegatonos Iotadieresis Upsilondieresis iotadieresis upsilondieresis tonos dieresistonos",
            "ςςϑϕϖϒΆΈΉΊΌΎΏάέήίόύώΪΫϊϋ΄΅");
        d["Ohm"] = "Ω"; d["mu1"] = "µ"; d["Delta1"] = "∆"; d["Omega1"] = "Ω";

        // Cyrillic afii names (AGL).
        for (int i = 0; i < 6; i++) { d["afii" + (10017 + i)] = ((char)(0x0410 + i)).ToString(); d["afii" + (10065 + i)] = ((char)(0x0430 + i)).ToString(); }
        d["afii10023"] = "Ё"; d["afii10071"] = "ё";
        for (int i = 0; i < 26; i++) { d["afii" + (10024 + i)] = ((char)(0x0416 + i)).ToString(); d["afii" + (10072 + i)] = ((char)(0x0436 + i)).ToString(); }
        Add("afii10051 afii10052 afii10053 afii10054 afii10055 afii10056 afii10057 afii10058 afii10059 afii10060 afii10061 afii10062 afii10145",
            "ЂЃЄЅІЇЈЉЊЋЌЎЏ");
        Add("afii10099 afii10100 afii10101 afii10102 afii10103 afii10104 afii10105 afii10106 afii10107 afii10108 afii10109 afii10110 afii10193 afii10050 afii10098",
            "ђѓєѕіїјљњћќўџҐґ");
        d["afii61352"] = "№"; d["afii00208"] = "―";

        // Symbols and punctuation.
        Add("cent1 sterling1 angleleft angleright arrowleft arrowup arrowright arrowdown arrowboth arrowupdn arrowdblleft arrowdblup arrowdblright arrowdbldown arrowdblboth",
            "¢£〈〉←↑→↓↔↕⇐⇑⇒⇓⇔");
        Add("infinity notequal lessequal greaterequal partialdiff summation product integral radical approxequal equivalence proportional similar element notelement intersection union propersubset propersuperset reflexsubset reflexsuperset emptyset gradient therefore perpendicular dotmath logicaland logicalor universal existential suchthat angle lozenge",
            "∞≠≤≥∂∑∏∫√≈≡∝∼∈∉∩∪⊂⊃⊆⊇∅∇∴⊥⋅∧∨∀∃∋∠◊");
        Add("prime second minute degree1 club diamond heart spade checkmark circle filledbox filledrect triagup triagdn triagrt triaglf openbullet invbullet",
            "′″′°♣♦♥♠✓○■▬▲▼►◄◦◘");
        Add("onethird twothirds oneeighth threeeighths fiveeighths seveneighths zerosuperior foursuperior fivesuperior sixsuperior sevensuperior eightsuperior ninesuperior zeroinferior oneinferior twoinferior threeinferior fourinferior fiveinferior sixinferior seveninferior eightinferior nineinferior",
            "⅓⅔⅛⅜⅝⅞⁰⁴⁵⁶⁷⁸⁹₀₁₂₃₄₅₆₇₈₉");
        Add("franc lira peseta dong rupiah Ifraktur Rfraktur weierstrass aleph estimated afii61289 servicemark copyrightsans registersans trademarksans",
            "₣₤₧₫₹ℑℜ℘ℵ℮ℓ℠©®™");
        Add("exclamdbl underscoredbl quotereversed colonmonetary twodotenleader onedotenleader enspace emspace thinspace hairspace zerowidthspace zerowidthjoiner zerowidthnonjoiner",
            "‼‗‛₡‥․    ​‍‌");
        Add("periodcentered1 middot anoteleia questiongreek overline", "··;;‾");
        d["controlLF"] = "\n"; d["controlCR"] = "\n"; d["tab"] = "\t";

        // Small capitals and old-style figures from expert sets: plain letters and digits.
        for (char c = 'A'; c <= 'Z'; c++) d.TryAdd(c + "small", char.ToLowerInvariant(c).ToString());
        string[] digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        for (int i = 0; i < 10; i++) { d.TryAdd(digits[i] + "oldstyle", i.ToString(CultureInfo.InvariantCulture)); d.TryAdd(digits[i] + "fitted", i.ToString(CultureInfo.InvariantCulture)); }
        return d;
    }
}
