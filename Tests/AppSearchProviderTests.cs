using System.IO;
using System.Reflection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppSearchProviderTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public async Task InstalledApplicationIndexIsReusedAndSearchRanksAndLimitsItsResults()
    {
        var provider = new AppSearchProvider();
        Assert.True(provider.IsAvailable);
        Assert.True(provider.IsInstant);
        Task index = provider.WarmupAsync();
        await index.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(index, provider.WarmupAsync());
        var apps = await (Task<IReadOnlyList<SpotlightSearchItem>>)index;
        Assert.NotEmpty(apps);
        Assert.All(apps, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Target));
            Assert.Equal(SpotlightResultKind.Application, item.Kind);
            Assert.Null(item.Icon);
        });
        Assert.Equal(apps.Count, apps.Select(item => item.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        string query = apps.First(item => !string.IsNullOrWhiteSpace(item.Title)).Title;
        var results = await provider.SearchAsync(query, 3, default);
        Assert.NotEmpty(results);
        Assert.InRange(results.Count, 1, 3);
        Assert.Equal(results.OrderByDescending(item => item.Score).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase), results);
        Assert.All(results, item => Assert.Equal(Loc.Get("spotlight.kind.application"), item.Subtitle));
        Assert.Empty(await provider.SearchAsync("zzfixture-unknown-app-123456789", 3, default));
        Assert.Empty(await provider.SearchAsync(query, 0, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(query, 1, cancelled.Token));
    }

    [Fact]
    public void StartMenuTraversalFindsNestedShortcutsAndKeepsTheFirstDuplicateTitle()
    {
        string root = Directory.CreateTempSubdirectory("vnotch-app-index-").FullName;
        try
        {
            string nested = Directory.CreateDirectory(Path.Combine(root, "Programs", "Nested")).FullName;
            string shortcut = Path.Combine(nested, "Fixture App.lnk");
            File.WriteAllBytes(shortcut, []);
            File.WriteAllText(Path.Combine(nested, "ignored.txt"), "fixture");
            var apps = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
            Invoke("AddStartMenuShortcuts", apps, root);
            var item = Assert.Single(apps.Values);
            Assert.Equal("Fixture App", item.Title);
            Assert.Equal(shortcut, item.Target);
            Assert.Equal(shortcut, item.IconPath);
            Invoke("AddIfMissing", apps, item with { Title = "FIXTURE APP", Target = "other" });
            Assert.Same(item, Assert.Single(apps.Values));
            Invoke("AddStartMenuShortcuts", apps, Path.Combine(root, "missing"));
            Assert.Single(apps);
            Assert.Equal(root, Invoke("ResolveTarget", root, ""));
            Assert.Equal(shortcut, Invoke("ResolveTarget", shortcut, ""));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("\\\\server\\share", "", "")]
    [InlineData("shell:AppsFolder\\fixture", "", "shell:AppsFolder\\fixture")]
    [InlineData("missing-file", "Package!Fixture", "shell:AppsFolder\\Package!Fixture")]
    [InlineData("missing-file", "", "shell:AppsFolder\\missing-file")]
    [InlineData("", "", "")]
    public void ShellTargetsRetainApplicationIdentityAndRejectNetworkPaths(string path, string appId, string expected) => Assert.Equal(expected, Invoke("ResolveTarget", path, appId));

    [Fact]
    public void BrokenShellPropertiesBecomeEmptyStringsWithoutDiscardingTheIndex()
    {
        Assert.Equal("", Invoke("ReadString", (Func<object?>)(() => throw new InvalidOperationException())));
        Assert.Equal("fixture", Invoke("ReadString", (Func<object?>)(() => " fixture ")));
        Assert.Equal("", Invoke("ReadString", (Func<object?>)(() => null)));
        Invoke("ReleaseComObject", (object?)null);
        Invoke("ReleaseComObject", new object());
    }

    private static object? Invoke(string method, params object?[] arguments) => typeof(AppSearchProvider).GetMethod(method, Private)!.Invoke(null, arguments);
}
