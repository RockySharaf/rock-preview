using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbRulesTests
{
    // A PackBits coder that, like Photoshop's, starts a repeat at any two equal bytes: on noisy rows it breaks literal
    // runs into short pieces, each with its own header byte, so the coded row comes out longer than the row.
    static byte[] PackBitsShortRepeats(byte[] row)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < row.Length)
        {
            int run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
            if (run >= 2)
            {
                o.Add((byte)(sbyte)(1 - run));
                o.Add(row[i]);
                i += run;
                continue;
            }
            int start = i, n = 0;
            while (i < row.Length && n < 128 && !(i + 1 < row.Length && row[i + 1] == row[i])) { i++; n++; }
            o.Add((byte)(n - 1));
            for (int k = start; k < start + n; k++) o.Add(row[k]);
        }
        return o.ToArray();
    }

    static byte[] UnpackBits(byte[] src, int size)
    {
        var dst = new byte[size];
        int si = 0, di = 0;
        while (si < src.Length && di < size)
        {
            sbyte n = (sbyte)src[si++];
            if (n >= 0) for (int k = 0; k <= n && di < size; k++) dst[di++] = src[si++];
            else if (n != -128) { byte v = src[si++]; for (int k = 0; k < 1 - n && di < size; k++) dst[di++] = v; }
        }
        return dst;
    }

    static long OldLimit(long rowBytes) => rowBytes + rowBytes / 64 + 64;

    [Theory]
    [InlineData(6000)]
    [InlineData(300)]
    [InlineData(1)]
    public void A_valid_row_longer_than_the_row_itself_is_within_the_limit(int rowBytes)
    {
        // Two equal bytes then one different, over and over: every 3 bytes cost 4 (a repeat and a 1-byte literal).
        var row = new byte[rowBytes];
        for (int i = 0; i < rowBytes; i++) row[i] = (byte)(i % 3 == 2 ? 200 + i % 7 : 10 + i / 3 % 50);
        var coded = PackBitsShortRepeats(row);
        Assert.Equal(row, UnpackBits(coded, rowBytes));
        if (rowBytes >= 300) Assert.True(coded.Length > OldLimit(rowBytes), $"coded {coded.Length} should pass the old limit {OldLimit(rowBytes)}");
        Assert.True(coded.Length <= ThumbRules.PackBitsMaxRow(rowBytes));
    }

    [Fact]
    public void The_worst_valid_coding_is_within_the_limit_and_hostile_lengths_are_not()
    {
        // The worst any coder can do is a header byte per data byte: twice the row.
        foreach (long rowBytes in new long[] { 1, 6000, 60_000 })
        {
            Assert.True(2 * rowBytes <= ThumbRules.PackBitsMaxRow(rowBytes));
            Assert.True(2 * rowBytes + 65 > ThumbRules.PackBitsMaxRow(rowBytes));
        }
        Assert.Equal(12_064, ThumbRules.PackBitsMaxRow(6000));
    }

    // ── The JPEG frame scan and the large-decode exemption ──────────────────────────────────────────────────────────

    static JpegReadAt Over(byte[] file) => (offset, buffer) =>
    {
        if (offset >= file.Length) return 0;
        int n = (int)Math.Min(buffer.Length, file.Length - offset);
        file.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    };

    static byte[] Jpeg(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), 0xFF, 0xD9];

    static byte[] Segment(byte marker, int payload) => [0xFF, marker, (byte)((payload + 2) >> 8), (byte)(payload + 2), .. new byte[payload]];

    [Theory]
    [InlineData(0xC0, (int)JpegFrame.Baseline)]
    [InlineData(0xC1, (int)JpegFrame.Baseline)]
    [InlineData(0xC2, (int)JpegFrame.WholeFrame)]
    [InlineData(0xC3, (int)JpegFrame.WholeFrame)]
    [InlineData(0xC9, (int)JpegFrame.WholeFrame)]
    public void The_first_frame_marker_decides(byte sof, int expected)
    {
        var file = Jpeg(Segment(0xE0, 14), Segment(0xDB, 65), Segment(0xC4, 30), Segment(sof, 15));
        Assert.Equal((JpegFrame)expected, ThumbKinds.JpegFrameOf(Over(file)));
    }

    [Fact]
    public void Fill_bytes_and_markers_without_length_are_stepped_over()
    {
        byte[] file = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, .. Segment(0xE1, 40), 0xFF, 0x01, .. Segment(0xC0, 15), 0xFF, 0xD9];
        Assert.Equal(JpegFrame.Baseline, ThumbKinds.JpegFrameOf(Over(file)));
    }

    [Fact]
    public void Stray_bytes_between_segments_are_skipped_as_decoders_do()
    {
        // Bytes left between segments (decoders warn and go on): the frame after them is still found.
        byte[] stray = [0xFF, 0xD8, .. Segment(0xE0, 14), 0x00, 0x12, 0xFF, 0x00, 0x34, .. Segment(0xC0, 15), 0xFF, 0xD9];
        Assert.Equal(JpegFrame.Baseline, ThumbKinds.JpegFrameOf(Over(stray)));
        byte[] strayProgressive = [0xFF, 0xD8, .. Segment(0xE0, 14), 0x0D, 0x0A, .. Segment(0xC2, 15), 0xFF, 0xD9];
        Assert.Equal(JpegFrame.WholeFrame, ThumbKinds.JpegFrameOf(Over(strayProgressive)));
        // Stray bytes that run on past the skip limit leave it unknown.
        byte[] tooFar = [0xFF, 0xD8, .. Segment(0xE0, 14), .. new byte[ThumbKinds.JpegMaxStray + 10], .. Segment(0xC0, 15), 0xFF, 0xD9];
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over(tooFar)));
    }

    [Fact]
    public void A_profile_split_over_many_segments_still_finds_the_frame()
    {
        // A large colour profile in 100 APP2 chunks before the frame: more than the old limit of 64.
        var file = Jpeg([Segment(0xE0, 14), .. Enumerable.Range(0, 100).Select(_ => Segment(0xE2, 60)), Segment(0xC0, 15)]);
        Assert.Equal(JpegFrame.Baseline, ThumbKinds.JpegFrameOf(Over(file)));
    }

    [Fact]
    public void Anything_unclear_is_unknown()
    {
        // Out of markers: a crafted file puts more segments before its progressive frame than the scan reads.
        var padded = Jpeg([.. Enumerable.Range(0, ThumbKinds.JpegMaxMarkers + 6).Select(_ => Segment(0xFE, 3)), Segment(0xC2, 15)]);
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over(padded)));
        // A scan, or the end, before any frame.
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over(Jpeg(Segment(0xE0, 14), Segment(0xDA, 10)))));
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over(Jpeg(Segment(0xE0, 14)))));
        // Cut short, garbage where a marker should be, not a JPEG at all, an unreadable file.
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over([0xFF, 0xD8, 0xFF, 0xE0, 0x00])));
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over([0xFF, 0xD8, 0x12, 0x34, 0x56, 0x78])));
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10])));
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf(Over([])));
        Assert.Equal(JpegFrame.Unknown, ThumbKinds.JpegFrameOf((_, _) => throw new IOException("unreadable")));
    }

    [Fact]
    public void Only_a_jpeg_container_with_a_found_baseline_frame_is_scaled_while_read()
    {
        Assert.True(ThumbKinds.ScaledWhileRead(containerIsJpeg: true, 1, JpegFrame.Baseline));
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: true, 1, JpegFrame.WholeFrame));
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: true, 1, JpegFrame.Unknown));
        // A PNG or TIFF named .jpg: its decoder says what it is, and its markers are no JPEG's.
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: false, 1, JpegFrame.Baseline));
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: false, 1, JpegFrame.Unknown));
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: true, 2, JpegFrame.Baseline));
        Assert.False(ThumbKinds.ScaledWhileRead(containerIsJpeg: true, 0, JpegFrame.Baseline));
    }
}
