using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

public class StoreCommitTests
{
    static SynthDoc Doc(ulong key, string body, ContentKind kind = ContentKind.Text, string? path = null, ushort volume = 0)
        => new(Synth.Row(key, kind, volume: volume), path ?? $"Docs\\{key}.txt", new StoredDoc(body, [], null), 100, 3);

    [Fact]
    public void Added_documents_are_searchable_after_commit_with_their_local_numbers()
    {
        using var store = ContentStore.Open(null);
        var docs = Synth.Docs(500, seed: 3);
        using var batch = store.BeginBatch();
        // Added in a shuffled order: the batch sorts them by key.
        var shuffled = docs.OrderBy(d => d.Path.GetHashCode()).ToList();
        foreach (var d in shuffled) batch.Add(d.Row, d.Path, d.Doc, d.BytesRead, d.Ms);
        Assert.Equal(500, batch.Count);
        Assert.Equal(docs.Sum(d => (long)StoredText.BlobLength(d.Doc)), batch.RawBytes);
        long generation;
        using (var before = store.Acquire())
        {
            Assert.Empty(before.Segments);
            generation = before.CommitGeneration;
        }
        var change = batch.Commit();
        using var view = change.View;
        Assert.NotNull(change.NewSegment);
        Assert.Single(view.Segments);
        Assert.Equal(change.NewSegment, view.Segments[0].Id);
        for (int i = 0; i < shuffled.Count; i++)
        {
            Assert.Equal(new DocRef(0, change.LocalOfAdded[i]), view.Find(shuffled[i].Row.Key));
            Assert.Equal(shuffled[i].Path, view.Segments[0].ReadPath(change.LocalOfAdded[i]));
        }
        Assert.Empty(change.Remaps);
        Assert.Empty(change.DroppedSegments);
        Assert.True(view.CommitGeneration > generation);
        Assert.Throws<InvalidOperationException>(() => batch.Add(docs[0].Row, "x", docs[0].Doc));
    }

    [Fact]
    public void A_key_added_again_replaces_the_old_document_and_a_batch_keeps_its_last_addition()
    {
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [Doc(1, "alpha old"), Doc(2, "beta"), Doc(3, "gamma")]).View.Dispose();

        using var batch = store.BeginBatch();
        int first = batch.Add(Doc(1, "alpha middle").Row, "Docs\\1.txt", new StoredDoc("alpha middle", [], null));
        int second = batch.Add(Doc(1, "alpha new").Row, "Docs\\1.txt", new StoredDoc("alpha new", [], null));
        var change = batch.Commit();
        using var view = change.View;
        Assert.Equal(-1, change.LocalOfAdded[first]);
        Assert.True(change.LocalOfAdded[second] >= 0);

        var live = Synth.Live(view);
        Assert.Equal(3, live.Count);
        var (r, _) = live[1];
        Assert.Equal("alpha new", view.Segments[r.Segment].ReadStored(r.Doc)!.Body);
        Assert.True(view.IsDeleted(new DocRef(0, 0))); // the old one, in the first segment
        Assert.Equal(new DocRef(1, 0), view.Find(1));
        Assert.Equal(3, view.Stats[ContentKind.Text].Docs);
    }

    [Fact]
    public void Deletes_alone_write_only_the_manifest()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        long first;
        using (var c = Synth.Commit(store, [Doc(1, "a b c"), Doc(2, "d e f"), Doc(3, "g h i")]).View) first = c.CommitGeneration;
        var segments = temp.Names().Where(n => n.StartsWith("seg-")).ToList();

        var change = Synth.Commit(store, [], [new DocRef(0, 1)]);
        using var view = change.View;
        Assert.Null(change.NewSegment);
        Assert.Equal(segments, temp.Names().Where(n => n.StartsWith("seg-")).ToList());
        Assert.True(view.IsDeleted(new DocRef(0, 1)));
        Assert.Null(view.Find(2));
        Assert.Equal(2, view.Stats[ContentKind.Text].Docs);
        Assert.Equal([1UL, 3UL], view.RowsByKey().Select(r => r.Row.Key));
        Assert.True(view.CommitGeneration > first);

        // Deleting again, or deleting nothing, changes nothing (no write).
        var again = Synth.Commit(store, [], [new DocRef(0, 1)]);
        Assert.Equal(view.CommitGeneration, again.View.CommitGeneration);
        again.View.Dispose();
    }

    [Fact]
    public void Statistics_follow_deletes_and_replacements()
    {
        using var store = ContentStore.Open(null);
        var docs = Synth.Docs(300, seed: 9);
        Synth.Commit(store, docs).View.Dispose();
        var deleted = docs.Where((_, i) => i % 3 == 0).ToList();
        using (var v = store.Acquire())
        {
            using var b = store.BeginBatch();
            foreach (var d in deleted) b.Delete(v.Find(d.Row.Key)!.Value);
            b.Commit().View.Dispose();
        }
        using var view = store.Acquire();
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var live = docs.Except(deleted).Where(d => d.Row.Kind == kind).ToList();
            var s = view.Stats.GetValueOrDefault(kind);
            Assert.Equal(live.Count, s.Docs);
            Assert.Equal(live.Count(d => ContentTokenizer.HasWords(d.Doc.Body)), s.WithText);
            Assert.Equal(live.Sum(d => (long)StoredText.BlobLength(d.Doc)), s.RawTextBytes);
            Assert.Equal(live.Count(d => d.Row.Status == DocStatus.Damaged), s.Problems);
        }
    }

    [Fact]
    public void Volumes_and_the_recorded_mode_persist()
    {
        using var temp = new TempFolder();
        ushort a, b;
        using (var store = ContentStore.Open(temp.Path))
        {
            Assert.Equal(ContentMode.NamesOnly, store.RecordedMode);
            a = store.VolumeIndex("vol:{AAAA}", "C:\\");
            b = store.VolumeIndex("unc:\\\\NAS\\JOBS", "\\\\nas\\jobs");
            Assert.NotEqual(a, b);
            Assert.Equal(a, store.VolumeIndex("VOL:{aaaa}", "C:\\"));
            Assert.Equal(b, store.VolumeIndex("unc:\\\\nas\\jobs", "Z:\\")); // display root follows the mapping
            store.SeenVolume(b, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            store.RecordMode(ContentMode.DocumentsAndPictures);
            using var change = Synth.Commit(store, [Doc(1, "on the nas", volume: b)]).View;
            Assert.Equal(2, change.Volumes.Count);
        }
        using (var store = ContentStore.Open(temp.Path, readOnly: true))
        {
            Assert.Equal(ContentMode.DocumentsAndPictures, store.RecordedMode);
            using var view = store.Acquire();
            Assert.Equal(new[] { (a, "vol:{AAAA}", "C:\\"), (b, "unc:\\\\NAS\\JOBS", "Z:\\") }, view.Volumes.Select(v => (v.Index, v.Key, v.DisplayRoot)));
            Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), view.Volumes.Single(v => v.Index == b).LastSeenUtc);
            Assert.Equal(b, store.VolumeIndex("unc:\\\\NAS\\JOBS", "Z:\\"));
            Assert.Throws<InvalidOperationException>(() => store.VolumeIndex("vol:{NEW}", "D:\\"));
            Assert.Equal(b, view.Segments[0].Row(0).Volume);
        }
    }

    [Fact]
    public void A_mode_change_alone_is_committed_by_an_empty_commit()
    {
        using var temp = new TempFolder();
        using (var store = ContentStore.Open(temp.Path))
        {
            long before = store.Generation;
            store.RecordMode(ContentMode.Documents);
            var change = Synth.Commit(store, []);
            long committed = change.View.CommitGeneration;
            Assert.True(committed > before);
            change.View.Dispose();
            var none = Synth.Commit(store, []); // nothing pending: no write
            Assert.Equal(committed, none.View.CommitGeneration);
            none.View.Dispose();
        }
        using var reopened = ContentStore.Open(temp.Path);
        Assert.Equal(ContentMode.Documents, reopened.RecordedMode);
    }

    [Fact]
    public void A_reopened_store_has_the_same_documents_terms_and_statistics()
    {
        using var temp = new TempFolder();
        var docs = Synth.Docs(2000, seed: 11);
        IReadOnlyDictionary<ContentKind, KindStats> stats;
        long generation;
        using (var store = ContentStore.Open(temp.Path))
        {
            Synth.Commit(store, docs.Take(1200)).View.Dispose();
            Synth.Commit(store, docs.Skip(1200)).View.Dispose();
            using (var v = store.Acquire()) Synth.Commit(store, [], [v.Find(docs[5].Row.Key)!.Value]).View.Dispose();
            using var view = store.Acquire();
            stats = view.Stats;
            generation = view.CommitGeneration;
        }
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(temp.File("manifest")), out var manifest));
        Assert.Equal(generation, manifest!.Generation); // a commit writes its own generation
        using var again = ContentStore.Open(temp.Path);
        using var reopened = again.Acquire();
        Assert.True(reopened.CommitGeneration >= generation); // opened again: the manifest's, or above when the process went past it
        Assert.Equal(stats.OrderBy(k => k.Key), reopened.Stats.OrderBy(k => k.Key));
        var live = Synth.Live(reopened);
        Assert.Equal(docs.Count - 1, live.Count);
        Assert.False(live.ContainsKey(docs[5].Row.Key));
        foreach (var d in docs.Where((_, i) => i % 50 == 1))
        {
            var (r, _) = live[d.Row.Key];
            Synth.SameDoc(d.Doc, reopened.Segments[r.Segment].ReadStored(r.Doc));
        }
    }

    [Fact]
    public void A_copy_keeps_the_compressed_text_and_picture_under_a_new_key_and_path()
    {
        using var store = ContentStore.Open(null);
        var picture = Synth.Picture(new Random(2));
        var original = new SynthDoc(Synth.Row(100, ContentKind.Picture, DocStatus.NoText), "Pictures\\a.jpg",
            new StoredDoc("", [(DocField.Keywords, "sunset; beach")], picture));
        var big = Doc(50, string.Concat(Enumerable.Range(0, 2000).Select(i => $"word{i % 300} ")));
        var fillers = Enumerable.Range(0, 10).Select(i => Doc(1000 + (ulong)i, $"filler {i}")).ToList();
        Synth.Commit(store, [original, big, .. fillers]).View.Dispose();

        using var view = store.Acquire();
        var src = view.Find(50)!.Value;
        var rawBefore = view.Segments[src.Segment].ReadStoredRaw(src.Doc).ToArray();
        using var batch = store.BeginBatch();
        int copy = batch.AddCopy(src, Synth.Row(60, ContentKind.Text), "Moved\\big.txt");
        batch.Delete(src);
        var change = batch.Commit();
        using var after = change.View;
        Assert.Empty(change.Remaps); // 1 of 12 documents: a plain copy, no rewrite
        var moved = after.Find(60)!.Value;
        Assert.Equal(new DocRef(after.Segments.Count - 1, change.LocalOfAdded[copy]), moved);
        Assert.Null(after.Find(50));
        Assert.Equal(rawBefore, after.Segments[moved.Segment].ReadStoredRaw(moved.Doc).ToArray());
        Assert.Equal("Moved\\big.txt", after.Segments[moved.Segment].ReadPath(moved.Doc));
        Assert.True(after.Segments[moved.Segment].TryGetTerm("word299"u8, out _));

        // A picture's record goes with its copy.
        var p = after.Find(100)!.Value;
        using var b2 = store.BeginBatch();
        b2.AddCopy(p, Synth.Row(101, ContentKind.Picture, DocStatus.NoText), "Pictures\\b.jpg");
        b2.Delete(p);
        using var v2 = b2.Commit().View;
        var q = v2.Find(101)!.Value;
        Assert.True(v2.Segments[q.Segment].TryGetPicture(q.Doc, out var info));
        Assert.Equal(Synth.Q(picture), info);
        Assert.Equal(DocFlags.HasPicture, v2.Segments[q.Segment].Row(q.Doc).Flags & DocFlags.HasPicture);
    }

    [Fact]
    public void A_big_rekey_rewrites_its_segment_once_in_the_same_commit()
    {
        using var store = ContentStore.Open(null);
        var docs = Enumerable.Range(0, 100).Select(i => Doc(1000 + (ulong)i * 10, $"document number {i} text{i}")).ToList();
        Synth.Commit(store, docs).View.Dispose();
        Synth.Commit(store, [Doc(5, "other segment")]).View.Dispose();

        using var view = store.Acquire();
        uint oldId = view.Segments[0].Id;
        using var batch = store.BeginBatch();
        var positions = new Dictionary<int, ulong>();
        for (int i = 0; i < 40; i++) // 40% of the first segment's documents move to a renamed folder
        {
            var src = view.Find(docs[i].Row.Key)!.Value;
            ulong newKey = 500_000 + (ulong)i;
            positions[batch.AddCopy(src, docs[i].Row with { Key = newKey }, $"Renamed\\{i}.txt")] = newKey;
            batch.Delete(src);
        }
        batch.Add(Synth.Row(7), "new.txt", new StoredDoc("brand new", [], null));
        var change = batch.Commit();
        using var after = change.View;

        var (old, remap) = Assert.Single(change.Remaps);
        Assert.Equal(oldId, old);
        Assert.Equal(2, after.Segments.Count); // the rewritten one is gone, its survivors moved into the new one
        Assert.DoesNotContain(after.Segments, s => s.Id == oldId);
        var newSeg = after.Segments.Single(s => s.Id == change.NewSegment);
        int ordinal = after.Segments.ToList().IndexOf(newSeg);
        for (int i = 0; i < 100; i++)
        {
            int oldLocal = view.Find(docs[i].Row.Key)!.Value.Doc;
            if (i < 40)
            {
                Assert.Equal(-1, remap[oldLocal]);
                Assert.Null(after.Find(docs[i].Row.Key));
            }
            else
            {
                Assert.Equal(new DocRef(ordinal, remap[oldLocal]), after.Find(docs[i].Row.Key));
                Assert.Equal(docs[i].Path, newSeg.ReadPath(remap[oldLocal]));
            }
        }
        foreach (var (position, key) in positions) Assert.Equal(new DocRef(ordinal, change.LocalOfAdded[position]), after.Find(key));
        Assert.Equal(102, Synth.Live(after).Count);
        for (int d = 0; d < newSeg.DocCount; d++) Assert.False(after.IsDeleted(new DocRef(ordinal, d)));
    }

    [Fact]
    public void Wholly_deleted_segments_are_dropped_at_commit()
    {
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [Doc(1, "one"), Doc(2, "two")]).View.Dispose();
        Synth.Commit(store, [Doc(3, "three")]).View.Dispose();
        var change = Synth.Commit(store, [], [new DocRef(0, 0), new DocRef(0, 1)]);
        using var view = change.View;
        Assert.Single(view.Segments);
        Assert.Single(change.DroppedSegments);
        Assert.Equal([3UL], view.RowsByKey().Select(r => r.Row.Key));
    }

    [Fact]
    public void One_writer_batch_at_a_time_and_read_only_stores_refuse_writes()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        using (var batch = store.BeginBatch())
        {
            Assert.Throws<InvalidOperationException>(() => store.BeginBatch());
            Assert.Throws<InvalidOperationException>(() => store.Merge(new MergePlan([1], 1, false), CancellationToken.None));
            Assert.Throws<ArgumentException>(() => batch.AddCopy(new DocRef(0, 0), Synth.Row(1), "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.Delete(new DocRef(3, 0)));
        }
        store.BeginBatch().Dispose(); // the disposed batch freed the slot
        Synth.Commit(store, [Doc(1, "x")]).View.Dispose();

        using var reader = ContentStore.Open(temp.Path, readOnly: true);
        Assert.True(reader.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => reader.BeginBatch());
        Assert.Throws<InvalidOperationException>(() => reader.DeleteAll());
        Assert.Throws<InvalidOperationException>(() => reader.RecordMode(ContentMode.Documents));
        Assert.Null(reader.PlanMerge(true));
        using var view = reader.Acquire();
        Assert.Single(Synth.Live(view));
    }

    [Fact]
    public void A_plain_commit_never_rewrites_a_segment_bigger_than_the_in_commit_limit()
    {
        using var store = ContentStore.Open(null);
        int size = ContentStore.RewriteInCommitDocs + 800;
        var docs = Enumerable.Range(0, size).Select(i => Doc(1_000_000 + (ulong)i * 10, $"document {i}")).ToList();
        Synth.Commit(store, docs).View.Dispose();

        // A third of its documents copied away in one batch: plain copies, no rewrite inside Commit (no ct, no pace).
        using var view = store.Acquire();
        uint id = view.Segments[0].Id;
        using var batch = store.BeginBatch();
        for (int i = 0; i < size / 3 + 1; i++)
        {
            var src = view.Find(docs[i].Row.Key)!.Value;
            batch.AddCopy(src, docs[i].Row with { Key = 50_000_000 + (ulong)i }, $"Moved\\{i}.txt");
            batch.Delete(src);
        }
        var change = batch.Commit();
        using var after = change.View;
        Assert.Empty(change.Remaps);
        Assert.Contains(after.Segments, s => s.Id == id);
        Assert.Equal(size, Synth.Live(after).Count);
        // The 30%-deleted rule rewrites it later, on the merge path (gated, cancellable).
        Assert.Equal([id], store.PlanMerge(allowLarge: false)!.Segments);
    }

    /// <summary>docs[i] for i in 0..count, keys spread out, in one segment.</summary>
    static List<SynthDoc> OneSegment(ContentStore store, int count, ulong first)
    {
        var docs = Enumerable.Range(0, count).Select(i => Doc(first + (ulong)i * 10, $"document number {i} text{i}")).ToList();
        Synth.Commit(store, docs).View.Dispose();
        return docs;
    }

    [Fact]
    public void A_big_rekey_rewrites_its_segment_once_under_ct_and_pace()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        var docs = OneSegment(store, 10_000, 1_000_000);
        Synth.Commit(store, [Doc(7_777_777, "the other segment")]).View.Dispose();

        using var view = store.Acquire();
        var seg = view.Segments[0];
        var moves = new List<(int Doc, DocRow Row, string Path)>();
        for (int i = 0; i < 5000; i++) // half of it: a folder rename
        {
            int doc = view.Find(docs[i].Row.Key)!.Value.Doc;
            ulong key = i == 0 ? 7_777_777 : 90_000_000 + (ulong)i; // the first lands on the other segment's key: that one goes
            moves.Add((doc, docs[i].Row with { Key = key }, $"Renamed\\{i}.txt"));
        }
        var rawBefore = seg.ReadStoredRaw(moves[10].Doc).ToArray();
        int paced = 0;
        var change = store.Rekey(seg.Id, moves, allowLarge: false, CancellationToken.None, _ => paced++);
        Assert.NotNull(change);
        using var after = change!.View;
        Assert.True(paced > 0);

        var (old, remap) = Assert.Single(change.Remaps);
        Assert.Equal(seg.Id, old);
        Assert.DoesNotContain(after.Segments, s => s.Id == seg.Id);
        var newSeg = after.Segments.Single(s => s.Id == change.NewSegment);
        int at = after.Segments.ToList().IndexOf(newSeg);
        Assert.Equal(10_000, newSeg.DocCount); // survivors and moved documents in one segment
        for (int i = 0; i < moves.Count; i++)
        {
            Assert.Equal(-1, remap[moves[i].Doc]);
            Assert.Equal(new DocRef(at, change.LocalOfAdded[i]), after.Find(moves[i].Row.Key));
            if (i % 500 == 0) Assert.Equal(moves[i].Path, newSeg.ReadPath(change.LocalOfAdded[i]));
        }
        Assert.Equal(rawBefore, newSeg.ReadStoredRaw(change.LocalOfAdded[10]).ToArray()); // text copied, not compressed again
        for (int i = 5000; i < 10_000; i += 97) Assert.Equal(new DocRef(at, remap[view.Find(docs[i].Row.Key)!.Value.Doc]), after.Find(docs[i].Row.Key));
        Assert.Null(after.Find(docs[0].Row.Key));
        Assert.Equal(10_000, Synth.Live(after).Count); // the other segment's document lost its key to a moved one
        Assert.True(newSeg.TryGetTerm("text42"u8, out _));
    }

    [Fact]
    public void A_rekey_waits_for_allow_large_skips_gone_documents_and_cancels_cleanly()
    {
        var saved = ContentStore.LargeRekeyDocs;
        ContentStore.LargeRekeyDocs = 100;
        try
        {
            using var temp = new TempFolder();
            using var store = ContentStore.Open(temp.Path);
            var docs = OneSegment(store, 3000, 1000);
            using var view = store.Acquire();
            uint id = view.Segments[0].Id;
            var moves = Enumerable.Range(0, 1200).Select(i => (Doc: i, Row: docs[i].Row with { Key = 9_000_000 + (ulong)i }, Path: $"New\\{i}.txt")).ToList();

            long generation = store.Generation;
            Assert.Null(store.Rekey(id, moves, allowLarge: false, CancellationToken.None)); // large: wait for idle and mains power
            Assert.Null(store.Rekey(id + 1_000_000, moves, allowLarge: true, CancellationToken.None)); // gone
            using var cts = new CancellationTokenSource();
            int paced = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => store.Rekey(id, moves, allowLarge: true, cts.Token, ct =>
            {
                paced++;
                cts.Cancel(); // Stop, half-way through the build
                ct.ThrowIfCancellationRequested();
            }));
            Assert.Equal(1, paced);
            Assert.Equal(generation, store.Generation);
            Assert.DoesNotContain(temp.Names(), n => n.EndsWith(".tmp"));

            // A document deleted since the caller looked is left out.
            Synth.Commit(store, [], [new DocRef(0, 3)]).View.Dispose();
            var change = store.Rekey(id, moves, allowLarge: true, CancellationToken.None)!;
            using var after = change.View;
            Assert.Equal(-1, change.LocalOfAdded[3]);
            Assert.True(change.LocalOfAdded.Where((_, i) => i != 3).All(l => l >= 0));
            Assert.Equal(2999, Synth.Live(after).Count);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var batch = store.BeginBatch();
                store.Rekey(change.NewSegment!.Value, [], allowLarge: true, CancellationToken.None);
            });
        }
        finally
        {
            ContentStore.LargeRekeyDocs = saved;
        }
    }

    [Fact]
    public void A_body_past_the_term_cap_keeps_its_field_terms()
    {
        // An exported list of ids: 110,000 distinct words, under the 1M-character text limit.
        string body = string.Join(',', Enumerable.Range(0, 110_000).Select(i => $"id{i:D6}"));
        var big = new SynthDoc(Synth.Row(1, ContentKind.Text), "ids.csv",
            new StoredDoc(body, [(DocField.Title, "Zanzibarquarterly"), (DocField.Keywords, "ledger; exports")], null));
        var small = new SynthDoc(Synth.Row(2, ContentKind.Text), "small.csv", new StoredDoc("a few words", [(DocField.Title, "Zanzibarquarterly")], null));

        using var store = ContentStore.Open(null);
        Synth.Commit(store, [big, small]).View.Dispose();
        Synth.Commit(store, [Doc(3, "another segment")]).View.Dispose();
        List<uint> ids;
        using (var view = store.Acquire())
        {
            Check(view.Segments[0]);
            ids = [.. view.Segments.Select(s => s.Id)];
        }
        // A merge makes the terms again from the stored text, the same way.
        using var merged = store.Merge(new MergePlan(ids, 3, false), CancellationToken.None).View;
        Check(Assert.Single(merged.Segments));

        static void Check(IContentSegment seg)
        {
            string title = ContentTokenizer.FieldPrefix(DocField.Title), keywords = ContentTokenizer.FieldPrefix(DocField.Keywords);
            foreach (var term in new[] { "zanzibarquarterly", title + "zanzibarquarterly" })
            {
                Assert.True(seg.TryGetTerm(Synth.Utf8(term), out var p), term);
                Assert.Equal([0, 1], Synth.DocsOf(seg, p).Where(d => d <= 1));
            }
            Assert.True(seg.TryGetTerm(Synth.Utf8(keywords + "ledger"), out _));
            Assert.True(seg.TryGetTerm(Synth.Utf8("exports"), out _));
            // The body stops at the cap: its first ids are terms, its last ones not.
            Assert.True(seg.TryGetTerm("id000000"u8, out _));
            Assert.False(seg.TryGetTerm("id109999"u8, out _));
        }
    }

    [Fact]
    public void Bytes_on_disk_count_segments_and_manifests()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        Assert.Equal(0, store.BytesOnDisk);
        Synth.Commit(store, Synth.Docs(100, seed: 4)).View.Dispose();
        long files = Directory.EnumerateFiles(temp.Path).Where(f => !f.EndsWith("writer.lock")).Sum(f => new FileInfo(f).Length);
        Assert.Equal(files, store.BytesOnDisk);
    }
}
