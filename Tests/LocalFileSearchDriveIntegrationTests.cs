using System.IO;
using VNotch.Services.Spotlight.Providers;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class LocalFileSearchDriveIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task DefaultSearchFindsControlledFilesAcrossWritableLocalDrives()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string query = "vnotch-drive-" + suffix;
        var fixtures = new List<(string Drive, string Directory, string File)>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                string directory = Path.Combine(drive.RootDirectory.FullName, "vnotch-search-check-" + suffix);
                try
                {
                    Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, query + ".txt");
                    await File.WriteAllTextAsync(file, "V-Notch isolated search fixture");
                    fixtures.Add((drive.RootDirectory.FullName, directory, file));
                }
                catch (UnauthorizedAccessException) { output.WriteLine($"{drive.Name}: root is not writable."); }
                catch (IOException) { output.WriteLine($"{drive.Name}: drive became unavailable."); }
            }
            Assert.NotEmpty(fixtures);
            var provider = new LocalFileSearchProvider();
            var results = await provider.SearchAsync(query, 50, CancellationToken.None);
            foreach (var fixture in fixtures)
            {
                Assert.Contains(results, item => string.Equals(item.Target, fixture.File, StringComparison.OrdinalIgnoreCase));
                var direct = await provider.SearchAsync(fixture.File, 10, CancellationToken.None);
                Assert.Equal(fixture.File, Assert.Single(direct).Target);
                output.WriteLine($"{fixture.Drive}: default-drive discovery and direct path search confirmed.");
            }
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                string full = Path.GetFullPath(fixture.Directory);
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full)!),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.Drive)), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full) == "vnotch-search-check-" + suffix)
                {
                    try { Directory.Delete(full, recursive: true); }
                    catch (IOException) { /* A search/indexer may still hold the fixture briefly. */ }
                }
            }
        }
    }
}
