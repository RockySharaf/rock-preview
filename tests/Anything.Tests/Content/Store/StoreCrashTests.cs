using System.Buffers.Binary;
using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

public class StoreCrashTests
{
    /// <summary>A store with three commits (the second replaces a key of the first, the third deletes) and every version written.</summary>
    sealed class Fixture : IDisposable
    {
        public readonly TempFolder Temp = new();
        public readonly Dictionary<ulong, List<SynthDoc>> Versions = [];
        public readonly List<uint> SegmentIds = [];
        /// <summary>The generation of each of the three commits.</summary>
        public readonly List<long> Generations = [];
        public ulong Replaced, Deleted;

        public Fixture(int perCommit = 150)
        {
            var docs = Synth.Docs(perCommit * 2, seed: 31);
            var first = docs.Take(perCommit).ToList();
            Replaced = first[3].Row.Key;
            var second = docs.Skip(perCommit).Append(first[3] with { Doc = new StoredDoc("the replacement version", [], null) }).ToList();
            using var store = ContentStore.Open(Temp.Path);
            store.VolumeIndex("vol:{C}", "C:\\");
            store.RecordMode(ContentMode.Documents);
            foreach (var d in first.Concat(second)) Add(d);
            Committed(Synth.Commit(store, first));
            Committed(Synth.Commit(store, second));
            using (var v = store.Acquire())
            {
                Deleted = first[7].Row.Key;
                Committed(Synth.Commit(store, [], [v.Find(Deleted)!.Value]));
                SegmentIds.AddRange(v.Segments.Select(s => s.Id));
            }
        }

        void Committed(StoreChange change)
        {
            Generations.Add(change.View.CommitGeneration);
            change.View.Dispose();
        }

        void Add(SynthDoc d)
        {
            if (!Versions.TryGetValue(d.Row.Key, out var list)) Versions[d.Row.Key] = list = [];
            list.Add(d);
        }

        public string Copy()
        {
            var to = Path.Combine(Temp.Path, "copy-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(Temp.Path))
                if (!f.EndsWith("writer.lock")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            return to;
        }

        public void Dispose() => Temp.Dispose();
    }

    /// <summary>
    /// Opens the folder (never throws) and checks that every document it returns is one that was written for its key. With
    /// strict false (damage in TEXT or PATHS, which only the scrub checks: amendment B narrows the test for those two),
    /// text and paths may be wrong but nothing may throw or point outside its segment.
    /// </summary>
    static int Check(string folder, Fixture f, bool readOnly, bool strict = true)
    {
        using var store = ContentStore.Open(folder, readOnly);
        using var view = store.Acquire();
        int seen = 0;
        foreach (var (r, row) in view.RowsByKey())
        {
            Assert.True(f.Versions.TryGetValue(row.Key, out var versions), $"a key nobody wrote: {row.Key}");
            var seg = view.Segments[r.Segment];
            const DocFlags Computed = DocFlags.HasText | DocFlags.HasPicture; // set by the store
            Assert.Contains(versions, v => v.Row with { Flags = v.Row.Flags & ~Computed } == row with { Flags = row.Flags & ~Computed });
            var stored = seg.ReadStored(r.Doc);
            if (stored != null && strict) Assert.Contains(versions, v => v.Doc.Body == stored.Body && v.Doc.Fields.SequenceEqual(stored.Fields));
            var path = seg.ReadPath(r.Doc);
            if (path != null && strict) Assert.Contains(versions, v => v.Path == path);
            seen++;
        }
        foreach (var seg in view.Segments)
            seg.Terms(default, default, (ReadOnlySpan<byte> term, in TermPostings p) =>
            {
                foreach (int d in Synth.DocsOf(seg, p)) Assert.InRange(d, 0, seg.DocCount - 1);
                return true;
            }, CancellationToken.None);
        return seen;
    }

    [Fact]
    public void Truncating_any_file_at_any_4_KB_boundary_never_throws_or_returns_a_wrong_document()
    {
        using var f = new Fixture();
        var files = Directory.EnumerateFiles(f.Temp.Path).Where(p => !p.EndsWith("writer.lock")).Select(Path.GetFileName).ToList();
        Assert.Contains("manifest", files);
        Assert.Contains("manifest.prev", files);
        foreach (var name in files)
        {
            long length = new FileInfo(f.Temp.File(name!)).Length;
            int step = length < 16 << 10 ? 61 : 4096; // manifests are small: cut them finer
            for (long cut = 0; cut < length; cut += step)
            {
                var copy = f.Copy();
                using (var fs = new FileStream(Path.Combine(copy, name!), FileMode.Open)) fs.SetLength(cut);
                Check(copy, f, readOnly: true);
                Check(copy, f, readOnly: false);
            }
        }
    }

    static List<SegmentFormat.TocEntry> Toc(byte[] b)
    {
        int tocLength = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(b.Length - 16));
        int toc = b.Length - 16 - tocLength;
        return [.. Enumerable.Range(0, tocLength / SegmentFormat.TocEntrySize).Select(i => SegmentFormat.TocEntry.Read(b.AsSpan(toc + i * SegmentFormat.TocEntrySize)))];
    }

    [Fact]
    public void Flipping_bits_in_any_section_never_throws_and_checked_sections_never_give_wrong_documents()
    {
        using var f = new Fixture();
        var rnd = new Random(17);
        foreach (uint id in f.SegmentIds)
        {
            string name = SegmentFormat.FileName(id);
            var original = File.ReadAllBytes(f.Temp.File(name));
            var toc = Toc(original);
            int tocStart = original.Length - 16 - toc.Count * SegmentFormat.TocEntrySize;
            var regions = toc.Where(e => e.Length > 0).Select(e => (e.Id.ToString(), e.Offset, e.Length))
                .Append(("Toc", tocStart, toc.Count * SegmentFormat.TocEntrySize)).Append(("Footer", original.Length - 16, 16));
            foreach (var (section, offset, length) in regions)
            {
                var positions = new[] { 0L, length / 2, length - 1 }.Concat(Enumerable.Range(0, 4).Select(_ => (long)rnd.Next((int)length))).Distinct();
                foreach (long at in positions)
                {
                    var copy = f.Copy();
                    var bytes = (byte[])original.Clone();
                    bytes[offset + at] ^= (byte)(1 << rnd.Next(8));
                    File.WriteAllBytes(Path.Combine(copy, name), bytes);
                    bool unchecked_ = section is nameof(SegmentFormat.Section.Text) or nameof(SegmentFormat.Section.Paths);
                    Check(copy, f, readOnly: true, strict: !unchecked_);

                    using var store = ContentStore.Open(copy, readOnly: true);
                    using var view = store.Acquire();
                    var seg = view.Segments.Cast<SegmentReader>().FirstOrDefault(s => s.Id == id);
                    bool lazy = section is nameof(SegmentFormat.Section.Dict) or nameof(SegmentFormat.Section.Postings); // per block, on first use
                    bool checkedAtOpen = !unchecked_ && !lazy;
                    if (checkedAtOpen) Assert.Null(seg);
                    else
                    {
                        Assert.NotNull(seg);
                        if (lazy)
                        {
                            // A dictionary block, or the postings of one, that fails its CRC answers nothing: every term has
                            // exactly its documents or none, never wrong ones.
                            var expected = Synth.Postings(Enumerable.Range(0, seg!.DocCount).Select(d => Version(f, seg.Row(d).Key, id)).ToList());
                            foreach (var (term, docs) in expected)
                            {
                                if (!seg.TryGetTerm(Synth.Utf8(term), out var p)) continue;
                                var got = Synth.DocsOf(seg, p);
                                if (got.Count > 0) Assert.Equal(docs, got);
                            }
                            Assert.True(seg.IsDamaged);
                        }
                        Assert.False(seg!.Verify(CancellationToken.None)); // the scrub finds every flip
                        Assert.True(seg.IsDamaged);
                    }
                }
            }
        }
    }

    /// <summary>The version of a key that went into a segment (the replaced key's first version is in the first segment).</summary>
    static SynthDoc Version(Fixture f, ulong key, uint segment)
    {
        var versions = f.Versions[key];
        return key == f.Replaced && segment != f.SegmentIds[0] ? versions[^1] : versions[0];
    }

    [Fact]
    public void Leftovers_are_swept_by_the_writer_only()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        uint half = f.SegmentIds.Max() + 40, stray = f.SegmentIds.Max() + 50;
        File.WriteAllText(Path.Combine(copy, "manifest.tmp"), "half a manifest");
        File.WriteAllText(Path.Combine(copy, SegmentFormat.FileName(half) + ".tmp"), "half a segment");
        File.Copy(Path.Combine(copy, SegmentFormat.FileName(f.SegmentIds[0])), Path.Combine(copy, SegmentFormat.FileName(stray))); // valid, unreferenced
        Directory.CreateDirectory(Path.Combine(copy, "tmp"));
        File.WriteAllText(Path.Combine(copy, "tmp", "w1.tmp"), "spill");
        var before = Directory.EnumerateFileSystemEntries(copy).Order().ToList();

        int live = Check(copy, f, readOnly: true);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(copy).Order().ToList()); // a reader deletes nothing

        using (var store = ContentStore.Open(copy))
        {
            using var view = store.Acquire();
            Assert.Equal(live, Synth.Live(view).Count);
            Assert.True(store.NextSegmentId > stray); // never reused, even after the sweep
        }
        var after = Directory.EnumerateFileSystemEntries(copy).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("manifest.tmp", after);
        Assert.DoesNotContain(SegmentFormat.FileName(half) + ".tmp", after);
        Assert.DoesNotContain(SegmentFormat.FileName(stray), after);
        Assert.DoesNotContain("tmp", after);
        Assert.Contains("manifest.prev", after);
        foreach (uint id in f.SegmentIds) Assert.Contains(SegmentFormat.FileName(id), after);
    }

    [Fact]
    public void A_bad_manifest_falls_back_to_the_previous_one()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        var path = Path.Combine(copy, "manifest");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x55;
        File.WriteAllBytes(path, bytes);
        Check(copy, f, readOnly: false);
        long committed;
        using (var store = ContentStore.Open(copy))
        {
            using var view = store.Acquire();
            Assert.True(view.CommitGeneration >= f.Generations[1]);
            Assert.NotNull(view.Find(f.Deleted)); // the commit before the delete
            Assert.Equal(ContentMode.Documents, store.RecordedMode);
            // The next commit writes a good manifest over the bad one and keeps manifest.prev loadable.
            using var change = Synth.Commit(store, [], [view.Find(f.Deleted)!.Value]).View;
            committed = change.CommitGeneration;
        }
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(path), out var m));
        Assert.Equal(committed, m!.Generation);
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(Path.Combine(copy, "manifest.prev")), out var prev));
        Assert.Equal(f.Generations[1], prev!.Generation);
        using var again = ContentStore.Open(copy, readOnly: true);
        using var v2 = again.Acquire();
        Assert.Null(v2.Find(f.Deleted));
    }

    [Fact]
    public void With_no_readable_manifest_the_segments_are_recovered_newest_first()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        File.WriteAllText(Path.Combine(copy, "manifest"), "garbage");
        File.Delete(Path.Combine(copy, "manifest.prev"));
        int live = Check(copy, f, readOnly: true);
        Assert.Equal(f.Versions.Count, live); // the lost deletion comes back (reconcile redoes it)
        using (var store = ContentStore.Open(copy))
        {
            using var view = store.Acquire();
            var r = view.Find(f.Replaced)!.Value;
            Assert.Equal("the replacement version", view.Segments[r.Segment].ReadStored(r.Doc)!.Body);
            Assert.Equal(1, Synth.Live(view).Count(x => x.Key == f.Replaced));
            Assert.Equal("vol:{C}", Assert.Single(view.Volumes).Key); // the volume table comes from the segments
            Synth.Commit(store, []).View.Dispose(); // a recovered list is written at the next commit
        }
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(Path.Combine(copy, "manifest")), out var m));
        Assert.Equal(f.SegmentIds, m!.Segments.Select(s => s.Id));
    }

    [Fact]
    public void A_missing_segment_is_left_out_and_dropped_at_the_next_commit()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        File.Delete(Path.Combine(copy, SegmentFormat.FileName(f.SegmentIds[0])));
        Check(copy, f, readOnly: true);
        using var store = ContentStore.Open(copy);
        using (var view = store.Acquire()) Assert.Single(view.Segments);
        Assert.True(store.HasDamage);
        var change = Synth.Commit(store, []);
        Assert.Contains(f.SegmentIds[0], change.DroppedSegments);
        change.View.Dispose();
        Assert.False(store.HasDamage);
    }

    /// <summary>Every file of a folder with its length and time written (the writer's lock left out).</summary>
    static List<(string Name, long Length, DateTime Written)> Snapshot(string folder)
        => [.. Directory.EnumerateFiles(folder).Where(p => !p.EndsWith("writer.lock")).Order()
            .Select(p => (Path.GetFileName(p), new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)))];

    [Fact]
    public void A_manifest_in_use_at_open_is_not_taken_for_damage()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        File.WriteAllText(Path.Combine(copy, "manifest.tmp"), "left by a crash"); // a writer would sweep it
        var before = Snapshot(copy);
        using (new FileStream(Path.Combine(copy, "manifest"), FileMode.Open, FileAccess.Read, FileShare.None)) // a backup tool holds it
        {
            using var store = ContentStore.Open(copy);
            Assert.True(store.IsUnavailable);
            Assert.True(store.IsReadOnly);
            Assert.False(store.HasDamage);
            Assert.Throws<InvalidOperationException>(() => store.BeginBatch());
            Assert.Throws<InvalidOperationException>(() => store.DeleteAll());
            Assert.Null(store.PlanMerge(true));
            using var view = store.Acquire();
            Assert.NotNull(view.Find(f.Deleted)); // shows manifest.prev's commit meanwhile
            Assert.Equal(f.SegmentIds, view.Segments.Select(s => s.Id));
        }
        Assert.Equal(before, Snapshot(copy)); // nothing deleted, swept or written

        // Once it is free, the writer opens with every commit there.
        using (var store = ContentStore.Open(copy))
        {
            Assert.False(store.IsUnavailable);
            Assert.False(store.IsReadOnly);
            using var view = store.Acquire();
            Assert.Null(view.Find(f.Deleted));
            Assert.Equal(f.Versions.Count - 1, Synth.Live(view).Count);
            Assert.Equal("the replacement version", view.Segments[view.Find(f.Replaced)!.Value.Segment].ReadStored(view.Find(f.Replaced)!.Value.Doc)!.Body);
        }
    }

    [Fact]
    public void Manifests_in_use_with_no_readable_fallback_show_nothing_rather_than_deleted_documents()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        File.Delete(Path.Combine(copy, "manifest.prev"));
        var before = Snapshot(copy);
        using (new FileStream(Path.Combine(copy, "manifest"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (bool readOnly in new[] { false, true })
            {
                using var store = ContentStore.Open(copy, readOnly);
                Assert.True(store.IsUnavailable);
                using var view = store.Acquire();
                Assert.Empty(view.Segments); // not the segments recovered without their deletions
            }
        }
        Assert.Equal(before, Snapshot(copy));
    }

    [Fact]
    public void A_segment_in_use_at_open_is_not_taken_for_damage()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        var before = Snapshot(copy);
        uint held = f.SegmentIds[0];
        // Another program has it open for writing, which our Read|Delete-sharing open can't share.
        using (new FileStream(Path.Combine(copy, SegmentFormat.FileName(held)), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            using var store = ContentStore.Open(copy);
            Assert.True(store.IsUnavailable);
            Assert.True(store.IsReadOnly);
            Assert.False(store.HasDamage); // nothing to drop
            using var view = store.Acquire();
            Assert.DoesNotContain(view.Segments, s => s.Id == held);
            Assert.Contains(view.Segments, s => s.Id == f.SegmentIds[1]);
        }
        Assert.Equal(before, Snapshot(copy)); // its manifest entry and its file stay

        using (var store = ContentStore.Open(copy))
        {
            Assert.False(store.IsUnavailable);
            Assert.False(store.HasDamage);
            var change = Synth.Commit(store, []);
            Assert.Empty(change.DroppedSegments);
            using var view = change.View;
            Assert.Equal(f.SegmentIds, view.Segments.Select(s => s.Id));
            Assert.Equal(f.Versions.Count - 1, Synth.Live(view).Count);
        }
    }

    [Fact]
    public void A_manifest_from_a_newer_version_is_never_written_over()
    {
        using var f = new Fixture();
        var copy = f.Copy();
        var path = Path.Combine(copy, "manifest");
        var bytes = File.ReadAllBytes(path);
        bytes[8] = 9; // format 9
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(copy, "manifest.tmp"), "left by the newer build");
        var before = Directory.EnumerateFiles(copy).Order().Select(p => (p, new FileInfo(p).Length)).ToList();

        using (var reader = ContentStore.Open(copy, readOnly: true)) Assert.True(reader.IsNewerFormat);
        using var store = ContentStore.Open(copy);
        Assert.True(store.IsNewerFormat);
        Assert.True(store.IsReadOnly);
        using (var view = store.Acquire()) Assert.Empty(view.Segments);
        Assert.Throws<InvalidOperationException>(() => store.BeginBatch());
        Assert.Null(store.PlanMerge(true));
        Assert.Equal(before, Directory.EnumerateFiles(copy).Where(p => !p.EndsWith("writer.lock")).Order().Select(p => (p, new FileInfo(p).Length)).ToList());

        // "Rebuild text index": the writer may delete it and start over.
        Assert.True(store.DeleteAll());
        Assert.False(store.IsNewerFormat);
        Assert.False(store.IsReadOnly);
        Synth.Commit(store, [f.Versions.Values.First()[0]]).View.Dispose();
        using var v2 = store.Acquire();
        Assert.Single(Synth.Live(v2));
    }

    [Fact]
    public void One_writer_at_a_time_and_the_lock_goes_with_it()
    {
        using var temp = new TempFolder();
        using (var first = ContentStore.Open(temp.Path))
        {
            Assert.False(first.IsReadOnly);
            Assert.True(File.Exists(temp.File("writer.lock")));
            using var second = ContentStore.Open(temp.Path);
            Assert.True(second.IsReadOnly);
            Synth.Commit(first, [new SynthDoc(Synth.Row(1), "a", new StoredDoc("x", [], null))]).View.Dispose();
        }
        Assert.False(File.Exists(temp.File("writer.lock")));
        using var third = ContentStore.Open(temp.Path);
        Assert.False(third.IsReadOnly);
    }

    [Fact]
    public void Segment_numbers_are_never_reused()
    {
        using var temp = new TempFolder();
        uint highest;
        using (var store = ContentStore.Open(temp.Path))
        {
            for (int i = 0; i < 3; i++) Synth.Commit(store, [new SynthDoc(Synth.Row((ulong)i + 1), "a", new StoredDoc("x y", [], null))]).View.Dispose();
            using (var v = store.Acquire()) highest = v.Segments.Max(s => s.Id);
            Assert.True(store.DeleteAll());
            var change = Synth.Commit(store, [new SynthDoc(Synth.Row(9), "b", new StoredDoc("z", [], null))]);
            Assert.True(change.NewSegment > highest);
            highest = change.NewSegment!.Value;
            change.View.Dispose();
        }
        // A stray file with a higher number (a crash between rename and manifest) still pushes the next number past it.
        File.Copy(temp.File(SegmentFormat.FileName(highest)), temp.File(SegmentFormat.FileName(highest + 10)));
        using (var store = ContentStore.Open(temp.Path))
        {
            var change = Synth.Commit(store, [new SynthDoc(Synth.Row(10), "c", new StoredDoc("w", [], null))]);
            Assert.True(change.NewSegment > highest + 10);
            change.View.Dispose();
            using var view = store.Acquire();
            Assert.Equal(2, Synth.Live(view).Count); // the stray segment's documents are not in the index
        }
        // A stray name at the top of the range uses nothing up: it is swept, and numbering goes on.
        string top = temp.File(SegmentFormat.FileName(uint.MaxValue - 1));
        File.Copy(temp.File(SegmentFormat.FileName(highest)), top);
        using (var store = ContentStore.Open(temp.Path))
        {
            Assert.False(File.Exists(top));
            var change = Synth.Commit(store, [new SynthDoc(Synth.Row(11), "d", new StoredDoc("v", [], null))]);
            Assert.True(change.NewSegment > highest + 10 && change.NewSegment < uint.MaxValue / 2);
            change.View.Dispose();
        }
    }

    [Fact]
    public void An_unreadable_folder_opens_as_an_empty_read_only_store()
    {
        using var temp = new TempFolder();
        var file = temp.File("not-a-folder");
        File.WriteAllText(file, "x");
        using var store = ContentStore.Open(Path.Combine(file, "Content"));
        Assert.True(store.IsReadOnly);
        using var view = store.Acquire();
        Assert.Empty(view.Segments);
        using var missing = ContentStore.Open(temp.File("never-created"), readOnly: true);
        Assert.False(Directory.Exists(temp.File("never-created")));
    }

    [Fact]
    public void Random_garbage_in_place_of_every_file_never_throws()
    {
        using var f = new Fixture(perCommit: 40);
        var rnd = new Random(23);
        for (int i = 0; i < 30; i++)
        {
            var copy = f.Copy();
            foreach (var file in Directory.EnumerateFiles(copy))
            {
                if (rnd.Next(3) != 0) continue;
                var junk = new byte[rnd.Next(0, 4096)];
                rnd.NextBytes(junk);
                if (rnd.Next(2) == 0 && junk.Length > 24) "RPCSEG01"u8.CopyTo(junk);
                File.WriteAllBytes(file, junk);
            }
            Check(copy, f, readOnly: rnd.Next(2) == 0);
        }
    }
}
