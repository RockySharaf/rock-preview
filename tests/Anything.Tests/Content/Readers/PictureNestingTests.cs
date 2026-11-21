using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Readers.PictureFixtures;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// EXIF blocks and Photoshop resources hand blocks to each other (tag 34377 holds resources, resource 0x0422 holds EXIF):
/// a crafted nest of them is read only a few levels deep, never once per level until the stack overflows.
/// </summary>
public class PictureNestingTests
{
    /// <summary>
    /// A nest of levels around the innermost block: each level is a TIFF header with one IFD entry (tag 34377) whose value is
    /// an 8BIM block 0x0422 holding the next level (38 bytes a level).
    /// </summary>
    static byte[] Nest(int levels, byte[] innermost)
    {
        if (innermost.Length % 2 == 1) innermost = [.. innermost, 0];
        var prefixes = new byte[levels][];
        long inner = innermost.Length;
        for (int i = 0; i < levels; i++)
        {
            var p = new byte[38];
            p[0] = p[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 8); // IFD0
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), 1); // one entry
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), 34377);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), 7); // UNDEFINED
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14), (uint)(12 + inner));
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(18), 26); // the value: right after the IFD
            "8BIM"u8.CopyTo(p.AsSpan(26));
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(30), 0x0422);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(34), (uint)inner); // after an empty Pascal name
            prefixes[i] = p;
            inner += 38;
        }
        var ms = new MemoryStream();
        for (int i = levels - 1; i >= 0; i--) ms.Write(prefixes[i]);
        ms.Write(innermost);
        return ms.ToArray();
    }

    /// <summary>A Photoshop file with these image resources and nothing else.</summary>
    static byte[] Psd(byte[] resources)
    {
        var ms = new MemoryStream();
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); ms.Write(b); }
        ms.Write("8BPS"u8);
        U16(1); ms.Write(new byte[6]); U16(3); U32(600); U32(800); U16(8); U16(3);
        U32(0); // colour mode data
        U32(resources.Length); ms.Write(resources);
        U32(0); // layer and mask information
        U16(0); ms.Write(new byte[64]); // image data, never read
        return ms.ToArray();
    }

    /// <summary>
    /// Reads on a thread with a large stack, so a nest that the guard missed fails the assertions (the innermost block was
    /// read) instead of overflowing the test host's stack.
    /// </summary>
    static ExtractedDoc OnLargeStack(byte[] bytes, string extension)
    {
        ExtractedDoc? doc = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { doc = ReaderRun.Bytes(bytes, extension); }
            catch (Exception e) { error = e; }
        }, 256 << 20);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
        return doc!;
    }

    [Fact]
    public void A_deep_nest_in_a_tiff_stops_after_a_few_levels()
    {
        var doc = OnLargeStack(Nest(2000, Exif()), "tif");
        Assert.Null(doc.Field(DocField.CameraMake)); // the innermost EXIF block is 2,000 levels down
    }

    [Fact]
    public void A_deep_nest_in_a_jpeg_exif_segment_stops_after_a_few_levels()
    {
        var doc = OnLargeStack(Jpeg(exif: Nest(1000, Exif())), "jpg");
        Assert.Equal(640, doc.Picture?.Width); // the rest of the JPEG still reads
        Assert.Null(doc.Field(DocField.CameraMake));
    }

    [Fact]
    public void A_deep_nest_in_a_psd_stops_after_a_few_levels()
    {
        var doc = OnLargeStack(Psd(Irb(0x0422, Nest(2000, Exif()))), "psd");
        Assert.Equal(800, doc.Picture?.Width);
        Assert.Null(doc.Field(DocField.CameraMake));
    }

    [Fact]
    public void Real_nesting_still_reads_exif()
    {
        // A TIFF whose Photoshop resources hold an EXIF block (three levels), and a PSD's EXIF resource (two).
        Assert.Equal("Canon", OnLargeStack(Nest(1, Exif()), "tif").Field(DocField.CameraMake));
        Assert.Equal("Canon", OnLargeStack(Psd(Irb(0x0422, Exif())), "psd").Field(DocField.CameraMake));
    }

    [Fact]
    public void Picture_parts_refuse_to_go_deeper_than_the_limit()
    {
        var parts = new PictureParts(new ExtractBudget(ExtractLimits.Local, CancellationToken.None));
        for (int i = 0; i < PictureParts.MaxNesting; i++) Assert.True(parts.Enter());
        Assert.False(parts.Partial);
        Assert.False(parts.Enter());
        Assert.True(parts.Partial);
        parts.Leave();
        Assert.True(parts.Enter());
    }
}
