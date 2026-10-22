using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfCryptTests
{
    public static TheoryData<string> OracleNames => new() { "rc4-40", "rc4-128", "aes-128", "aes-256" };

    static byte[] Oracle(string name) => name switch
    {
        "rc4-40" => PdfOracles.Rc4_40,
        "rc4-128" => PdfOracles.Rc4_128,
        "aes-128" => PdfOracles.Aes128,
        _ => PdfOracles.Aes256,
    };

    [Theory]
    [MemberData(nameof(OracleNames))]
    public void PyMuPDF_encrypted_copies_open_with_the_empty_password(string name)
    {
        var doc = Read(Oracle(name));
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Hello encrypted world INV-2024-001", doc.Body);
        Assert.Contains((DocField.Title, "Oracle title"), doc.Fields);
        Assert.Contains((DocField.Author, "Rocky"), doc.Fields);
    }

    [Fact]
    public void A_user_password_is_password_protected()
    {
        var doc = Read(PdfOracles.UserPassword);
        Assert.Equal(DocStatus.PasswordProtected, doc.Status);
        Assert.Equal("", doc.Body);
        Assert.Empty(doc.Fields);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, false)]
    [InlineData(3, 1, true)]
    [InlineData(4, 2, true)]
    public void Every_handler_decrypts_strings_and_streams(int crypt, int xref, bool objectStreams)
    {
        var w = W.SimpleDocument(W.Lines("Secret invoice 7731", "Second line"), W.Lines("Page two"));
        w.Info = w.Add("<< /Title " + W.Str("Encrypted title") + " /Author " + W.Str("Ünïcode Author") + " >>");
        var doc = Read(w.Write((W.XrefKind)xref, objectStreams, (W.CryptKind)crypt));
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Secret invoice 7731\nSecond line\fPage two", doc.Body);
        Assert.Contains((DocField.Title, "Encrypted title"), doc.Fields);
        Assert.Contains((DocField.Author, "Ünïcode Author"), doc.Fields);
    }

    [Fact]
    public void Strings_inside_object_streams_are_not_decrypted_again()
    {
        // The writer leaves strings of objects inside object streams in the clear, as the format says (the stream is
        // what's encrypted): decrypting them again would turn the title to noise.
        var w = W.SimpleDocument(W.Lines("Body"));
        w.Info = w.Add("<< /Title (Plain inside the stream) >>");
        var doc = Read(w.Write(W.XrefKind.Stream, objectStreams: true, W.CryptKind.Rc4_128));
        Assert.Contains((DocField.Title, "Plain inside the stream"), doc.Fields);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Metadata_left_in_the_clear_with_EncryptMetadata_false(bool clearXmpAlthoughEncrypted)
    {
        var w = W.SimpleDocument(W.Lines("Body"));
        w.EncryptMetadata = clearXmpAlthoughEncrypted;
        string xmp = "<?xpacket begin=''?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Clear XMP</rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
        int meta = w.AddStream("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(xmp));
        // With EncryptMetadata false it must be plain; with true, some writers leave it plain anyway.
        w.LeavePlain(meta);
        w.Set(1, $"<< /Type /Catalog /Pages 2 0 R /Metadata {meta} 0 R >>");
        var doc = Read(w.Write(crypt: W.CryptKind.Aes128));
        Assert.Equal("Body", doc.Body);
        Assert.Contains((DocField.Title, "Clear XMP"), doc.Fields);
    }

    [Fact]
    public void Encrypted_metadata_is_decrypted()
    {
        var w = W.SimpleDocument(W.Lines("Body"));
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Hidden XMP</rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>";
        int meta = w.AddStream("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(xmp), "Flate");
        w.Set(1, $"<< /Type /Catalog /Pages 2 0 R /Metadata {meta} 0 R >>");
        Assert.Contains((DocField.Title, "Hidden XMP"), Read(w.Write(crypt: W.CryptKind.Aes256)).Fields);
    }

    [Fact]
    public void Other_security_handlers_and_bad_keys_are_password_protected()
    {
        var w = W.SimpleDocument(W.Lines("Body"));
        int encrypt = w.Add("<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /R 4 >>");
        Assert.Equal(DocStatus.PasswordProtected, Read(w.Write(extraTrailer: $" /Encrypt {encrypt} 0 R")).Status);

        // A standard handler whose /U doesn't come from the empty password.
        var file = Encoding.Latin1.GetString(W.SimpleDocument(W.Lines("Body")).Write(crypt: W.CryptKind.Rc4_128));
        int u = file.IndexOf("/U <", StringComparison.Ordinal) + 4;
        file = file[..u] + (file[u] == '0' ? '1' : '0') + file[(u + 1)..];
        Assert.Equal(DocStatus.PasswordProtected, Read(Encoding.Latin1.GetBytes(file)).Status);

        // An unknown revision.
        var w2 = W.SimpleDocument(W.Lines("Body"));
        int odd = w2.Add("<< /Filter /Standard /V 3 /R 3 /O <00> /U <00> /P -4 >>");
        Assert.Equal(DocStatus.PasswordProtected, Read(w2.Write(extraTrailer: $" /Encrypt {odd} 0 R")).Status);
    }

    [Fact]
    public void Rc4_matches_a_published_test_vector()
    {
        // RFC 6229 is for keystreams; this is the classic "Key"/"Plaintext" vector.
        byte[] cipher = PdfCrypt.Rc4(Encoding.ASCII.GetBytes("Key"), Encoding.ASCII.GetBytes("Plaintext"));
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(cipher));
    }
}
