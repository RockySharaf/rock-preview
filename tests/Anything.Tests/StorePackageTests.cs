using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Anything.Tests;

/// <summary>
/// The Microsoft Store's package. Microsoft refused allowElevation on 2026-11-24, so its manifest
/// (build/Store/AppxManifest.xml) declares no restricted capability but runFullTrust, the one a desktop program needs, and
/// the program never asks for elevation inside the package: one added back by accident fails here, before a
/// certification does. The program's part is read from the source, as its windows can't be made in the tests.
/// </summary>
public sealed class StorePackageTests
{
    const string RestrictedCapabilities = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Anything.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("The tests run outside the source tree: Anything.sln wasn't found above " + AppContext.BaseDirectory);
    }

    static XDocument Manifest() => XDocument.Load(Path.Combine(RepoRoot(), "build", "Store", "AppxManifest.xml"));

    [Fact]
    public void The_manifest_declares_no_elevation()
    {
        var named = Manifest().Descendants().Select(e => (string?)e.Attribute("Name")).OfType<string>().ToList();
        Assert.NotEmpty(named);
        Assert.DoesNotContain(named, n => n.Equals("allowElevation", StringComparison.OrdinalIgnoreCase));
        // Not even in a comment: people read the manifest sent back after the refusal too.
        Assert.DoesNotContain("allowElevation", File.ReadAllText(Path.Combine(RepoRoot(), "build", "Store", "AppxManifest.xml")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_only_restricted_capability_is_runFullTrust()
    {
        // rescap, and any later restrictedcapabilities/N namespace.
        var restricted = Manifest().Descendants()
            .Where(e => e.Name.LocalName == "Capability" && e.Name.NamespaceName.StartsWith(RestrictedCapabilities, StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("Name") ?? "")
            .ToList();
        Assert.Equal(new[] { "runFullTrust" }, restricted);
    }

    [Fact]
    public void The_program_never_asks_for_elevation_inside_the_package()
    {
        // Both ways the program starts itself elevated refuse first thing there, whoever calls them.
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anything", "Program.cs"));
        foreach (string helper in new[] { "RelaunchElevated", "RunElevatedAndWait" })
            Assert.Matches(new Regex(@"public static \w+ " + helper + @"\(IEnumerable<string> args\)\s*\{\s*if \(StorePackage\.IsInstalled\) return "), program);
    }
}
