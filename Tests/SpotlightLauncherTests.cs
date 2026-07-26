using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightLauncherTests
{
    [Fact]
    public void IsValidTarget_AcceptsExistingFilesFoldersAndAppsFolderIds()
    {
        string directory = Directory.CreateTempSubdirectory("vnotch-spotlight-").FullName;
        string file = Path.Combine(directory, "item.txt");
        File.WriteAllText(file, "test");
        try
        {
            Assert.True(SpotlightLauncher.IsValidTarget(Item(SpotlightResultKind.File, file)));
            Assert.True(SpotlightLauncher.IsValidTarget(Item(SpotlightResultKind.Folder, directory)));
            Assert.True(SpotlightLauncher.IsValidTarget(Item(
                SpotlightResultKind.Application, "shell:AppsFolder\\PackageFamily!App")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(SpotlightResultKind.Application, "cmd.exe /c calc.exe")]
    [InlineData(SpotlightResultKind.Application, "shell:AppsFolder\\")]
    [InlineData(SpotlightResultKind.File, "https://example.com")]
    [InlineData(SpotlightResultKind.Folder, "C:\\missing-folder")]
    [InlineData(SpotlightResultKind.Application, "shell:AppsFolder\\Package!App\r\ncalc.exe")]
    public void IsValidTarget_RejectsCommandsAndMissingTargets(SpotlightResultKind kind, string target)
    {
        Assert.False(SpotlightLauncher.IsValidTarget(Item(kind, target)));
    }

    [Fact]
    public void CanReveal_AllowsFileBackedTargetsButNotStoreApps()
    {
        string directory = Directory.CreateTempSubdirectory("vnotch-spotlight-").FullName;
        string file = Path.Combine(directory, "item.txt");
        File.WriteAllText(file, "test");
        try
        {
            Assert.True(SpotlightLauncher.CanReveal(Item(SpotlightResultKind.File, file)));
            Assert.True(SpotlightLauncher.CanReveal(Item(SpotlightResultKind.Folder, directory)));
            Assert.True(SpotlightLauncher.CanReveal(Item(SpotlightResultKind.Application, file)));
            Assert.False(SpotlightLauncher.CanReveal(Item(
                SpotlightResultKind.Application, "shell:AppsFolder\\PackageFamily!App")));
            Assert.False(SpotlightLauncher.CanReveal(Item(SpotlightResultKind.File, "C:\\missing-file.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CanLaunchElevated_RequiresAFileBackedAppOrFile()
    {
        string directory = Directory.CreateTempSubdirectory("vnotch-spotlight-").FullName;
        string file = Path.Combine(directory, "tool.exe");
        File.WriteAllText(file, "test");
        try
        {
            Assert.True(SpotlightLauncher.CanLaunchElevated(Item(SpotlightResultKind.Application, file)));
            Assert.True(SpotlightLauncher.CanLaunchElevated(Item(SpotlightResultKind.File, file)));
            Assert.False(SpotlightLauncher.CanLaunchElevated(Item(SpotlightResultKind.Folder, directory)));
            Assert.False(SpotlightLauncher.CanLaunchElevated(Item(
                SpotlightResultKind.Application, "shell:AppsFolder\\PackageFamily!App")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetCopyableText_ReturnsPathsForFileBackedItemsAndValuesForCalculations()
    {
        string directory = Directory.CreateTempSubdirectory("vnotch-spotlight-").FullName;
        string file = Path.Combine(directory, "item.txt");
        File.WriteAllText(file, "test");
        try
        {
            Assert.Equal(file, SpotlightLauncher.GetCopyableText(Item(SpotlightResultKind.File, file)));
            Assert.Equal(directory, SpotlightLauncher.GetCopyableText(Item(SpotlightResultKind.Folder, directory)));
            Assert.Equal("42", SpotlightLauncher.GetCopyableText(Item(SpotlightResultKind.Calculation, "42")));
            Assert.Null(SpotlightLauncher.GetCopyableText(Item(
                SpotlightResultKind.Application, "shell:AppsFolder\\PackageFamily!App")));
            Assert.Null(SpotlightLauncher.GetCopyableText(Item(SpotlightResultKind.File, "C:\\missing-file.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsValidTarget_RejectsCalculationsFromProcessLaunch()
    {
        Assert.False(SpotlightLauncher.IsValidTarget(Item(SpotlightResultKind.Calculation, "42")));
    }

    [Fact]
    public void WindowsSearchQuery_RemovesAqsControlCharacters()
    {
        string sanitized = WindowsSearchProvider.SanitizeQuery("report:\"2026\" *(draft)\\'");

        Assert.Equal("report2026 draft", sanitized);
    }

    [Fact]
    public void WindowsSearchQuery_BoundsInputAndCanRejectOnlyControlCharacters()
    {
        Assert.Equal(256, WindowsSearchProvider.SanitizeQuery(new string('a', 300)).Length);
        Assert.Empty(WindowsSearchProvider.SanitizeQuery("\\\"'():*"));
    }

    private static SpotlightSearchItem Item(SpotlightResultKind kind, string target) =>
        new("id", kind, "title", "subtitle", target);
}
