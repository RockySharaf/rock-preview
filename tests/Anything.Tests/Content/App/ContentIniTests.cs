using Anything.Core.Content;

namespace Anything.Tests.Content.App;

public sealed class ContentIniTests
{
    static Dictionary<string, string> Save(ContentSettings s)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ContentIni.Save(s, (k, v) => keys.Add(k, v)); // each key once
        return keys;
    }

    static ContentSettings Load(IReadOnlyDictionary<string, string> keys, bool demo = false)
    {
        var s = new ContentSettings();
        ContentIni.Load(s, k => keys.TryGetValue(k, out var v) ? v : null, demo);
        return s;
    }

    [Fact]
    public void Every_setting_round_trips()
    {
        var s = new ContentSettings
        {
            Mode = ContentMode.DocumentsAndPictures,
            ModeExplicit = true,
            UseMyFolders = false,
            Folders = [@"E:\", @"\\nas\jobs"],
            ReadNetworkDrives = true,
            ExcludeFolders = [@"E:\Temp"],
            Kinds = ContentKinds.Pdf | ContentKinds.Design,
            ExtraTextExtensions = ["log", "json"],
            MaxFileSizeMB = 500,
            ReadOnBattery = true,
            ReadEncrypted = true,
            NetworkKilobytesPerSecond = 4096,
            OcrFolders = [@"D:\Scans"],
        };
        var back = Load(Save(s));
        Assert.Equal(s.Mode, back.Mode);
        Assert.True(back.ModeExplicit);
        Assert.Equal(s.UseMyFolders, back.UseMyFolders);
        Assert.Equal(s.Folders, back.Folders);
        Assert.True(back.ReadNetworkDrives);
        Assert.Equal(s.ExcludeFolders, back.ExcludeFolders);
        Assert.Equal(s.Kinds, back.Kinds);
        Assert.Equal(s.ExtraTextExtensions, back.ExtraTextExtensions);
        Assert.Equal(s.MaxFileSizeMB, back.MaxFileSizeMB);
        Assert.Equal(s.ReadOnBattery, back.ReadOnBattery);
        Assert.Equal(s.ReadEncrypted, back.ReadEncrypted);
        Assert.Equal(s.NetworkKilobytesPerSecond, back.NetworkKilobytesPerSecond);
        Assert.Equal(s.OcrFolders, back.OcrFolders);
    }

    [Fact]
    public void Every_public_property_is_written()
    {
        // A property added to ContentSettings later must get a key here too, or Save drops it.
        var keys = Save(new ContentSettings { ModeExplicit = true });
        Assert.Equal(new[]
            {
                "content_mode", "content_my_folders", "content_folders", "content_network_drives", "content_exclude_folders", "content_kinds", "content_extensions",
                "content_max_size_mb", "content_read_on_battery", "content_read_encrypted", "content_network_kbps", "content_ocr_folders",
            },
            keys.Keys);
        int settable = typeof(ContentSettings).GetProperties().Count(p => p.CanWrite);
        Assert.Equal(settable - 1, keys.Count); // ModeExplicit is content_mode's presence, not a key of its own
    }

    [Fact]
    public void An_unchosen_mode_stays_unchosen()
    {
        var s = Load(new Dictionary<string, string>());
        Assert.Equal(ContentMode.NamesOnly, s.Mode);
        Assert.False(s.ModeExplicit);
        Assert.False(Save(s).ContainsKey("content_mode")); // saved as "names" it would delete a kept index
        var unknown = Load(new Dictionary<string, string> { ["content_mode"] = "everything" }); // a newer version's value
        Assert.False(unknown.ModeExplicit);
        Assert.False(Save(unknown).ContainsKey("content_mode"));
        var names = Load(new Dictionary<string, string> { ["content_mode"] = " Names " });
        Assert.True(names.ModeExplicit);
        Assert.Equal("names", Save(names)["content_mode"]);
    }

    [Fact]
    public void Defaults_when_keys_are_missing_or_bad()
    {
        var s = Load(new Dictionary<string, string>
        {
            ["content_max_size_mb"] = "-4",
            ["content_network_kbps"] = "0",
            ["content_kinds"] = "spreadsheets|movies",
            ["content_my_folders"] = "maybe",
        });
        var d = new ContentSettings();
        Assert.Equal(d.MaxFileSizeMB, s.MaxFileSizeMB);
        Assert.Equal(d.NetworkKilobytesPerSecond, s.NetworkKilobytesPerSecond);
        Assert.Equal(ContentKinds.All, s.Kinds); // nothing known in it: the default
        Assert.True(s.UseMyFolders);             // whitelisted: an unknown value is the default, not "off"
        Assert.Empty(s.Folders);
        Assert.Empty(s.ExtraTextExtensions);
        Assert.Equal(0, Load(new Dictionary<string, string> { ["content_max_size_mb"] = "0" }).MaxFileSizeMB); // 0 = no limit
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("0", true, false)]
    [InlineData("true", false, true)]
    [InlineData("TRUE", false, true)]   // a hand-edited ini
    [InlineData(" True ", false, true)]
    [InlineData("False", true, false)]
    [InlineData("FALSE", true, false)]
    [InlineData("maybe", true, true)]   // unknown: the default
    [InlineData("maybe", false, false)]
    [InlineData("yes", false, false)]
    [InlineData("", true, true)]
    [InlineData(null, true, true)]      // missing
    public void Switches_are_whitelisted(string? value, bool fallback, bool expected)
        => Assert.Equal(expected, ContentIni.Bool(value, fallback));

    [Fact]
    public void A_hand_edited_switch_in_capitals_keeps_its_meaning()
    {
        // content_my_folders=TRUE must not turn My folders off (the next reconcile would delete their documents).
        Assert.True(Load(new Dictionary<string, string> { ["content_my_folders"] = "TRUE" }).UseMyFolders);
        Assert.False(Load(new Dictionary<string, string> { ["content_my_folders"] = "FALSE" }).UseMyFolders);
        Assert.True(Load(new Dictionary<string, string> { ["content_read_on_battery"] = "TRUE" }).ReadOnBattery);
        Assert.False(Load(new Dictionary<string, string> { ["content_read_on_battery"] = "sometimes" }).ReadOnBattery);
    }

    [Fact]
    public void Kinds_parse_names_in_any_case_and_an_empty_list_means_none()
    {
        Assert.Equal(ContentKinds.Pdf | ContentKinds.Mail, Load(new Dictionary<string, string> { ["content_kinds"] = "PDF|mail|bogus" }).Kinds);
        Assert.Equal(ContentKinds.None, Load(new Dictionary<string, string> { ["content_kinds"] = "" }).Kinds);
        Assert.Equal("pdf|office|text|mail|design", Save(new ContentSettings())["content_kinds"]);
        Assert.Equal("", Save(new ContentSettings { Kinds = ContentKinds.None })["content_kinds"]);
    }

    [Fact]
    public void The_demo_starts_with_pictures_and_ini_files()
    {
        var demo = Load(new Dictionary<string, string>(), demo: true);
        Assert.Equal(ContentMode.DocumentsAndPictures, demo.Mode);
        Assert.True(demo.ModeExplicit);
        Assert.Equal(new[] { "ini" }, demo.ExtraTextExtensions);
        // What the demo's ini says wins over its defaults.
        var chosen = Load(new Dictionary<string, string> { ["content_mode"] = "names", ["content_extensions"] = "" }, demo: true);
        Assert.Equal(ContentMode.NamesOnly, chosen.Mode);
        Assert.Empty(chosen.ExtraTextExtensions);
    }

    [Fact]
    public void List_values_never_break_the_ini()
    {
        var s = new ContentSettings { Folders = ["a|b", "line\nbreak", "  ", @"C:\ok"] };
        string folders = Save(s)["content_folders"];
        Assert.DoesNotContain('\n', folders);
        Assert.Equal(new[] { "a b", "line break", @"C:\ok" }, Load(new Dictionary<string, string> { ["content_folders"] = folders }).Folders);
    }
}
