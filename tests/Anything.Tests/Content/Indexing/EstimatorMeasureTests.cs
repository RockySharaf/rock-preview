using System.Diagnostics;
using Anything.Core.Content;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// A manual measurement on this PC's own folders (directory listings only, nothing opened): set RP_MEASURE=1 to run.
/// It prints the census of My folders, how long it took, and the three choices' estimates on the priors.
/// </summary>
public class EstimatorMeasureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task My_folders_census_and_estimates_on_this_PC()
    {
        if (Environment.GetEnvironmentVariable("RP_MEASURE") != "1") return;
        var settings = new ContentSettings { Mode = ContentMode.Documents, ModeExplicit = true };
        IReadOnlyList<string> folders = Environment.GetEnvironmentVariable("RP_MEASURE_FOLDER") is { Length: > 0 } one ? [one] : ContentSettings.MyFolders();
        output.WriteLine("My folders: " + string.Join(", ", folders));
        var sw = Stopwatch.StartNew();
        var census = await ContentEstimator.CountFoldersAsync(folders, settings, TimeSpan.FromSeconds(20), null, CancellationToken.None);
        output.WriteLine($"census: {sw.Elapsed.TotalSeconds:F1} s, complete {census.Complete}, cloud-only {census.CloudOnly}");
        foreach (var (kind, t) in census.Kinds.OrderBy(k => k.Key))
            output.WriteLine($"  {kind,-8} {t.Files,8:N0} files {t.Bytes / 1e6,10:N0} MB  samples {census.Samples.GetValueOrDefault(kind)?.Count ?? 0}");
        foreach (var mode in (ContentMode[])[ContentMode.Documents, ContentMode.DocumentsAndPictures])
        {
            var e = ContentEstimator.Estimate(census, ContentEstimator.Priors, mode, settings);
            output.WriteLine($"{mode}: {e.Documents:N0} documents, {e.Pictures:N0} pictures, {e.IndexBytes / 1e6:F1} MB, first pass {e.FirstPass.TotalMinutes:F1} min{(e.AtLeast ? " (at least)" : "")}");
        }
    }
}
