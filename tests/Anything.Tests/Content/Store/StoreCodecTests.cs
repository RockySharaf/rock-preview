using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

public class StoreCodecTests
{
    [Fact]
    public void Crc32c_matches_the_known_answer_and_runs_incrementally()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
        Assert.Equal(0u, Crc32C.Compute([]));
        var data = new byte[1000];
        new Random(1).NextBytes(data);
        uint state = Crc32C.Start;
        state = Crc32C.Update(state, data.AsSpan(0, 13));
        state = Crc32C.Update(state, data.AsSpan(13, 500));
        state = Crc32C.Update(state, data.AsSpan(513));
        Assert.Equal(Crc32C.Compute(data), Crc32C.Finish(state));
    }

    [Fact]
    public void Varints_round_trip_and_reject_bad_input()
    {
        Span<byte> b = stackalloc byte[VarInt.MaxLength];
        foreach (ulong v in new ulong[] { 0, 1, 127, 128, 300, 16_383, 16_384, uint.MaxValue, long.MaxValue, ulong.MaxValue })
        {
            int n = VarInt.Write(b, v);
            Assert.Equal(VarInt.Size(v), n);
            int pos = 0;
            Assert.True(VarInt.TryRead(b[..n], ref pos, out ulong back));
            Assert.Equal(v, back);
            Assert.Equal(n, pos);
            pos = 0;
            Assert.False(n > 1 && VarInt.TryRead(b[..(n - 1)], ref pos, out _)); // truncated
        }
        int p = 0;
        Assert.False(VarInt.TryRead([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02], ref p, out _)); // past 64 bits
        p = 0;
        Assert.False(VarInt.TryRead([0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01], ref p, out _)); // overlong
        p = 0;
        Assert.False(VarInt.TryRead([0x85, 0x01], ref p, 100, out int _)); // 133 > max
        p = 0;
        Assert.False(VarInt.TryReadString([0x05, (byte)'a', (byte)'b'], ref p, 100, out _)); // shorter than its length
        p = 0;
        Assert.False(VarInt.TryReadString([0x02, 0xC3, 0x28], ref p, 100, out _)); // not UTF-8
    }

    [Fact]
    public void Stored_text_round_trips_short_long_and_unicode()
    {
        var docs = new[]
        {
            new StoredDoc("", [], null),
            new StoredDoc("short", [], null),
            new StoredDoc("", [(DocField.Title, "Only a title")], null),
            new StoredDoc(string.Concat(Enumerable.Repeat("Invoice INV-2024-0042 for Café résumé 東京都 😀\n", 400)),
                [(DocField.Title, "Annual report"), (DocField.Keywords, "a; b; c"), (DocField.Author, "Ünal")], null),
        };
        foreach (var doc in docs)
        {
            var stored = StoredText.Encode(doc, out int raw);
            Assert.Equal(StoredText.BlobLength(doc), raw);
            if (raw == 0) Assert.Empty(stored);
            else if (raw < SegmentFormat.RawUnder) Assert.Equal(raw, stored.Length); // kept as it is
            var back = StoredText.Decode(stored, raw);
            Assert.NotNull(back);
            Assert.Equal(doc.Body, back!.Body);
            Assert.Equal(doc.Fields, back.Fields);
        }
        var big = StoredText.Encode(docs[3], out int bigRaw);
        Assert.True(big.Length < bigRaw / 10, "repetitive text compresses");
    }

    [Fact]
    public void Damaged_stored_text_decodes_to_null_never_throws()
    {
        var doc = new StoredDoc(string.Concat(Enumerable.Range(0, 3000).Select(i => $"word{i} ")), [(DocField.Title, "t")], null);
        var stored = StoredText.Encode(doc, out int raw);
        Assert.Null(StoredText.Decode(stored.AsSpan(0, stored.Length / 2), raw)); // truncated
        Assert.Null(StoredText.Decode(stored, raw + 1)); // wrong raw length
        Assert.Null(StoredText.Decode(stored, SegmentFormat.MaxBlob + 1));
        Assert.Null(StoredText.Decode([1, 2, 3], 0));
        var rnd = new Random(5);
        for (int i = 0; i < 500; i++)
        {
            var bad = (byte[])stored.Clone();
            bad[rnd.Next(bad.Length)] ^= (byte)(1 << rnd.Next(8));
            _ = StoredText.Decode(bad, raw); // null or some text: never an exception
        }
        Assert.Null(StoredText.ParseBlob([0x05])); // five fields promised, none there
        Assert.Null(StoredText.ParseBlob([0x01, 0x01, 0x09, (byte)'a'])); // a field longer than the blob
        Assert.Null(StoredText.ParseBlob([0x00, 0xFF, 0xFE])); // body not UTF-8
    }

    [Fact]
    public void A_body_past_8_MB_is_cut_between_whole_characters()
    {
        // 😀 is a surrogate pair (4 UTF-8 bytes): the cut must not split one.
        var body = new StringBuilder();
        for (long bytes = 0; bytes < SegmentFormat.MaxBlob + 100; bytes += 4 + 2 + 1000) body.Append("😀é").Append('a', 1000);
        var doc = new StoredDoc(body.ToString(), [(DocField.Title, new string('t', 10))], null);
        var stored = StoredText.Encode(doc, out int raw);
        Assert.True(raw <= SegmentFormat.MaxBlob);
        Assert.True(raw > SegmentFormat.MaxBlob - 8);
        var back = StoredText.Decode(stored, raw)!;
        Assert.StartsWith(back.Body, doc.Body, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(back.Body[^1]));
        Assert.Equal(doc.Fields, back.Fields);
    }

    [Fact]
    public void Lone_surrogates_are_stored_as_replacement_characters()
    {
        var doc = new StoredDoc("a\uD800b", [], null);
        var back = StoredText.Decode(StoredText.Encode(doc, out int raw), raw)!;
        Assert.Equal("a\uFFFDb", back.Body);
        Assert.Equal(raw, StoredText.BlobLength(back));
    }

    [Fact]
    public void Picture_records_round_trip_within_their_fields()
    {
        var p = new PictureInfo(6000, 4000, 300, 300, 16, ColorMode.Cmyk, 6, 1, 5, 133_500_000_000_000_000L, true, 48_856_600, 2_352_200,
            800, 2.8f, 1 / 250f, 35.5f);
        Span<byte> b = stackalloc byte[SegmentFormat.PictureSize];
        SegmentFormat.WritePicture(b, p);
        Assert.Equal(p, SegmentFormat.ReadPicture(b));

        var odd = new PictureInfo(-5, 70_000, 100_000, -1, 1, ColorMode.Lab, 0, 0, 0, 0, false, 0, 0, 1_000_000, float.NaN, float.PositiveInfinity, 1e9f);
        var q = SegmentFormat.Quantize(odd);
        Assert.Equal(0, q.Width);
        Assert.Equal(70_000, q.Height);
        Assert.Equal(ushort.MaxValue, q.DpiX);
        Assert.Equal(0, q.DpiY);
        Assert.Equal(ushort.MaxValue, q.Iso);
        Assert.Equal(0, q.FNumber);
        Assert.Equal(0, q.ExposureSeconds);
        Assert.Equal(6553.5f, q.FocalLengthMm);
        Assert.Equal(q, SegmentFormat.Quantize(q)); // stable after one trip
    }

    [Fact]
    public void Segment_file_names_round_trip()
    {
        Assert.Equal("seg-0000002A.rpc", SegmentFormat.FileName(42));
        Assert.True(SegmentFormat.TryParseFileName("seg-0000002A.rpc", out uint id));
        Assert.Equal(42u, id);
        Assert.True(SegmentFormat.TryParseFileName("SEG-FFFFFFFF.RPC", out id));
        Assert.Equal(uint.MaxValue, id);
        Assert.False(SegmentFormat.TryParseFileName("seg-0000002A.rpc.tmp", out _));
        Assert.False(SegmentFormat.TryParseFileName("seg-2A.rpc", out _));
        Assert.False(SegmentFormat.TryParseFileName("seg-0000002G.rpc", out _));
    }

    static Manifest Sample() => new()
    {
        Generation = 12, CreatedUtc = 133_000_000_000_000_000L, NextSegmentId = 9, Mode = ContentMode.DocumentsAndPictures,
        Volumes = [new VolumeEntry(0, "vol:{1234}", "C:\\", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)), new VolumeEntry(3, "unc:\\\\NAS\\JOBS", "\\\\nas\\jobs", DateTime.UnixEpoch)],
        Segments =
        [
            new Manifest.SegmentEntry { Id = 2, DocCount = 100, Bytes = 5000, Tier = 0 },
            new Manifest.SegmentEntry
            {
                Id = 5, DocCount = 200, Bytes = 9000, Tier = 0, Damaged = true, Deleted = Bits(200, 0, 7, 64, 199), DeletedCount = 4,
                DeletedStats = new Dictionary<ContentKind, KindStats> { [ContentKind.Pdf] = new(4, 2, 1000, 400, 50, 9000, 30, 1) },
            },
        ],
        Stats = new Dictionary<ContentKind, KindStats> { [ContentKind.Pdf] = new(296, 100, 1, 2, 3, 4, 5, 6), [ContentKind.Picture] = new(1, 0, 0, 0, 0, 0, 0, 0) },
    };

    static ulong[] Bits(int count, params int[] docs)
    {
        var bits = new ulong[(count + 63) >> 6];
        foreach (int d in docs) bits[d >> 6] |= 1UL << d;
        return bits;
    }

    [Fact]
    public void Manifest_round_trips()
    {
        var m = Sample();
        var bytes = m.Serialize();
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(bytes, out var back));
        Assert.Equal(m.Generation, back!.Generation);
        Assert.Equal(m.NextSegmentId, back.NextSegmentId);
        Assert.Equal(m.Mode, back.Mode);
        Assert.Equal(m.Volumes, back.Volumes);
        Assert.Equal(m.Segments.Select(s => (s.Id, s.DocCount, s.Bytes, s.Damaged, s.DeletedCount)), back.Segments.Select(s => (s.Id, s.DocCount, s.Bytes, s.Damaged, s.DeletedCount)));
        Assert.Equal(m.Segments[1].Deleted, back.Segments[1].Deleted);
        Assert.Null(back.Segments[0].Deleted);
        Assert.Equal(m.Segments[1].DeletedStats, back.Segments[1].DeletedStats);
        Assert.Equal(m.Stats.OrderBy(k => k.Key), back.Stats.OrderBy(k => k.Key));
        Assert.True(back.Segments[1].IsDeleted(64));
        Assert.False(back.Segments[1].IsDeleted(63));
    }

    [Fact]
    public void A_damaged_manifest_is_bad_and_a_newer_one_is_newer()
    {
        var bytes = Sample().Serialize();
        for (int i = 0; i < bytes.Length; i++)
        {
            var bad = (byte[])bytes.Clone();
            bad[i] ^= 0x10;
            var result = Manifest.Parse(bad, out _);
            Assert.True(result != Manifest.ParseResult.Ok, $"flip at {i} accepted");
        }
        for (int n = 0; n < bytes.Length; n++) Assert.NotEqual(Manifest.ParseResult.Ok, Manifest.Parse(bytes.AsSpan(0, n), out _));

        var newer = (byte[])bytes.Clone();
        newer[8] = 2; // format 2
        Assert.Equal(Manifest.ParseResult.Newer, Manifest.Parse(newer, out _));
    }
}
