using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>The Rock Preview service sends a volume's records to the app: they must arrive unchanged.</summary>
public sealed class VolumeRecordsTransferTests
{
    [Fact]
    public void RecordsSurviveTheTrip()
    {
        var records = new VolumeRecords();
        records.EnsureRoot();
        records.SetFromUsn(16, VolumeRecords.RootRecord, "Users", FileAttributes.Directory);
        records.SetFromUsn(17, 16, "ROG", FileAttributes.Directory);
        records.SetFromUsn(18, 17, "photo.psd", FileAttributes.Archive);
        records.SetFromUsn(18, 16, "photo-link.psd", FileAttributes.Archive); // a second hard link
        records.SetFromUsn(40, 17, "naïve résumé.docx", FileAttributes.Archive | FileAttributes.Hidden);

        var copy = RoundTrip(records);

        Assert.Equal(records.Length, copy.Length);
        for (int rec = 0; rec < records.Length; rec++)
        {
            Assert.Equal(records.IsInUse(rec), copy.IsInUse(rec));
            Assert.Equal(records.IsDirectory(rec), copy.IsDirectory(rec));
            Assert.Equal(records.NameOf(rec), copy.NameOf(rec));
            Assert.Equal(records.ParentOf(rec), copy.ParentOf(rec));
            Assert.Equal(records.SizeOf(rec), copy.SizeOf(rec));
        }

        // Both build the same index, hard link included.
        Assert.Equal(Paths(records), Paths(copy));
        Assert.Contains(@"C:\Users\photo-link.psd", Paths(copy));
    }

    [Fact]
    public void TruncatedDataIsRejected()
    {
        var records = new VolumeRecords();
        records.EnsureRoot();
        records.SetFromUsn(16, VolumeRecords.RootRecord, "file.txt", FileAttributes.Archive);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) records.WriteTo(w);
        var cut = new MemoryStream(ms.ToArray()[..(int)(ms.Length / 2)]);
        using var r = new BinaryReader(cut);
        Assert.ThrowsAny<IOException>(() => VolumeRecords.ReadFrom(r));
    }

    static VolumeRecords RoundTrip(VolumeRecords records)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) records.WriteTo(w);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var copy = VolumeRecords.ReadFrom(r);
        Assert.Equal(ms.Length, ms.Position); // everything written was read
        return copy;
    }

    static List<string> Paths(VolumeRecords records)
    {
        var index = new Anything.Core.FileIndex();
        records.BuildInto(index, "C:", ExclusionFilter.None, null);
        var paths = new List<string>();
        for (int id = 0; id < index.Count; id++)
            if (index.IsAlive(id)) paths.Add(index.GetFullPath(id));
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}
