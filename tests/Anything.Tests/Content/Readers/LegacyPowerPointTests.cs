using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Anything.Core.Content.Extract.Office;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// The PowerPoint 97-2003 reader: presentations written record by record here ([MS-PPT] record headers, the outline,
/// slide drawings, the persist directory and the Current User stream), and hostile variants of them.
/// </summary>
public class LegacyPowerPointTests
{
    // ------------------------------------------------------------------ record writer

    const int Document = 0x03E8, DocumentAtom = 0x03E9, Slide = 0x03EE, SlideAtom = 0x03EF, SlidePersistAtom = 0x03F3,
        PPDrawing = 0x040C, SlideListWithText = 0x0FF0, TextHeaderAtom = 0x0F9F, TextCharsAtom = 0x0FA0,
        TextBytesAtom = 0x0FA8, OutlineTextRefAtom = 0x0F9E, StyleTextPropAtom = 0x0FA1, UserEditAtom = 0x0FF5,
        CurrentUserAtom = 0x0FF6, PersistDirectoryAtom = 0x1772, CryptSession10Container = 0x2F14,
        DgContainer = 0xF002, SpgrContainer = 0xF003, SpContainer = 0xF004, Sp = 0xF00A, ClientTextbox = 0xF00D;

    static byte[] Record(int type, int version, int instance, byte[] data)
    {
        var r = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)(version | instance << 4));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)data.Length);
        data.CopyTo(r, 8);
        return r;
    }

    static byte[] Atom(int type, byte[] data, int instance = 0) => Record(type, 0, instance, data);
    static byte[] Container(int type, int instance, params byte[][] children) => Record(type, 0xF, instance, children.SelectMany(c => c).ToArray());
    static byte[] U32s(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    static byte[] Chars(string s) => Atom(TextCharsAtom, Encoding.Unicode.GetBytes(s));
    static byte[] Bytes(string s) => Atom(TextBytesAtom, Encoding.Latin1.GetBytes(s));
    static byte[] Header(uint textType) => Atom(TextHeaderAtom, U32s(textType));
    static byte[] Persist(uint persistId, uint slideId) => Atom(SlidePersistAtom, U32s(persistId, 0, 1, slideId, 0));

    /// <summary>A slide list: instance 0 slides, 1 masters, 2 notes.</summary>
    static byte[] SlideList(int instance, params byte[][] children) => Container(SlideListWithText, instance, children);

    static byte[] DocumentContainer(params byte[][] lists)
        => Container(Document, 0, [Atom(DocumentAtom, new byte[40]), .. lists, Atom(0x03EA, [])]);

    /// <summary>A shape whose client text box holds these atoms.</summary>
    static byte[] Shape(params byte[][] textbox)
        => Container(SpContainer, 0, Atom(Sp, new byte[8], instance: 1), Container(ClientTextbox, 0, textbox));

    /// <summary>A Slide container whose drawing holds these shapes.</summary>
    static byte[] SlideContainer(params byte[][] shapes)
        => Container(Slide, 0, Atom(SlideAtom, new byte[24]),
            Container(PPDrawing, 0, Container(DgContainer, 0, Container(SpgrContainer, 0, shapes))));

    static byte[] TextBox(string text) => Shape(Header(4), Chars(text), Atom(StyleTextPropAtom, new byte[10]));
    static byte[] PlaceholderRef(uint index) => Shape(Atom(OutlineTextRefAtom, U32s(index)));

    /// <summary>A document stream written record by record; Add gives each top-level record's position.</summary>
    sealed class Stream
    {
        readonly MemoryStream _bytes = new();
        public int Add(byte[] record)
        {
            int at = (int)_bytes.Length;
            _bytes.Write(record);
            return at;
        }

        /// <summary>A persist directory with one block per entry.</summary>
        public int Directory(params (uint Id, int At)[] entries)
            => Add(Atom(PersistDirectoryAtom, U32s(entries.SelectMany(e => new[] { 1u << 20 | e.Id, (uint)e.At }).ToArray())));

        public int Edit(int previous, int directory, uint documentRef = 1, uint? encryption = null)
        {
            var data = encryption is { } e
                ? U32s(256, 0x03000000, (uint)previous, (uint)directory, documentRef, 8, 1, e)
                : U32s(256, 0x03000000, (uint)previous, (uint)directory, documentRef, 8, 1);
            return Add(Atom(UserEditAtom, data));
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>
    /// A PowerPoint 97-2003 file written by OLE, for other tests: each slide's text as outline text (no persist directory,
    /// so the outline is all there is), and a Current User stream with the encrypted token when asked.
    /// </summary>
    internal static byte[] PptFile(string[] slides, bool encrypted = false)
    {
        var s = new Stream();
        var list = new List<byte[]>();
        for (int i = 0; i < slides.Length; i++)
        {
            list.Add(Persist((uint)(i + 3), (uint)(256 + i)));
            list.Add(Header(0));
            list.Add(Chars(slides[i]));
        }
        s.Add(DocumentContainer(SlideList(0, [.. list])));
        var document = s.ToArray();
        return CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "PowerPoint Document", document);
            if (encrypted) CfbFixtures.Stream(root, "Current User", CurrentUser(0, 0xF3D1C4DF));
        });
    }

    static byte[] CurrentUser(int editAt, uint token = 0xE391C05F)
        => Atom(CurrentUserAtom, [.. U32s(0x14, token, (uint)editAt), 0, 0, 0xF4, 0x03, 3, 0, 0, 0]);

    static List<string>? Parse(byte[] document, byte[]? currentUser, int maxChars = 100_000)
        => LegacyPowerPoint.Parse(document, currentUser, maxChars, CancellationToken.None);

    /// <summary>
    /// Two slides: the first with a title and body in the outline, a text box and a placeholder pointing at the outline;
    /// the second with one-byte outline text only. Masters and notes have text that must not show.
    /// </summary>
    static (byte[] Document, byte[] CurrentUser) TwoSlides()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(
            SlideList(1, Persist(2, 0x80000000), Header(0), Chars("master text")),
            SlideList(0,
                Persist(3, 256), Header(0), Chars("Title one"), Header(1), Chars("Body\rsecond line"),
                Persist(4, 257), Header(0), Bytes("Caf\xE9 two")),
            SlideList(2, Persist(5, 256), Header(2), Chars("notes text"))));
        int slide1 = s.Add(SlideContainer(PlaceholderRef(0), TextBox("Free box"), PlaceholderRef(1)));
        int slide2 = s.Add(SlideContainer(PlaceholderRef(0)));
        int directory = s.Directory((1, doc), (3, slide1), (4, slide2));
        int edit = s.Edit(0, directory);
        return (s.ToArray(), CurrentUser(edit));
    }

    // ------------------------------------------------------------------ normal cases

    [Fact]
    public void OutlineAndTextBoxesInOrder_MastersAndNotesLeftOut()
    {
        var (doc, user) = TwoSlides();
        var slides = Parse(doc, user);
        Assert.NotNull(slides);
        Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Café two"], slides);
    }

    [Fact]
    public void OutlineOnly()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("Only the outline"))));
        int slide = s.Add(SlideContainer(PlaceholderRef(0)));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        Assert.Equal(["Only the outline"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void TextBoxesOnly_TwoByteAndOneByteAtoms()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256))));
        int slide = s.Add(SlideContainer(TextBox("Größe 日本"), Shape(Header(4), Bytes("plain bytes"))));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        Assert.Equal(["Größe 日本\nplain bytes"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void SlideWithoutTextIsEmpty()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Persist(4, 257), Header(0), Chars("second"))));
        int a = s.Add(SlideContainer());
        int b = s.Add(SlideContainer());
        int edit = s.Edit(0, s.Directory((1, doc), (3, a), (4, b)));
        Assert.Equal(["", "second"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void NoSlideListGivesNoSlides()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer());
        int edit = s.Edit(0, s.Directory((1, doc)));
        Assert.Equal([], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void LaterEditMovesASlide()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256))));
        int old = s.Add(SlideContainer(TextBox("old version")));
        int edit1 = s.Edit(0, s.Directory((1, doc), (3, old)));
        int moved = s.Add(SlideContainer(TextBox("new version")));
        int edit2 = s.Edit(edit1, s.Directory((3, moved)));
        var doc2 = s.ToArray();
        Assert.Equal(["new version"], Parse(doc2, CurrentUser(edit2)));
        // The earlier edit alone still sees the old slide.
        Assert.Equal(["old version"], Parse(doc2, CurrentUser(edit1)));
    }

    [Fact]
    public void LaterEditReplacesTheDocument()
    {
        var s = new Stream();
        int doc1 = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("first save"))));
        int slide = s.Add(SlideContainer(PlaceholderRef(0)));
        int edit1 = s.Edit(0, s.Directory((1, doc1), (3, slide)));
        int doc2 = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("second save"))));
        int edit2 = s.Edit(edit1, s.Directory((1, doc2)));
        Assert.Equal(["second save"], Parse(s.ToArray(), CurrentUser(edit2)));
    }

    [Fact]
    public void NoCurrentUserAndNoEditGivesTheOutlineOnly()
    {
        // Without the persist directory there is no telling which Slide container is which slide: a title master is a
        // Slide container too, stored here before the real ones, and pairing by stream order would shift every text box.
        var s = new Stream();
        s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("one"), Persist(4, 257), Header(0), Chars("two"))));
        s.Add(SlideContainer(TextBox("title master box")));
        s.Add(SlideContainer(TextBox("box one")));
        s.Add(SlideContainer(TextBox("box two")));
        var doc = s.ToArray();
        Assert.Equal(["one", "two"], Parse(doc, null));
        // A Current User stream that leads nowhere falls back the same way.
        Assert.Equal(["one", "two"], Parse(doc, CurrentUser(doc.Length - 4)));
        Assert.Equal(["one", "two"], Parse(doc, [1, 2, 3]));
        // So does a UserEditAtom whose persist directory isn't one.
        int edit = s.Edit(0, 8);
        var withEdit = s.ToArray();
        Assert.Equal(["one", "two"], Parse(withEdit, null));
        Assert.Equal(["one", "two"], Parse(withEdit, CurrentUser(edit + 1)));
    }

    [Fact]
    public void MissingCurrentUserUsesTheLastEdit()
    {
        var (doc, user) = TwoSlides();
        var expected = Parse(doc, user);
        Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Café two"], expected);
        Assert.Equal(expected, Parse(doc, null));
        Assert.Equal(expected, Parse(doc, [1, 2, 3]));
        Assert.Equal(expected, Parse(doc, user[..19]));

        // With several edits the last one wins and its chain of earlier edits is followed.
        var s = new Stream();
        int d = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Persist(4, 257))));
        int old = s.Add(SlideContainer(TextBox("old version")));
        int other = s.Add(SlideContainer(TextBox("from the first edit")));
        int edit1 = s.Edit(0, s.Directory((1, d), (3, old), (4, other)));
        int moved = s.Add(SlideContainer(TextBox("new version")));
        s.Edit(edit1, s.Directory((3, moved)));
        Assert.Equal(["new version", "from the first edit"], Parse(s.ToArray(), null));

        // The same through a file without a Current User stream.
        var file = CfbFixtures.Ole(root => CfbFixtures.Stream(root, "PowerPoint Document", doc));
        WithFile(file, path => Assert.Equal(expected, LegacyPowerPoint.ReadSlides(path, 1000, CancellationToken.None)));
    }

    [Fact]
    public void CurrentUserAtAWrongOffsetUsesTheLastEdit()
    {
        var (doc, user) = TwoSlides();
        var expected = Parse(doc, user);
        int directory = IndexOf(doc, [0x00, 0x00, 0x72, 0x17]);
        Assert.True(directory > 0);
        // At the Document, past the end, at the persist directory, inside the edit, at the very end.
        foreach (int wrong in new[] { 0, doc.Length + 100, directory, doc.Length - 30, doc.Length - 4, -1 })
            Assert.Equal(expected, Parse(doc, CurrentUser(wrong)));
        // A token that only says encrypted still gives nothing.
        Assert.Null(Parse(doc, CurrentUser(0, token: 0xF3D1C4DF)));
    }

    [Fact]
    public void LastEditThatNamesEncryptionGivesNull()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("secret"))));
        s.Edit(0, s.Directory((1, doc)), encryption: 9);
        Assert.Null(Parse(s.ToArray(), null));
    }

    [Fact]
    public void LoopingEditsWithoutCurrentUserStayWithinTheBudget()
    {
        // Two edits pointing at each other, found without a Current User stream.
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("still read"))));
        int slide = s.Add(SlideContainer(TextBox("box")));
        int directory = s.Directory((1, doc), (3, slide));
        int first = s.Edit(0, directory);
        int second = s.Edit(first, directory);
        var bytes = s.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first + 8 + 8), (uint)second);
        Assert.Equal(["still read\nbox"], Parse(bytes, null));

        // Ten thousand edits in one loop, all naming one empty directory, with a Current User stream pointing into the
        // loop: the first walk uses up the edits, and the walk from the last edit shares that bound instead of starting
        // over.
        const int edits = 10_000;
        var t = new Stream();
        t.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("outline"))));
        t.Add(SlideContainer(TextBox("unreachable")));
        int empty = t.Add(Atom(PersistDirectoryAtom, []));
        var at = new int[edits];
        for (int i = 0; i < edits; i++) at[i] = t.Edit(i == 0 ? 0 : at[i - 1], empty);
        var loop = t.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(at[0] + 8 + 8), (uint)at[edits - 1]);
        var sw = Stopwatch.StartNew();
        var result = LegacyPowerPoint.Parse(loop, CurrentUser(at[edits / 2]), 1000, CancellationToken.None, out var spent);
        Assert.Equal(["outline"], result);
        Assert.True(spent.Edits <= 4096, $"edits visited: {spent.Edits}");
        // Without the Current User stream the walk from the last edit alone is bounded the same way.
        result = LegacyPowerPoint.Parse(loop, null, 1000, CancellationToken.None, out spent);
        Assert.Equal(["outline"], result);
        Assert.True(spent.Edits <= 4096, $"edits visited: {spent.Edits}");
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void SlidesListedInADifferentOrderThanStored()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(4, 257), Persist(3, 256))));
        int a = s.Add(SlideContainer(TextBox("stored first")));
        int b = s.Add(SlideContainer(TextBox("stored second")));
        int edit = s.Edit(0, s.Directory((1, doc), (3, a), (4, b)));
        Assert.Equal(["stored second", "stored first"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void OutlineTextRefIsNotCountedTwice()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("Heading"))));
        int slide = s.Add(SlideContainer(PlaceholderRef(0), PlaceholderRef(0)));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        Assert.Equal(["Heading"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void TextOutsideClientTextBoxesIsIgnored()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256))));
        // A text atom loose in the slide container and one in a shape without a client text box.
        int slide = s.Add(Container(Slide, 0, Atom(SlideAtom, new byte[24]), Chars("loose"),
            Container(PPDrawing, 0, Container(DgContainer, 0, Container(SpContainer, 0, Chars("no box")), TextBox("in box")))));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        Assert.Equal(["in box"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void ControlCharacterRules()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256),
            Header(0), Chars("a\rb\vc\td\0e\u0001f\u001Fg"),
            Header(1), Bytes("x\ry\x0Bz\x07!"),
            Header(1), Chars("\r\r"),
            Header(1), Chars(""),
            Header(1), Chars("trailing\r\r"),
            Header(1), Atom(TextCharsAtom, [0x3D, 0xD8, 0x00, 0xDE, 0x00, 0xD8, 0x41, 0x00, 0x42]))));
        int slide = s.Add(SlideContainer());
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        // A pair of surrogates stays, a lone one becomes U+FFFD, an odd last byte is dropped.
        Assert.Equal(["a\nb\nc\tdefg\nx\ny\nz!\ntrailing\n\U0001F600\uFFFDA"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void MaxCharsCountsAcrossSlides()
    {
        var (doc, user) = TwoSlides();
        // All of it: 35 + 8 characters.
        Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Café two"], Parse(doc, user, 43));
        Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Café two"], Parse(doc, user, 1000));
        // The second slide is cut.
        Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Caf"], Parse(doc, user, 38));
        // The first slide is cut and the second left out.
        Assert.Equal(["Title one\nBo"], Parse(doc, user, 12));
        Assert.Equal(["Title one"], Parse(doc, user, 9));
        Assert.Equal(["Title one\nBody\nsecond line\nFree box"], Parse(doc, user, 35));
        // A cut right after a line end keeps it, so the total still reaches maxChars; the same inside the text boxes.
        Assert.Equal(["Title one\n"], Parse(doc, user, 10));
        Assert.Equal(["Title one\nBody\nsecond line\n"], Parse(doc, user, 27));
        Assert.Equal(["Title one\nBody\nsecond line\nFre"], Parse(doc, user, 30));
        Assert.Equal([""], Parse(doc, user, 0));
        Assert.Equal([""], Parse(doc, user, -5));
    }

    [Fact]
    public void TotalReachesMaxCharsExactlyWhenSomethingIsLeftOut()
    {
        var (doc, user) = TwoSlides();
        // The whole text is 43 characters: any smaller maxChars cuts and the total is exactly maxChars; a larger one
        // gives all of it, less than maxChars. (43 itself is the one case that reaches maxChars with nothing left out.)
        for (int max = 1; max <= 60; max++)
        {
            var slides = Parse(doc, user, max);
            Assert.NotNull(slides);
            int total = slides.Sum(x => x.Length);
            if (max < 43) Assert.Equal(max, total);
            else Assert.Equal(43, total);
        }
        // A slide list longer than the text: the empty slides after the limit are left out, and the total says so.
        var s = new Stream();
        int d = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("abc"), Persist(4, 257), Persist(5, 258))));
        int edit = s.Edit(0, s.Directory((1, d)));
        Assert.Equal(["abc"], Parse(s.ToArray(), CurrentUser(edit), 3));
        Assert.Equal(["abc", "", ""], Parse(s.ToArray(), CurrentUser(edit), 4));
    }

    [Fact]
    public void MaxCharsNeverSplitsASurrogatePair()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("ab\U0001F600"), Persist(4, 257), Header(0), Chars("x"))));
        int slide = s.Add(SlideContainer());
        int boxed = s.Add(SlideContainer(TextBox("ab\U0001F600")));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide), (4, boxed)));
        var bytes = s.ToArray();
        // The pair that doesn't fit becomes U+FFFD, so the total still reaches maxChars.
        Assert.Equal(["ab\uFFFD"], Parse(bytes, CurrentUser(edit), 3));
        Assert.Equal(["ab\U0001F600"], Parse(bytes, CurrentUser(edit), 4));
        // In a slide's text boxes, cut while the box text is built and cut when it is copied after the outline.
        Assert.Equal(["ab\U0001F600", "x\nab\uFFFD"], Parse(bytes, CurrentUser(edit), 9));
        Assert.Equal(["ab\U0001F600", "x\na"], Parse(bytes, CurrentUser(edit), 7));
        var t = new Stream();
        int doc2 = t.Add(DocumentContainer(SlideList(0, Persist(3, 256))));
        int slide2 = t.Add(SlideContainer(TextBox("ab\U0001F600")));
        int edit2 = t.Edit(0, t.Directory((1, doc2), (3, slide2)));
        Assert.Equal(["ab\uFFFD"], Parse(t.ToArray(), CurrentUser(edit2), 3));
    }

    // ------------------------------------------------------------------ encrypted

    [Fact]
    public void EncryptedUserEditGivesNull()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("secret"))));
        int slide = s.Add(SlideContainer());
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)), encryption: 9);
        Assert.Null(Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void UserEditWithZeroEncryptionIdIsNotEncrypted()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("open"))));
        int edit = s.Edit(0, s.Directory((1, doc)), encryption: 0);
        Assert.Equal(["open"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void CryptSessionContainerGivesNull()
    {
        var s = new Stream();
        s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("secret"))));
        s.Add(Container(CryptSession10Container, 0, Atom(0x2F15, new byte[16])));
        Assert.Null(Parse(s.ToArray(), null));
    }

    [Fact]
    public void EncryptedCurrentUserTokenGivesNull()
    {
        var (doc, _) = TwoSlides();
        var edit = doc.Length - 36; // the UserEditAtom written last
        Assert.Null(Parse(doc, CurrentUser(edit, token: 0xF3D1C4DF)));
        Assert.NotNull(Parse(doc, CurrentUser(edit)));
    }

    [Fact]
    public void NotAPresentationGivesNull()
    {
        Assert.Null(Parse([], null));
        Assert.Null(Parse([1, 2, 3], null));
        Assert.Null(Parse(new byte[4096], null)); // records of type 0, no Document
        Assert.Null(Parse(Encoding.ASCII.GetBytes("This is just some text, not records at all."), null));
    }

    // ------------------------------------------------------------------ hostile input

    [Fact]
    public void TruncatedAtEveryByte()
    {
        var (doc, user) = TwoSlides();
        for (int n = 0; n <= doc.Length; n++)
        {
            var slides = Parse(doc[..n], user);
            if (slides != null) Assert.All(slides, t => Assert.True(t.Length <= 43));
        }
        for (int n = 0; n <= user.Length; n++) Assert.NotNull(Parse(doc, user[..n]));
    }

    [Fact]
    public void LengthPastTheEndIsCutToTheParent()
    {
        var (doc, user) = TwoSlides();
        // The Document container claims 4 GB: it is cut to the stream and still read.
        var big = (byte[])doc.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(4), 0xFFFFFFFF);
        var slides = Parse(big, user);
        Assert.NotNull(slides);
        Assert.Equal("Title one\nBody\nsecond line\nFree box", slides[0]);

        // A text atom claiming more than its slide list holds.
        var s = new Stream();
        s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Atom(TextCharsAtom, Encoding.Unicode.GetBytes("abc")))));
        var bytes = s.ToArray();
        int chars = IndexOf(bytes, Encoding.Unicode.GetBytes("abc")) - 8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(chars + 4), 0x7FFFFFF0);
        var cut = Parse(bytes, null);
        Assert.NotNull(cut);
        Assert.StartsWith("abc", cut[0]);
    }

    [Fact]
    public void ZeroLengthRecords()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(Container(SlideListWithText, 0), SlideList(0, Persist(3, 256), Atom(TextCharsAtom, []), Atom(SlidePersistAtom, []))));
        int slide = s.Add(Container(Slide, 0, Container(PPDrawing, 0), Container(ClientTextbox, 0), Atom(TextBytesAtom, [])));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        // The first slide list (empty) is the one read.
        Assert.Equal([], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void PersistDirectoryPointingAnywhere()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Persist(4, 257), Persist(5, 258), Persist(6, 259), Persist(7, 260))));
        int slide = s.Add(SlideContainer(TextBox("real")));
        // Slides pointing at the Document itself, inside a slide, past the end, at a shared slide; the Document id at a slide.
        int edit = s.Edit(0, s.Directory((1, slide), (3, doc), (4, slide + 8), (5, int.MaxValue), (6, slide), (7, slide)));
        var slides = Parse(s.ToArray(), CurrentUser(edit));
        // The Document reference is wrong, so the last top-level Document is used.
        Assert.Equal(["", "", "", "real", "real"], slides);
    }

    [Fact]
    public void PersistDirectoryWithHugeCounts()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("kept"))));
        // A block claiming 4095 entries with only two there, then a block header with nothing after it.
        int directory = s.Add(Atom(PersistDirectoryAtom, U32s(0xFFFu << 20 | 1, (uint)doc, 0, 0xFFF00010)));
        int edit = s.Edit(0, directory);
        Assert.Equal(["kept"], Parse(s.ToArray(), CurrentUser(edit)));
    }

    [Fact]
    public void UserEditChainThatLoops()
    {
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("still read"))));
        int directory = s.Directory((1, doc));
        // Two edits pointing at each other, and one pointing at itself.
        int first = s.Edit(0, directory);
        int second = s.Edit(first, directory);
        var bytes = s.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first + 8 + 8), (uint)second);
        var sw = Stopwatch.StartNew();
        Assert.Equal(["still read"], Parse(bytes, CurrentUser(second)));
        var self = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(self.AsSpan(second + 8 + 8), (uint)second);
        Assert.Equal(["still read"], Parse(self, CurrentUser(second)));
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void ManyEditsSharingOneHugeDirectoryStayLinear()
    {
        // One persist directory of 200,000 entries named by 4,000 edits: it is merged once.
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("fine"))));
        var entries = new uint[1 + 200_000];
        entries[0] = 0xFFFu << 20 | 1;
        entries[1] = (uint)doc;
        int directory = s.Add(Atom(PersistDirectoryAtom, U32s(entries)));
        int edit = 0;
        for (int i = 0; i < 4000; i++) edit = s.Edit(edit, directory);
        var sw = Stopwatch.StartNew();
        Assert.Equal(["fine"], Parse(s.ToArray(), CurrentUser(edit)));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void HundredThousandNestedContainers()
    {
        const int depth = 100_000;
        byte[] Nest(byte[] inner)
        {
            var bytes = new byte[depth * 8 + inner.Length];
            for (int i = 0; i < depth; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8), 0x000F);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8 + 2), (ushort)ClientTextbox);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), (uint)(bytes.Length - (i + 1) * 8));
            }
            inner.CopyTo(bytes, depth * 8);
            return bytes;
        }

        var sw = Stopwatch.StartNew();
        // Inside a slide: the text at the bottom is too deep and skipped.
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("outline"))));
        int slide = s.Add(Container(Slide, 0, Nest(Chars("deep")), TextBox("shallow")));
        int edit = s.Edit(0, s.Directory((1, doc), (3, slide)));
        Assert.Equal(["outline\nshallow"], Parse(s.ToArray(), CurrentUser(edit)));

        // Inside the Document and inside a slide list.
        var t = new Stream();
        t.Add(Container(Document, 0, Nest([]), SlideList(0, Persist(3, 256), Nest(Chars("x")), Header(0), Chars("after"))));
        Assert.Equal(["after"], Parse(t.ToArray(), null));

        // At the top level, before everything.
        var u = new Stream();
        u.Add(Nest([]));
        u.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("top"))));
        Assert.Equal(["top"], Parse(u.ToArray(), null));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void ManySlidesSharingOneDeepSlideStayLinear()
    {
        // 20,000 listed slides whose persist entries point at successive containers nested inside one slide: the
        // records visited in all are bounded, so this ends quickly with a partial result.
        const int count = 20_000;
        var nest = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(nest.AsSpan(i * 8), 0x000F);
            BinaryPrimitives.WriteUInt16LittleEndian(nest.AsSpan(i * 8 + 2), (ushort)Slide);
            BinaryPrimitives.WriteUInt32LittleEndian(nest.AsSpan(i * 8 + 4), (uint)(nest.Length - (i + 1) * 8));
        }
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Enumerable.Range(0, count).Select(i => Persist((uint)(10 + i), (uint)(256 + i))).ToArray())));
        int slides = s.Add(nest);
        int directory = s.Directory([(1, doc), .. Enumerable.Range(0, count).Select(i => ((uint)(10 + i), slides + i * 8))]);
        int edit = s.Edit(0, directory);
        var sw = Stopwatch.StartNew();
        var result = Parse(s.ToArray(), CurrentUser(edit));
        Assert.NotNull(result);
        Assert.Equal(count, result.Count);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void ManySlidesSharingOneContainerOfEmptyTextsReadItOnce()
    {
        // 20,000 listed slides all pointing at one Slide container whose text box holds 1,000 empty text atoms: empty
        // texts add no characters, so maxChars never stops the reading; the container's text is built once and reused.
        const int count = 20_000, empties = 1_000;
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Enumerable.Range(0, count).Select(i => Persist((uint)(10 + i), (uint)(256 + i))).ToArray())));
        int slide = s.Add(SlideContainer(Shape(Enumerable.Range(0, empties).Select(_ => Atom(TextCharsAtom, [])).ToArray())));
        int directory = s.Directory([(1, doc), .. Enumerable.Range(0, count).Select(i => ((uint)(10 + i), slide))]);
        int edit = s.Edit(0, directory);
        var bytes = s.ToArray();
        var result = LegacyPowerPoint.Parse(bytes, CurrentUser(edit), 100_000, CancellationToken.None, out var spent);
        Assert.NotNull(result);
        Assert.Equal(count, result.Count);
        Assert.All(result, t => Assert.Equal("", t));
        // Each empty text is read once, not once per slide (that would be 20,000,000), and each slide costs one unit.
        Assert.Equal(empties, spent.Texts);
        Assert.Equal(count, spent.Slides);
        Assert.True(spent.Records < 2L * (count + empties) + 100, $"records visited: {spent.Records}");
    }

    [Fact]
    public void ManyOverlappingPersistDirectoriesStayWithinTheWordBudget()
    {
        // A thousand persist directories, each claiming to run on to the stream's end over the ones after it and 200,000
        // words of empty blocks (count 0, so the entry cap never sees them), named by a thousand edits. Walked in full,
        // that is over 200,000,000 words; the words read from all directories together are bounded by the stream length.
        const int directories = 1_000, padding = 200_000;
        var s = new Stream();
        int doc = s.Add(DocumentContainer(SlideList(0, Persist(3, 256), Header(0), Chars("fine"))));
        var at = new int[directories];
        for (int i = 0; i < directories; i++)
        {
            var header = Atom(PersistDirectoryAtom, U32s(1u << 20 | 1, (uint)doc));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x7FFFFFFF);
            at[i] = s.Add(header);
        }
        s.Add(new byte[padding * 4]);
        int edit = 0;
        for (int i = 0; i < directories; i++) edit = s.Edit(edit, at[i]);
        var bytes = s.ToArray();
        var result = LegacyPowerPoint.Parse(bytes, CurrentUser(edit), 1000, CancellationToken.None, out var spent);
        // The latest directory is merged first, so the Document is found.
        Assert.Equal(["fine"], result);
        long budget = bytes.Length / 4 * 2 + 1024;
        Assert.True(spent.DirectoryWords <= budget, $"directory words read: {spent.DirectoryWords}, budget {budget}");
        Assert.True(spent.DirectoryWords >= padding, "the first directories are still read in full");
    }

    [Fact]
    public void RandomDamageNeverThrows()
    {
        var (doc, user) = TwoSlides();
        var random = new Random(1234);
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 3000; round++)
        {
            var d = (byte[])doc.Clone();
            var u = (byte[])user.Clone();
            for (int k = random.Next(1, 8); k > 0; k--)
            {
                var target = random.Next(4) == 0 ? u : d;
                target[random.Next(target.Length)] = (byte)random.Next(256);
            }
            var slides = Parse(d, u, 200);
            if (slides != null) Assert.True(slides.Sum(x => x.Length) <= 200);
        }
        Assert.True(sw.ElapsedMilliseconds < 10_000);
    }

    [Fact]
    public void CancellationIsPassedOn()
    {
        var (doc, user) = TwoSlides();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyPowerPoint.Parse(doc, user, 100, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyPowerPoint.ReadSlides("x.ppt", 100, cts.Token));
    }

    // ------------------------------------------------------------------ the public method on files

    [Fact]
    public void ReadSlides_MissingFileGivesNull()
        => Assert.Null(LegacyPowerPoint.ReadSlides(Path.Combine(Path.GetTempPath(), "rp-missing-" + Guid.NewGuid().ToString("N") + ".ppt"), 1000, CancellationToken.None));

    [Fact]
    public void ReadSlides_NotACompoundFileGivesNull()
        => WithFile(Encoding.ASCII.GetBytes("Plain text that only has a .ppt name."), path => Assert.Null(LegacyPowerPoint.ReadSlides(path, 1000, CancellationToken.None)));

    [Fact]
    public void ReadSlides_CompoundFileWithoutPresentationGivesNull()
    {
        var file = CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", new byte[600]));
        WithFile(file, path => Assert.Null(LegacyPowerPoint.ReadSlides(path, 1000, CancellationToken.None)));
    }

    [Fact]
    public void ReadSlides_ReadsAPresentationFile()
    {
        var (doc, user) = TwoSlides();
        var file = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "PowerPoint Document", doc);
            CfbFixtures.Stream(root, "Current User", user);
        });
        WithFile(file, path => Assert.Equal(["Title one\nBody\nsecond line\nFree box", "Café two"], LegacyPowerPoint.ReadSlides(path, 1000, CancellationToken.None)));
        var encrypted = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "PowerPoint Document", doc);
            CfbFixtures.Stream(root, "EncryptedPackage", new byte[16]);
        });
        WithFile(encrypted, path => Assert.Null(LegacyPowerPoint.ReadSlides(path, 1000, CancellationToken.None)));
    }

    static void WithFile(byte[] bytes, Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), "rp-ppt-" + Guid.NewGuid().ToString("N") + ".ppt");
        File.WriteAllBytes(path, bytes);
        try { test(path); }
        finally { File.Delete(path); }
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
