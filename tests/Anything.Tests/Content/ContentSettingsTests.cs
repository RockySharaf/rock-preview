using System.Collections;
using System.Reflection;
using Anything.Core.Content;

namespace Anything.Tests.Content;

public class ContentSettingsTests
{
    [Fact]
    public void Defaults_read_nothing()
    {
        var s = new ContentSettings();
        Assert.Equal(ContentMode.NamesOnly, s.Mode);
        Assert.False(s.ModeExplicit);
        Assert.True(s.UseMyFolders);
        Assert.Equal(ContentKinds.All, s.Kinds);
        Assert.Equal(2048, s.MaxFileSizeMB);
        Assert.Equal(8192, s.NetworkKilobytesPerSecond);
        Assert.False(s.ReadOnBattery);
        Assert.False(s.ReadEncrypted);
    }

    [Fact]
    public void Clone_copies_every_settable_property_and_deep_copies_lists()
    {
        var original = new ContentSettings();
        var props = typeof(ContentSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.SetMethod!.IsPublic).ToArray();
        Assert.True(props.Length >= 12, "every setting is a public settable property");
        var fresh = new ContentSettings();
        foreach (var p in props) p.SetValue(original, Different(p.PropertyType, p.GetValue(fresh)));

        var copy = original.Clone();
        foreach (var p in props)
        {
            object? a = p.GetValue(original), b = p.GetValue(copy);
            if (a is IList listA)
            {
                var listB = Assert.IsAssignableFrom<IList>(b);
                Assert.NotSame(listA, listB);
                Assert.Equal(listA.Cast<object>(), listB.Cast<object>());
                listA.Add("changed later");
                Assert.DoesNotContain("changed later", listB.Cast<object>());
            }
            else Assert.True(Equals(a, b), $"{p.Name} was not copied");
        }
    }

    /// <summary>A value of the type that differs from the default one.</summary>
    static object Different(Type t, object? current)
    {
        if (t == typeof(bool)) return !(bool)current!;
        if (t == typeof(int)) return (int)current! + 7777;
        if (t == typeof(List<string>)) return new List<string> { "x", "y" };
        if (t.IsEnum)
        {
            foreach (var v in Enum.GetValues(t))
                if (!Equals(v, current)) return v;
        }
        throw new InvalidOperationException($"Add a non-default value for {t} to this test.");
    }

    [Fact]
    public void An_unchosen_mode_is_never_saved_so_it_never_deletes_a_kept_index()
    {
        // An older build dropped content_mode: load, save, load again must still be "never chosen".
        var s = new ContentSettings();
        s.LoadMode(null);
        Assert.False(s.ModeExplicit);
        Assert.Equal(ContentMode.NamesOnly, s.Mode);
        Assert.Null(s.ModeToSave());
        var reloaded = new ContentSettings();
        reloaded.LoadMode(s.ModeToSave());
        Assert.False(reloaded.ModeExplicit);

        // Values a newer or broken build might write are unchosen too, never an explicit names-only.
        foreach (var unknown in new[] { "", "   ", "everything", "0", "name" })
        {
            s.LoadMode(unknown);
            Assert.False(s.ModeExplicit, unknown);
            Assert.Null(s.ModeToSave());
        }
    }

    [Theory]
    [InlineData("names", ContentMode.NamesOnly), InlineData("Documents", ContentMode.Documents), InlineData(" PICTURES ", ContentMode.DocumentsAndPictures)]
    public void A_chosen_mode_round_trips(string value, ContentMode mode)
    {
        var s = new ContentSettings();
        s.LoadMode(value);
        Assert.True(s.ModeExplicit);
        Assert.Equal(mode, s.Mode);
        Assert.Equal(value.Trim().ToLowerInvariant(), s.ModeToSave());
        Assert.Equal(s.ModeToSave(), ContentSettings.ModeName(mode));
        // Settings or Setup choosing a mode makes it explicit, and then it is saved.
        Assert.Equal(ContentSettings.ModeName(mode), new ContentSettings { Mode = mode, ModeExplicit = true }.ModeToSave());
    }

    [Fact]
    public void Nested_folders_are_merged()
    {
        var merged = ContentSettings.MergeNested(
        [
            @"C:\Users\Me", @"C:\Users\Me\Documents", @"c:\users\me\", "D:", @"D:\Data", @"\\nas\jobs\2024", @"\\NAS\jobs",
            @"relative\path", "", "   ", @"C:\UsersX",
        ]);
        Assert.Equal([@"C:\Users\Me", @"D:\", @"\\NAS\jobs", @"C:\UsersX"], merged);
    }

    [Fact]
    public void Inside_needs_a_separator_boundary()
    {
        Assert.True(ContentSettings.IsInside(@"C:\Users\Me\a", @"C:\Users\Me"));
        Assert.True(ContentSettings.IsInside(@"C:\Users\Me", @"c:\users\me"));
        Assert.True(ContentSettings.IsInside(@"C:\Data", @"C:\"));
        Assert.False(ContentSettings.IsInside(@"C:\UsersX", @"C:\Users"));
        Assert.False(ContentSettings.IsInside(@"D:\Users", @"C:\"));
    }

    [Fact]
    public void My_folders_are_full_paths_none_inside_another()
    {
        var folders = ContentSettings.MyFolders();
        Assert.NotEmpty(folders);
        Assert.All(folders, f => Assert.True(Path.IsPathFullyQualified(f), f));
        foreach (var a in folders)
            foreach (var b in folders)
                if (!ReferenceEquals(a, b)) Assert.False(ContentSettings.IsInside(a, b), $"{a} is inside {b}");
    }

    [Fact]
    public void Added_folders_add_to_my_folders()
    {
        var my = ContentSettings.MyFolders();
        string inside = Path.Combine(my[0], "Sub folder");
        var s = new ContentSettings { Folders = [inside, @"\\nas\jobs"] };
        var effective = s.EffectiveFolders();
        Assert.Contains(@"\\nas\jobs", effective);
        Assert.DoesNotContain(inside, effective);
        Assert.All(my, f => Assert.Contains(f, effective));

        s.UseMyFolders = false;
        Assert.Equal([inside, @"\\nas\jobs"], s.EffectiveFolders());
    }
}
