using System.Text;
using Anything.Core.Content;
using Anything.Core.Search;

namespace Anything.Tests.Content;

public class ContentTokenizerTests
{
    // Letters, digits and punctuation of many scripts, without full-width ASCII and without the dropped mark blocks
    // (combining diacritics, Hebrew points, Arabic harakat): there the content fold must equal the name search's fold.
    static readonly string[] ParityPool = Elements(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ,;!?()-_.'@:/\\" +
        "àáâãäåæçèéêëìíîïñòóôõöøùúûüýÿÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖØÙÚÛÜÝßĀāĂăĄąĆćČčĎďĒēĚěĞğĪīŁłŃńŇňŌōŐőŒœŘřŚśŠšŞşŢţŤťŪūŮůŰűŸŹźŻżŽž" +
        "ΑΒΓΔΕΖΗΘαβγδεζηθάέήίόύώ" + "АБВГДЕЁЖЗИЙабвгдеёжзий" + "אבגדהוזחטי" + "ابتثجحخدذر" +
        "กขคงจฉชซัิีึืุู่้๊๋" + "कखगघङचछजझञािीुूेैोौ" + "東京都大阪日本語中文한국어ひらがなカタカナ") .Concat(["𠀀", "𠀁", "😀"]).ToArray();

    // Plain-script letters only (no marks, no joiners, no bigram scripts): word starts must agree with Needle.
    static readonly string[] WordPool = Elements("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789àéîõüÀÉÎÕÜçÇñÑΑΒΓαβγАБВабвёЁ");
    static readonly string[] Separators = [" ", ", ", "; ", "!", "?", " (", ") ", "  "];

    static string[] Elements(string s)
    {
        var list = new List<string>();
        for (int i = 0; i < s.Length; i++)
            list.Add(char.IsHighSurrogate(s[i]) ? s.Substring(i++, 2) : s[i].ToString());
        return [.. list];
    }

    static string Random(Random rnd, string[] pool, int min, int max)
    {
        var sb = new StringBuilder();
        int n = rnd.Next(min, max + 1);
        for (int i = 0; i < n; i++) sb.Append(pool[rnd.Next(pool.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Fold_matches_the_name_search_fold_on_10000_random_strings()
    {
        var rnd = new Random(20261021);
        for (int i = 0; i < 10_000; i++)
        {
            string s = Random(rnd, ParityPool, 0, 24);
            Assert.True(new Needle(s, false, false).Text == ContentTokenizer.Fold(s), $"Fold differs for [{s}]");
        }
    }

    [Fact]
    public void Word_starts_match_Needle_at_word_starts_on_random_texts()
    {
        var rnd = new Random(42);
        for (int i = 0; i < 3000; i++)
        {
            var words = Enumerable.Range(0, rnd.Next(1, 8)).Select(_ => Random(rnd, WordPool, 1, 12)).ToArray();
            var sb = new StringBuilder();
            for (int w = 0; w < words.Length; w++) sb.Append(w > 0 ? Separators[rnd.Next(Separators.Length)] : "").Append(words[w]);
            string text = sb.ToString();
            string pick = words[rnd.Next(words.Length)];
            string query = rnd.Next(3) == 0 || pick.Length < 2 ? Random(rnd, WordPool, 2, 4) : pick[..rnd.Next(2, pick.Length + 1)];

            var needle = new Needle(query, false, false);
            bool byNeedle = false;
            for (int at = needle.IndexOf(text); at >= 0; at = needle.IndexOf(text, at + 1))
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) { byNeedle = true; break; }
            string folded = ContentTokenizer.Fold(query);
            bool byTokens = ListSink.Of(text).Folded().Any(t => t.StartsWith(folded, StringComparison.Ordinal));
            Assert.True(byNeedle == byTokens, $"[{query}] in [{text}]: Needle {byNeedle}, tokens {byTokens}");
        }
    }

    [Fact]
    public void Deliberate_differences_from_the_name_fold()
    {
        // A combining diacritic that can't compose is dropped; name search keeps it.
        Assert.Equal("x", ContentTokenizer.Fold("x́"));
        Assert.Equal("x́", new Needle("x́", false, false).Text);
        // Full-width ASCII folds to ASCII; name search only lower-cases it.
        Assert.Equal("abc12", ContentTokenizer.Fold("ＡＢＣ１２"));
        Assert.Equal("ａｂｃ１２", new Needle("ＡＢＣ１２", false, false).Text);
        // Hebrew points and Arabic harakat are dropped.
        Assert.Equal("שלום", ContentTokenizer.Fold("שָׁלוֹם"));
        Assert.Equal("كتب", ContentTokenizer.Fold("كَتَبَ"));
        // Indic vowel signs are word characters: one word, where name search's word boundaries split it.
        Assert.Equal(["किताब"], ListSink.Of("किताब").Folded());
        Assert.False(char.IsLetterOrDigit('ि'));
        // Invisible format characters are left out of the word; name search keeps them.
        Assert.Equal("abcd", ContentTokenizer.Fold("ab­cd"));
        Assert.Equal("ab­cd", new Needle("ab­cd", false, false).Text);
    }

    [Fact]
    public void Invisible_format_characters_never_split_words()
    {
        Assert.Equal(["donaudampfschiff"], ListSink.Of("Donau­dampf­schiff").Folded());
        Assert.Equal((0, 18), (ListSink.Of("Donau­dampf­schiff").Tokens[0].Start, ListSink.Of("Donau­dampf­schiff").Tokens[0].Length));
        Assert.Equal(["coop"], ListSink.Of("co‍op").Folded());
        Assert.Equal(["coop"], ListSink.Of("co‌op").Folded());
        Assert.Equal(["coop"], ListSink.Of("co⁠op").Folded());
        Assert.Equal(["coop"], ListSink.Of("co﻿op").Folded());
        Assert.Equal(["ab"], ListSink.Of("­ab­").Folded());
        // The zero-width space still separates words (Thai and Khmer text use it between words).
        Assert.Equal(["ab", "cd"], ListSink.Of("ab​cd").Folded());
        Assert.Equal(["abcd"], ContentTokenizer.Analyze("ab­cd", true).Tokens);

        // Found under every match flag, and the part after the soft hyphen is no word start.
        Assert.Equal((0, 5), Find("ab­cd", "abcd", prefix: false));
        Assert.Equal((0, 5), Find("ab­cd", "abcd", prefix: false, matchCase: true, diacritics: true));
        Assert.Equal((0, 4), Find("abcd", "ab­cd", prefix: false, diacritics: true));
        Assert.Equal((-1, 0), Find("ab­cd", "cd"));
    }

    [Fact]
    public void Text_has_words_only_with_a_letter_digit_or_kept_mark()
    {
        Assert.False(ContentTokenizer.HasWords(""));
        Assert.False(ContentTokenizer.HasWords("\f\n \t\f"));
        Assert.False(ContentTokenizer.HasWords("— 😀 !!"));
        Assert.False(ContentTokenizer.HasWords("́­"));
        Assert.True(ContentTokenizer.HasWords("\f\fx"));
        Assert.True(ContentTokenizer.HasWords("東"));
        Assert.True(ContentTokenizer.HasWords("𠀀"));
    }

    [Fact]
    public void Nfd_marks_fold_into_the_letter()
    {
        Assert.Equal("cafe", ContentTokenizer.Fold("CAFÉ"));
        var sink = ListSink.Of("café crème");
        Assert.Equal(["cafe", "creme"], sink.Folded());
        Assert.Equal((0, 5), (sink.Tokens[0].Start, sink.Tokens[0].Length));
    }

    [Fact]
    public void Thai_marks_keep_words_apart()
    {
        // ไม้ (wood) and ไม่ (not) differ only by a tone mark.
        Assert.Equal(["ไม้", "ม้"], ListSink.Of("ไม้").Folded());
        Assert.Equal(["ไม่", "ม่"], ListSink.Of("ไม่").Folded());
        Assert.Equal(["สวั", "วัส", "สดี", "ดี"], ListSink.Of("สวัสดี").Folded());
    }

    [Fact]
    public void Full_width_letters_digits_and_joiners_fold_to_ascii()
    {
        Assert.Equal(["invoice", "2024"], ListSink.Of("ＩＮＶＯＩＣＥ ２０２４").Folded());
        Assert.Equal(["inv", "2024", "inv2024"], ListSink.Of("ＩＮＶ－２０２４").Folded());
    }

    [Fact]
    public void Codes_give_parts_and_the_compound()
    {
        var sink = ListSink.Of("Ref INV-2024-001.");
        Assert.Equal(
            [("ref", 0, 3, TokenKind.Word), ("inv", 4, 3, TokenKind.Part), ("2024", 8, 4, TokenKind.Part), ("001", 13, 3, TokenKind.Part),
             ("inv2024001", 4, 12, TokenKind.Compound)],
            sink.Tokens);
        Assert.Equal(["don", "dont"], ListSink.Of("don't").Folded());
        Assert.Equal(["rock", "roll", "rocknroll"], ListSink.Of("rock’n’roll").Folded());
        Assert.Equal(["john", "smith", "acme", "com", "johnsmithacmecom"], ListSink.Of("john.smith@acme.com").Folded());
        Assert.Equal(["14", "314"], ListSink.Of("3.14").Folded());
    }

    [Theory]
    [InlineData('-'), InlineData('_'), InlineData('.'), InlineData('/'), InlineData('\\'), InlineData('\''), InlineData('’'), InlineData('@'), InlineData(':')]
    public void Every_joiner_makes_a_code(char joiner)
        => Assert.Equal(["ab", "cd", "abcd"], ListSink.Of($"ab{joiner}cd").Folded());

    [Fact]
    public void Only_one_joiner_between_word_characters_makes_a_code()
    {
        Assert.Equal(["ab", "cd"], ListSink.Of("ab--cd").Folded());
        Assert.Equal(["ab", "cd"], ListSink.Of("ab - cd").Folded());
        Assert.Equal(["ab", "cd"], ListSink.Of("ab, cd").Folded());
        Assert.Equal(["end", "next"], ListSink.Of("end. Next").Folded());
    }

    [Fact]
    public void Compounds_need_2_to_8_parts_and_64_characters_at_most()
    {
        Assert.Contains("a1b2c3d4e5f6g7h8", ListSink.Of("a1.b2.c3.d4.e5.f6.g7.h8").Folded());
        var nine = ListSink.Of("a1.b2.c3.d4.e5.f6.g7.h8.i9");
        Assert.Equal(9, nine.Tokens.Count);
        Assert.All(nine.Tokens, t => Assert.Equal(TokenKind.Part, t.Kind));
        string a = new('a', 40), b = new('b', 30);
        Assert.Equal([a, b], ListSink.Of($"{a}-{b}").Folded());
    }

    [Fact]
    public void Words_are_2_to_64_characters()
    {
        Assert.Equal([new string('a', 64)], ListSink.Of(new string('a', 64)).Folded());
        Assert.Empty(ListSink.Of(new string('a', 65)).Tokens);
        Assert.Equal(["ok"], ListSink.Of("x ok y").Folded());
        Assert.Empty(ListSink.Of("_ - ... !!! 😀 \u0001").Tokens);
    }

    [Fact]
    public void Cjk_runs_give_bigrams_and_their_last_character()
    {
        var tokyo = ListSink.Of("東京都");
        Assert.Equal([("東京", 0, 2, TokenKind.Bigram), ("京都", 1, 2, TokenKind.Bigram), ("都", 2, 1, TokenKind.Bigram)], tokyo.Tokens);
        Assert.Equal(["関東", "東"], ListSink.Of("関東").Folded());
        Assert.Equal(["東"], ListSink.Of("東").Folded());
        Assert.Equal(["한국", "국어", "어"], ListSink.Of("한국어").Folded());
    }

    [Fact]
    public void Runs_split_where_a_bigram_script_meets_other_letters_or_digits()
    {
        Assert.Equal(["2024", "年", "月", "15", "日"], ListSink.Of("2024年3月15日").Folded());
        Assert.Equal(["iphone15", "を買", "買っ", "った", "た"], ListSink.Of("iPhone15を買った").Folded());
        Assert.Equal(["第", "2024", "号"], ListSink.Of("第２０２４号").Folded());
        Assert.Equal(["inv", "東京", "京"], ListSink.Of("INV-東京").Folded());
    }

    [Fact]
    public void Supplementary_letters_pass_through()
    {
        Assert.Equal(["𠀀𠀁", "𠀁"], ListSink.Of("𠀀𠀁").Folded());
        Assert.Equal(["ab", "cd"], ListSink.Of("ab😀cd").Folded());
    }

    [Fact]
    public void Fields_give_plain_and_field_scoped_tokens()
    {
        var sink = new ListSink();
        ContentTokenizer.TokenizeField(DocField.Keywords, "Sunset beach", sink);
        string p = ContentTokenizer.FieldPrefix(DocField.Keywords);
        Assert.Equal("\u0001D", p);
        Assert.Equal(["sunset", p + "sunset", "beach", p + "beach"], sink.Folded());
        Assert.Equal(TokenKind.Field, sink.Tokens[1].Kind);
        Assert.Equal(sink.Tokens[0].Start, sink.Tokens[1].Start);
        Assert.Equal("\u0001A", ContentTokenizer.FieldPrefix(DocField.Title));
        Assert.Equal("\u0001N", ContentTokenizer.FieldPrefix(DocField.Recipients));
    }

    [Fact]
    public void Unknown_versions_are_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ContentTokenizer.Tokenize("a", new ListSink(), version: 99));

    [Fact]
    public void Analyze_codes_phrases_and_bigram_words()
    {
        var code = ContentTokenizer.Analyze("INV-2024-001", lastIsPrefix: true);
        Assert.Equal(["inv", "2024", "001"], code.Tokens);
        Assert.Equal("inv2024001", code.Compound);
        Assert.True(code.NeedsSequence);
        Assert.True(code.LastIsPrefix);
        Assert.Equal([TokenKind.Part, TokenKind.Part, TokenKind.Part], code.Kinds);
        Assert.Equal("inv-2024-001", code.Folded);

        var phrase = ContentTokenizer.Analyze("annual report", false);
        Assert.Equal(["annual", "report"], phrase.Tokens);
        Assert.Null(phrase.Compound);
        Assert.True(phrase.NeedsSequence);

        var tokyo = ContentTokenizer.Analyze("東京都", true);
        Assert.Equal(["東京", "京都"], tokyo.Tokens);
        Assert.Equal([TokenKind.Bigram, TokenKind.Bigram], tokyo.Kinds);
        Assert.True(tokyo.NeedsSequence);
        Assert.Null(tokyo.Compound);

        var east = ContentTokenizer.Analyze("東", true);
        Assert.Equal(["東"], east.Tokens);
        Assert.False(east.NeedsSequence);

        var cafe = ContentTokenizer.Analyze("Café", true);
        Assert.Equal("Café", cafe.Text);
        Assert.Equal(["cafe"], cafe.Tokens);
        Assert.Equal([TokenKind.Word], cafe.Kinds);

        var shortTail = ContentTokenizer.Analyze("report.p", true);
        Assert.Equal(["report", "p"], shortTail.Tokens);
        Assert.Equal("reportp", shortTail.Compound);

        var mixed = ContentTokenizer.Analyze("iPhone15を", true);
        Assert.Equal(["iphone15", "を"], mixed.Tokens);
        Assert.Null(mixed.Compound);

        Assert.Empty(ContentTokenizer.Analyze("--", true).Tokens);
    }

    static (int, int) Find(string text, string word, bool prefix = true, bool matchCase = false, bool diacritics = false, bool wholeWord = false, int from = 0)
        => ContentTokenizer.FindSequence(text, ContentTokenizer.Analyze(word, prefix), matchCase, diacritics, wholeWord, from);

    [Fact]
    public void FindSequence_case_and_diacritics()
    {
        Assert.Equal((0, 7), Find("Invoice INVOICE invoice", "INVOICE", prefix: false));
        Assert.Equal((8, 7), Find("Invoice INVOICE invoice", "INVOICE", prefix: false, matchCase: true));
        Assert.Equal((16, 7), Find("Invoice INVOICE invoice", "invoice", prefix: false, matchCase: true));
        Assert.Equal((0, 6), Find("resume résumé", "résumé", prefix: false));
        Assert.Equal((7, 6), Find("resume résumé", "résumé", prefix: false, diacritics: true));
        Assert.Equal((-1, 0), Find("resume", "résumé", prefix: false, diacritics: true));
        Assert.Equal((0, 5), Find("café au lait", "café", prefix: false));
    }

    [Fact]
    public void FindSequence_prefix_whole_word_and_word_starts()
    {
        Assert.Equal((0, 7), Find("invoices invoice", "invoice"));
        Assert.Equal((9, 7), Find("invoices invoice", "invoice", wholeWord: true));
        Assert.Equal((9, 7), Find("invoices invoice", "invoice", prefix: false));
        Assert.Equal((10, 7), Find("reinvoice invoice", "invoice"));
        Assert.Equal((-1, 0), Find("reinvoice", "invoice"));
    }

    [Fact]
    public void FindSequence_phrases_need_only_separators_between()
    {
        Assert.Equal((4, 14), Find("the annual, report", "annual report", prefix: false));
        Assert.Equal((0, 13), Find("annual reports", "annual report"));
        Assert.Equal((-1, 0), Find("annual reports", "annual report", prefix: false));
        Assert.Equal((-1, 0), Find("annualreport", "annual report"));
        Assert.Equal((0, 13), Find("annual\nreport", "annual report"));
    }

    [Fact]
    public void FindSequence_codes_ignore_separators()
    {
        Assert.Equal((4, 12), Find("Ref INV 2024 001", "INV-2024-001", prefix: false));
        Assert.Equal((0, 10), Find("INV2024001", "INV-2024-001", prefix: false));
        Assert.Equal((0, 12), Find("inv-2024-001", "inv2024001", prefix: false));
        Assert.Equal((4, 8), Find("INV-2024-001", "2024001", prefix: false));
        Assert.Equal((0, 8), Find("INV-2024-001", "INV-2024"));
        Assert.Equal((-1, 0), Find("INV 2024001", "inv2024001", prefix: false));
    }

    [Fact]
    public void FindSequence_bigram_words_match_inside_runs()
    {
        Assert.Equal((3, 2), Find("私は東京都に住む", "京都"));
        Assert.Equal((2, 3), Find("私は東京都に住む", "東京都", prefix: false));
        Assert.Equal((-1, 0), Find("私は東京都に住む", "京都", wholeWord: true));
        Assert.Equal((1, 1), Find("関東", "東"));
        Assert.Equal((0, 9), Find("iPhone15を買った", "iphone15を"));
        Assert.Equal((0, 9), Find("iPhone15を買った", "iPhone15 を"));
        Assert.Equal((0, 4), Find("2024年", "2024", prefix: false));
    }

    [Theory]
    [InlineData("สวัสดี", "สวัสด", 6)]   // Thai: the query stops before the last vowel
    [InlineData("ไม้", "ไม", 3)]         // Thai: before the tone mark
    [InlineData("किताब", "कित", 4)]      // Hindi: before a vowel sign
    [InlineData("नमस्ते", "नमस", 4)]     // Hindi: before the virama
    [InlineData("ভাষা", "ভাষ", 4)]       // Bengali
    [InlineData("தமிழ்", "தமிழ", 5)]     // Tamil
    [InlineData("ខ្មែរ", "ខ្ម", 4)]      // Khmer: before a vowel sign
    public void FindSequence_prefixes_may_stop_before_a_units_marks(string text, string query, int length)
    {
        Assert.Equal((0, length), Find(text, query));
        Assert.Equal((-1, 0), Find(text, query, prefix: false));
        Assert.Equal((-1, 0), Find(text, query, wholeWord: true));
    }

    [Fact]
    public void FindSequence_prefix_with_diacritics_on_may_stop_before_a_combining_mark()
    {
        Assert.Equal((0, 4), Find("abx́", "abx", diacritics: true));
        Assert.Equal((-1, 0), Find("abx́", "abx", prefix: false, diacritics: true));
        Assert.Equal((0, 4), Find("abx́", "abx", prefix: false));
    }

    [Fact]
    public void FindSequence_sees_the_index_word_boundaries_after_dropped_marks()
    {
        // A separator, a dropped mark, then a kept Thai mark: the index starts a unit at the Thai mark (a bigram-script
        // unit), so the Latin letters after it start a word of their own.
        Assert.Contains("ab", ListSink.Of("-่́ab").Folded());
        Assert.Equal((3, 2), Find("-่́ab", "ab"));
        Assert.Contains("zb", ListSink.Of("-ָ่ZＢ").Folded());
        Assert.Equal((3, 2), Find("-ָ่ZＢ", "zb"));
    }

    [Fact]
    public void Every_token_is_found_where_it_stands_on_random_texts_with_marks()
    {
        // Each emitted word token, searched as a whole word from its own start, must be found there.
        string[] pool = Elements("ab Zx1-.ָ่́ัก東­िक ");
        var rnd = new Random(2026);
        for (int i = 0; i < 20_000; i++)
        {
            string text = Random(rnd, pool, 1, 10);
            foreach (var t in ListSink.Of(text).Tokens)
            {
                if (t.Kind is not (TokenKind.Word or TokenKind.Part)) continue;
                var found = ContentTokenizer.FindSequence(text, ContentTokenizer.Analyze(text.Substring(t.Start, t.Length), true), false, false, false, t.Start);
                Assert.True(found.Start == t.Start, $"[{t.Folded}] at {t.Start} in [{text}]: found {found}");
            }
        }
    }

    [Fact]
    public void FindSequence_from_finds_every_occurrence()
    {
        const string text = "an invoice, then INVOICE-2 and invoices";
        var found = new List<int>();
        var word = ContentTokenizer.Analyze("invoice", true);
        for (var (at, len) = ContentTokenizer.FindSequence(text, word, false, false, false); at >= 0;
             (at, len) = ContentTokenizer.FindSequence(text, word, false, false, false, at + len))
            found.Add(at);
        Assert.Equal([3, 17, 31], found);
        // Starting inside a word never treats its rest as a word start.
        Assert.Equal((-1, 0), Find("invoice", "voice", from: 2));
    }
}
