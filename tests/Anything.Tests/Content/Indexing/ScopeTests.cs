using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

/// <summary>The §3 scope rules, walked over a FileIndex and checked from stored paths, with keys that need no Win32.</summary>
public class ScopeTests
{
    static (string Key, string Prefix)? Resolve(string path)
    {
        var (key, prefix, _) = Harness.Resolve(path);
        return (key, prefix);
    }

    static ContentScope Scope(Action<ContentSettings>? edit = null, bool allRoots = false, IEnumerable<string>? denied = null)
    {
        var s = new ContentSettings { Mode = ContentMode.Documents, ModeExplicit = true, UseMyFolders = false, Folders = [@"C:\Users\Me", @"E:\"] };
        edit?.Invoke(s);
        return ContentScope.Build(s, allRoots, denied ?? [], Resolve);
    }

    /// <summary>Whether a path is in scope by walking a FileIndex built for it, and by its stored path: both must agree.</summary>
    static bool In(ContentScope scope, string path, FileAttributes attributes = FileAttributes.Archive)
    {
        var index = new FileIndex();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Folder(string p)
        {
            if (ids.TryGetValue(p, out int id)) return id;
            int cut = p.LastIndexOf('\\');
            id = Harness.IsRoot(p) ? index.AddRoot(p) : index.Add(p[(cut + 1)..], Folder(p[..cut]), FileAttributes.Directory, -1, 0, 0);
            ids[p] = id;
            return id;
        }
        int c = path.LastIndexOf('\\');
        int file = index.Add(path[(c + 1)..], Folder(path[..c]), attributes, 10, 100, 0);
        var roots = new RootTable();
        var walker = new FolderWalker();
        walker.Reset(index, scope, roots);
        walker.TryKeyOf(file, out _, out _, out _);
        roots.Add(walker.UnknownRoots, Harness.Resolve, (_, _) => 0, scope);
        walker.Reset(index, scope, roots);
        bool walked = walker.TryKeyOf(file, out ulong key, out _, out _);
        var (vk, rel, display) = Harness.Resolve(path);
        bool byPath = scope.InScope(vk, rel, display, out _) && (attributes & FileAttributes.Temporary) == 0;
        Assert.True(walked == byPath, $"{path}: the walk says {walked}, the stored path says {byPath}");
        if (walked) Assert.Equal(ContentKeys.Of(vk, rel), key);
        return walked;
    }

    [Fact]
    public void Added_folders_and_their_kinds_are_in_and_the_rest_out()
    {
        var scope = Scope();
        Assert.True(In(scope, @"C:\Users\Me\a.pdf"));
        Assert.True(In(scope, @"C:\Users\Me\Deep\er\b.docx"));
        Assert.True(In(scope, @"E:\c.txt"));
        Assert.False(In(scope, @"C:\Users\Other\a.pdf"));
        Assert.False(In(scope, @"C:\Users\a.pdf"));
        Assert.False(In(scope, @"D:\a.pdf"));
        Assert.False(In(scope, @"C:\Users\Me\song.mp3"));
        Assert.False(In(scope, @"C:\Users\Me\photo.jpg")); // Documents mode
        Assert.True(In(Scope(s => s.Mode = ContentMode.DocumentsAndPictures), @"C:\Users\Me\photo.jpg"));
    }

    [Fact]
    public void Excluded_folders_by_path_by_name_and_by_wildcard_are_out()
    {
        var scope = Scope(s => s.ExcludeFolders = [@"C:\Users\Me\Private", "Archive", @"C:\Users\*\Secret*"]);
        Assert.False(In(scope, @"C:\Users\Me\Private\a.pdf"));
        Assert.False(In(scope, @"C:\Users\Me\Work\Archive\a.pdf"));
        Assert.False(In(scope, @"E:\Archive\a.pdf"));
        Assert.False(In(scope, @"C:\Users\Me\Secrets\a.pdf"));
        Assert.True(In(scope, @"C:\Users\Me\Privates\a.pdf"));
        Assert.True(In(scope, @"C:\Users\Me\Work\a.pdf"));
    }

    [Fact]
    public void Denied_folders_always_win_even_over_an_added_folder_inside_them()
    {
        var scope = Scope(s => s.Folders.Add(@"C:\Users\Me\AppData\Roaming\Notes"));
        Assert.False(In(scope, @"C:\Users\Me\AppData\Roaming\Notes\n.txt"));
        Assert.False(In(scope, @"C:\Users\Me\code\.git\COMMIT_EDITMSG.txt"));
        Assert.False(In(scope, @"C:\Users\Me\web\node_modules\pkg\readme.md"));
        Assert.False(In(scope, @"E:\$Recycle.Bin\a.txt"));
        var withData = Scope(denied: [@"C:\Users\Me\Index"]);
        Assert.False(In(withData, @"C:\Users\Me\Index\Content\manifest.txt"));
    }

    [Fact]
    public void Secret_names_are_out_whatever_the_extras_say()
    {
        var scope = Scope(s => s.ExtraTextExtensions = ["log", "key", "env"]);
        Assert.True(In(scope, @"C:\Users\Me\app.log"));
        Assert.False(In(scope, @"C:\Users\Me\server.key"));
        Assert.False(In(scope, @"C:\Users\Me\.env"));
        Assert.False(In(scope, @"C:\Users\Me\id_rsa.txt"));
        Assert.False(In(scope, @"C:\Users\Me\~$report.docx"));
        Assert.False(In(scope, @"C:\Users\Me\download.pdf.crdownload"));
    }

    [Fact]
    public void Kinds_switched_off_are_out()
    {
        var scope = Scope(s => s.Kinds = ContentKinds.Pdf);
        Assert.True(In(scope, @"C:\Users\Me\a.pdf"));
        Assert.False(In(scope, @"C:\Users\Me\a.docx"));
        Assert.False(In(scope, @"C:\Users\Me\a.txt"));
    }

    [Fact]
    public void Temporary_files_are_out()
    {
        var scope = Scope();
        Assert.False(In(scope, @"C:\Users\Me\a.pdf", FileAttributes.Temporary | FileAttributes.Archive));
    }

    [Fact]
    public void Every_root_is_in_for_the_demo_but_the_deny_list_still_applies()
    {
        var scope = Scope(allRoots: true);
        Assert.True(In(scope, @"D:\anything\a.pdf"));
        Assert.False(In(scope, @"D:\Users\Demo\AppData\Local\Temp\a.txt"));
    }

    [Fact]
    public void A_root_below_its_volume_is_judged_with_its_prefix()
    {
        // A folder root D:\Data: its documents' keys and verdicts chain over the prefix.
        var scope = ContentScope.Build(new ContentSettings { Mode = ContentMode.Documents, UseMyFolders = false, Folders = [@"D:\Data\Jobs"] }, false, [], Resolve);
        Assert.Equal(FolderVerdict.Pending, scope.RootVerdict("path:D:", "Data", @"D:\", out ulong state));
        Assert.Equal(ContentKeys.Of("path:D:", "Data"), state);
        Assert.True(scope.InScope("path:D:", @"Data\Jobs\a.pdf", null, out var kind));
        Assert.Equal(ContentKind.Pdf, kind);
        Assert.False(scope.InScope("path:D:", @"Data\Other\a.pdf", null, out _));
    }

    [Fact]
    public void The_fingerprint_moves_with_anything_that_changes_the_scope()
    {
        ulong a = Scope().Fingerprint;
        Assert.Equal(a, Scope().Fingerprint);
        Assert.NotEqual(a, Scope(s => s.Folders.Add(@"F:\")).Fingerprint);
        Assert.NotEqual(a, Scope(s => s.ExcludeFolders.Add("Archive")).Fingerprint);
        Assert.NotEqual(a, Scope(s => s.Kinds = ContentKinds.Pdf).Fingerprint);
        Assert.NotEqual(a, Scope(s => s.Mode = ContentMode.DocumentsAndPictures).Fingerprint);
        Assert.NotEqual(a, Scope(s => s.ExtraTextExtensions = ["log"]).Fingerprint);
    }
}
