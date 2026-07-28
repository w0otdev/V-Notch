using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class SystemFileSearchProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"vnotch-system-search-{Guid.NewGuid():N}");

    public SystemFileSearchProviderTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "ncpa.cpl"), string.Empty);
        File.WriteAllText(Path.Combine(_directory, "services.msc"), string.Empty);
        File.WriteAllText(Path.Combine(_directory, "optionalfeatures.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_directory, "not-launchable.dll"), string.Empty);
    }

    [Theory]
    [InlineData("ncpa.cpl", "ncpa.cpl")]
    [InlineData("ncpa", "ncpa.cpl")]
    [InlineData("services", "services.msc")]
    [InlineData("optionalfeatures", "optionalfeatures.exe")]
    public async Task SearchAsync_FindsLaunchableSystemFiles(string query, string expectedFile)
    {
        var provider = new SystemFileSearchProvider([_directory]);

        IReadOnlyList<SpotlightSearchItem> results =
            await provider.SearchAsync(query, 10, CancellationToken.None);

        SpotlightSearchItem result = Assert.Single(results);
        Assert.Equal(expectedFile, result.Title, ignoreCase: true);
        Assert.Equal(SpotlightResultKind.Application, result.Kind);
        Assert.Equal(
            Path.Combine(_directory, expectedFile),
            result.Target,
            ignoreCase: true);
    }

    [Fact]
    public async Task SearchAsync_DoesNotExposeNonLaunchableSystemFiles()
    {
        var provider = new SystemFileSearchProvider([_directory]);

        IReadOnlyList<SpotlightSearchItem> results =
            await provider.SearchAsync("not-launchable", 10, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_PrefersFirstRootForDuplicateFileNames()
    {
        string secondRoot = Path.Combine(_directory, "x86");
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(secondRoot, "ncpa.cpl"), string.Empty);
        var provider = new SystemFileSearchProvider([_directory, secondRoot]);

        SpotlightSearchItem result = Assert.Single(
            await provider.SearchAsync("ncpa.cpl", 10, CancellationToken.None));

        Assert.Equal(
            Path.Combine(_directory, "ncpa.cpl"),
            result.Target,
            ignoreCase: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
