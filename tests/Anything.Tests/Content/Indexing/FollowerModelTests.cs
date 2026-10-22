using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// Random steps against a ground truth: after every step and a settle, every file in scope on a readable root has exactly
/// one document with its key, its words and its stamp, bound to its current id; and no document remains for a file that
/// is gone, unless its volume's root isn't Ready and complete (an unplugged stick, an offline share, a root rescanning).
/// </summary>
public class FollowerModelTests(ITestOutputHelper output)
{
    const string Me = @"C:\Users\Me", Usb = "E:", Nas = @"\\nas\share";

    sealed class Model
    {
        public readonly Harness H;
        public readonly Random R;
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = [];
        public bool UsbPlugged = true, UsbScanning, NasOnline = true;
        public string DocsName = "Docs", PicsParent = Me;
        int _names;

        /// <param name="rewrites">
        /// 1: the store rewrites a segment a commit's copies mostly move, as the real one does for small segments; 2: also
        /// big re-keys in place at any size. The remapped changes get the same model checks.
        /// </param>
        public Model(int seed, int rewrites = 0)
        {
            R = new Random(seed);
            H = new Harness(new ContentSettings
            {
                Mode = ContentMode.Documents,
                ModeExplicit = true,
                UseMyFolders = false,
                Folders = [Me, Usb + "\\", Nas + @"\Jobs"],
            }, new FakeStore { MergeAt = 6, RewriteCopiedSegments = rewrites > 0 },
                rewrites > 1 ? new ContentIndexOptions { RekeyInPlaceAbove = 0 } : null);
            foreach (var f in Folders()) H.Folder(f);
            for (int i = 0; i < 40; i++) AddFile();
            H.Start();
            Settle();
        }

        public IEnumerable<string> Folders()
        {
            yield return Me;
            yield return Me + "\\" + DocsName;
            yield return Me + "\\" + DocsName + @"\Old";
            yield return PicsParent + @"\Pics";
            yield return @"C:\Other";
            if (UsbPlugged) { yield return Usb; yield return Usb + @"\Work"; }
            if (NasOnline) { yield return Nas + @"\Jobs"; yield return Nas + @"\Jobs\2024"; yield return Nas + @"\Private"; }
        }

        static readonly string[] Exts = ["txt", "pdf", "docx", "md", "mp3"];

        string NewName() => $"f{_names++}.{Exts[R.Next(Exts.Length)]}";

        public string AddFile(string? folder = null)
        {
            var folders = Folders().ToList();
            folder ??= folders[R.Next(folders.Count)];
            string path = folder + "\\" + NewName();
            H.File(path, "words " + R.Next(1000) + " body " + path.Length);
            Files.Add(path);
            return path;
        }

        public bool Writable(string path)
        {
            string root = Harness.RootOf(path);
            if (root.Equals(Usb, StringComparison.OrdinalIgnoreCase)) return UsbPlugged && !UsbScanning;
            if (root.Equals(Nas, StringComparison.OrdinalIgnoreCase)) return NasOnline;
            return true;
        }

        public string? Pick() => Files.Where(Writable).OrderBy(_ => R.Next()).FirstOrDefault();

        public static bool InScope(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.');
            if (ext is not ("txt" or "pdf" or "docx" or "md")) return false;
            return path.StartsWith(Me + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(Usb + "\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(Nas + @"\Jobs\", StringComparison.OrdinalIgnoreCase);
        }

        public bool Readable(string path)
        {
            string root = Harness.RootOf(path);
            if (root.Equals(Usb, StringComparison.OrdinalIgnoreCase)) return UsbPlugged;
            if (root.Equals(Nas, StringComparison.OrdinalIgnoreCase)) return NasOnline;
            return true;
        }

        /// <summary>Whether a stale document of this volume may be kept: its root isn't Ready and complete.</summary>
        public bool MayKeep(string volumeKey)
        {
            if (volumeKey.Equals("path:E:", StringComparison.OrdinalIgnoreCase)) return !UsbPlugged || UsbScanning;
            if (volumeKey.StartsWith("unc:", StringComparison.OrdinalIgnoreCase)) return !NasOnline;
            return false;
        }

        public void Settle()
        {
            H.Run();
            foreach (int ms in (int[])[6000, 11_000, 3000, 660_000, 6000, 11_000, 3000])
            {
                H.Clock.Advance(ms);
                H.Run();
            }
        }
    }

    [Theory]
    [InlineData(20261021, 10_000, 0)]
    [InlineData(7, 3000, 0)]
    [InlineData(4242, 3000, 0)]
    [InlineData(31337, 3000, 0)]
    [InlineData(20261021, 3000, 1)]
    [InlineData(5, 3000, 1)]
    [InlineData(20261021, 3000, 2)]
    [InlineData(99, 3000, 2)]
    public void Random_steps_match_the_ground_truth(int seed, int steps, int rewrites)
    {
        var m = new Model(seed, rewrites);
        try
        {
            for (int step = 0; step < steps; step++)
            {
                // Now and then the store fails a commit (a full disk for a moment): nothing may be lost; the try after 10 s
                // falls within the settle.
                if (m.R.Next(25) == 0) m.H.Store.CommitFailures = 1;
                string op = Step(m);
                m.Log.Add($"{step}: {op}");
                m.Settle();
                Verify(m, step);
            }
            output.WriteLine($"files {m.Files.Count}, reads {m.H.Files.Reads}, reconciles {m.H.Follower.FullReconciles}, commits {m.H.Store.CommitCount}, rewrites {m.H.Store.Rewrites}, in-place re-keys {m.H.Store.RekeyCount}, max lock {m.H.Follower.MaxReadLockMs} ms");
        }
        catch
        {
            foreach (var line in m.Log.TakeLast(25)) output.WriteLine(line);
            throw;
        }
        finally { m.H.Dispose(); }
    }

    static string Step(Model m)
    {
        var h = m.H;
        int dice = m.R.Next(100);
        // Keep the tree between about 100 and 300 files: more adds when small, more deletes when large.
        if (m.Files.Count > 300 && dice < 18) dice = 35;
        if (dice < 18 || m.Files.Count < 100 && dice < 40) return "add " + m.AddFile();
        if (dice < 30 && m.Pick() is { } changed)
        {
            h.Change(changed, "changed " + m.R.Next(100_000));
            return "change " + changed;
        }
        if (dice < 40 && m.Pick() is { } gone)
        {
            h.Delete(gone);
            m.Files.Remove(gone);
            return "delete " + gone;
        }
        if (dice < 48 && m.Pick() is { } from)
        {
            string folder = Path.GetDirectoryName(from)!.TrimEnd('\\');
            var folders = m.Folders().Where(f => Harness.RootOf(f).Equals(Harness.RootOf(from), StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.R.Next(2) == 0) folder = folders[m.R.Next(folders.Count)];
            string to = folder + "\\r" + m.R.Next(1_000_000) + Path.GetExtension(from);
            h.Rename(from, to);
            m.Files.Remove(from);
            m.Files.Add(to);
            return $"rename {from} → {to}";
        }
        if (dice < 52)
        {
            // Folder rename (PathVersion): Docs ↔ Papers, everything below re-keyed.
            string oldPath = Me + "\\" + m.DocsName;
            m.DocsName = m.DocsName == "Docs" ? "Papers" : "Docs";
            string newPath = Me + "\\" + m.DocsName;
            var moved = m.Files.Where(f => f.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            h.Rename(oldPath, newPath);
            foreach (var f in moved) { m.Files.Remove(f); m.Files.Add(newPath + f[oldPath.Length..]); }
            return $"rename folder {oldPath} → {newPath}";
        }
        if (dice < 55)
        {
            // A folder moved out of scope and back.
            string oldPath = m.PicsParent + @"\Pics";
            m.PicsParent = m.PicsParent == Me ? @"C:\Other" : Me;
            string newPath = m.PicsParent + @"\Pics";
            var moved = m.Files.Where(f => f.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            h.Rename(oldPath, newPath);
            foreach (var f in moved) { m.Files.Remove(f); m.Files.Add(newPath + f[oldPath.Length..]); }
            return $"move folder {oldPath} → {newPath}";
        }
        if (dice < 60)
        {
            h.Compact();
            return "compact";
        }
        if (dice < 63 && m.UsbPlugged && m.NasOnline && !m.UsbScanning)
        {
            // Clear (Rebuild Index): every entry comes back under new ids with the same stamps.
            h.Index.Clear();
            h.ForgetIds();
            foreach (var f in m.Folders()) h.Folder(f);
            foreach (var f in m.Files) h.Readd(f);
            return "clear";
        }
        if (dice < 66)
        {
            h.Index.Configure(dateAccessed: !h.Index.HasDateAccessed, folderSizes: false, recentChanges: true);
            return "log reset";
        }
        if (dice < 70 && m.UsbPlugged && !m.UsbScanning)
        {
            Rescan(m, Usb, deleteOne: false);
            return "rescan E:";
        }
        if (dice < 73 && m.UsbPlugged && !m.UsbScanning)
        {
            // A file deleted while the app was off, seen by a rescan; orphans wait for the root to be complete.
            h.Gate.Set(Usb, RootStatus.Scanning);
            m.UsbScanning = true;
            string? deleted = Rescan(m, Usb, deleteOne: true);
            return "rescan E: while scanning, deleted " + deleted;
        }
        if (dice < 76 && m.UsbScanning)
        {
            h.Gate.Set(Usb, RootStatus.Ready);
            m.UsbScanning = false;
            return "E: complete";
        }
        if (dice < 79 && m.UsbPlugged && !m.UsbScanning)
        {
            // Unplugged: the root and all its ids die; its documents are kept unbound.
            h.Index.Remove(h.Id(Usb));
            foreach (var p in h.Under(Usb).Append(Usb).ToList()) h.Forget(p);
            h.Gate.Set(Usb, (RootGateState?)null);
            m.UsbPlugged = false;
            return "unplug E:";
        }
        if (dice < 82 && !m.UsbPlugged)
        {
            m.UsbPlugged = true;
            h.Gate.Set(Usb, RootStatus.Ready);
            foreach (var f in m.Folders().Where(f => f.StartsWith(Usb, StringComparison.OrdinalIgnoreCase))) h.Folder(f);
            foreach (var f in m.Files.Where(f => f.StartsWith(Usb, StringComparison.OrdinalIgnoreCase))) h.Readd(f);
            return "replug E:";
        }
        if (dice < 85 && m.NasOnline)
        {
            m.NasOnline = false;
            h.Gate.Set(Nas, RootStatus.Offline);
            h.Files.OfflineRoots.Add(Nas);
            return "NAS offline";
        }
        if (dice < 88 && !m.NasOnline)
        {
            m.NasOnline = true;
            h.Gate.Set(Nas, RootStatus.Ready);
            h.Files.OfflineRoots.Remove(Nas);
            return "NAS online";
        }
        if (dice < 92 && m.Pick() is { } safe)
        {
            h.Delete(safe);
            h.File(safe, "saved again " + m.R.Next(1000));
            return "safe-save " + safe;
        }
        if (dice < 94 && m.UsbPlugged && !m.UsbScanning && m.Pick() is { } cross
            && Harness.RootOf(cross).Equals("C:", StringComparison.OrdinalIgnoreCase))
        {
            // Moved to the stick: dead on one root, new on the other, same name, size and date.
            var (size, modified, body, _) = h.Files.Get(cross);
            string to = Usb + @"\Work\" + Path.GetFileName(cross);
            if (m.Files.Contains(to)) return "skip";
            h.Delete(cross);
            m.Files.Remove(cross);
            h.File(to, body, size, modified);
            m.Files.Add(to);
            return $"move across volumes {cross} → {to}";
        }
        if (dice >= 94) return SeveralRenames(m);
        return "add " + m.AddFile();
    }

    /// <summary>Several renames in one step: onto names just freed (a chain), a swap, a series shifted, a rename with an edit, out of scope and back.</summary>
    static string SeveralRenames(Model m)
    {
        var h = m.H;
        var mine = m.Files.Where(f => Harness.RootOf(f).Equals("C:", StringComparison.OrdinalIgnoreCase)).OrderBy(_ => m.R.Next()).ToList();
        if (mine.Count < 4) return "add " + m.AddFile();
        void Move(string from, string to)
        {
            h.Rename(from, to);
            m.Files.Remove(from);
            m.Files.Add(to);
        }
        string Fresh(string like) => Path.GetDirectoryName(like)! + "\\r" + m.R.Next(1_000_000) + Path.GetExtension(like);
        switch (m.R.Next(5))
        {
            case 0:
            {
                // c → d, then a → c: a takes the name c just freed.
                string a = mine[0], c = mine[1], d = Fresh(c);
                if (m.Files.Contains(d)) return "skip";
                Move(c, d);
                Move(a, c);
                return $"chain {c} → {d}, {a} → {c}";
            }
            case 1:
            {
                string a = mine[0], b = mine[1], t = Fresh(a);
                if (m.Files.Contains(t)) return "skip";
                Move(a, t);
                Move(b, a);
                Move(t, b);
                return $"swap {a} ↔ {b}";
            }
            case 2:
            {
                // A series shifted by one: the last onto a fresh name, each other onto the next one's name.
                var series = mine.Take(2 + m.R.Next(3)).ToList();
                string end = Fresh(series[^1]);
                if (m.Files.Contains(end)) return "skip";
                Move(series[^1], end);
                for (int i = series.Count - 2; i >= 0; i--) Move(series[i], series[i + 1]);
                return "shift " + string.Join(" → ", series) + " → " + end;
            }
            case 3:
            {
                string a = mine[0], b = Fresh(a);
                if (m.Files.Contains(b)) return "skip";
                Move(a, b);
                h.Change(b, "renamed and edited " + m.R.Next(100_000));
                return $"rename and edit {a} → {b}";
            }
            default:
            {
                // Out of the scope (a name no kind reads) and back within the delete grace.
                string a = mine[0], bak = a + ".bak";
                if (m.Files.Contains(bak)) return "skip";
                Move(a, bak);
                h.Pass(3000);
                Move(bak, a);
                return $"out of scope and back {a}";
            }
        }
    }

    /// <summary>A whole-root rescan: the root and everything below it removed, the same entries added back with new ids.</summary>
    static string? Rescan(Model m, string root, bool deleteOne)
    {
        var h = m.H;
        string? deleted = null;
        var onRoot = m.Files.Where(f => Harness.RootOf(f).Equals(root, StringComparison.OrdinalIgnoreCase)).ToList();
        if (deleteOne && onRoot.Count > 0)
        {
            deleted = onRoot[m.R.Next(onRoot.Count)];
            h.Files.Remove(deleted);
            m.Files.Remove(deleted);
            onRoot.Remove(deleted);
        }
        h.Index.Remove(h.Id(root));
        foreach (var p in h.Under(root).Append(root).ToList()) h.Forget(p);
        foreach (var f in m.Folders().Where(f => Harness.RootOf(f).Equals(root, StringComparison.OrdinalIgnoreCase))) h.Folder(f);
        foreach (var f in onRoot) h.Readd(f);
        return deleted;
    }

    static void Verify(Model m, int step)
    {
        var h = m.H;
        var live = h.Store.Live();
        var byKey = new Dictionary<ulong, (Anything.Core.Content.Store.DocRow Row, string Path, Anything.Core.Content.Store.StoredDoc Doc)>();
        foreach (var d in live)
        {
            Assert.True(byKey.TryAdd(d.Row.Key, d), $"step {step}: two live documents for {d.Path}");
        }
        var expected = new HashSet<ulong>();
        foreach (var f in m.Files)
        {
            if (!Model.InScope(f)) continue;
            ulong key = Harness.Key(f);
            expected.Add(key);
            if (!m.Readable(f)) continue;
            Assert.True(byKey.TryGetValue(key, out var doc), $"step {step}: no document for {f}");
            var (size, modified, body, _) = h.Files.Get(f);
            Assert.True(doc.Doc.Body == body, $"step {step}: {f} has \"{doc.Doc.Body}\", the file has \"{body}\"");
            Assert.True(doc.Row.Size == size && doc.Row.Modified == modified, $"step {step}: {f} stamp differs");
            Assert.True(h.BoundId(f) == h.Id(f), $"step {step}: {f} bound to {h.BoundId(f)}, its id is {h.Id(f)}");
        }
        foreach (var (key, d) in byKey)
        {
            if (expected.Contains(key)) continue;
            string volume = h.Store.VolumeKey(d.Row.Volume);
            Assert.True(m.MayKeep(volume), $"step {step}: a document for {d.Path} ({volume}) whose file is gone or out of scope");
        }
    }
}
