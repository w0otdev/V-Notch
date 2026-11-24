using System.IO;
using System.Reflection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppSearchProviderTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public async Task BatchRankingMatchesScalarScoresOrderAndLimitAcrossLanguageChanges()
    {
        var apps = Enumerable.Range(0, 150).Select(i => new SpotlightSearchItem($"app:{i}",
            SpotlightResultKind.Application, $"Studio {149 - i:D3}", "Application", $@"C:\Apps\studio{i}.exe")).ToList();
        apps.Add(new("accent", SpotlightResultKind.Application, "Ứng Dụng.exe", "Application", @"C:\Apps\Ứng Dụng.exe"));
        apps.Add(new("long", SpotlightResultKind.Application, new string('x', 1024) + ".preview", "Studio", "shell:AppsFolder\\Editor"));
        apps.Add(new("cmd", SpotlightResultKind.Application, "Command Prompt", "spotlight.kind.application",
            Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        int builds = 0;
        var provider = new AppSearchProvider(() => { Interlocked.Increment(ref builds); return apps; }, () => DateTime.UnixEpoch);
        string previous = Loc.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "en", "vi", "en" })
            {
                Loc.SetLanguage(language);
                string localizedTitle = new SpotlightResultPresentation(apps[^1]).DisplayTitle;
                foreach (string query in new[] { "Studio", "stuido", "ung dung", "Application", "cmd.exe", localizedTitle, "?", "  " })
                {
                    var expected = apps.Select(item => item with { Score = SpotlightRanker.Score(item, query) })
                        .Where(item => item.Score > 0).ToList();
                    expected.Sort(SpotlightSearchService.s_scoreAndTitleComparison);
                    foreach (int limit in new[] { 0, 1, 50, 200 })
                    {
                        var actual = await provider.SearchAsync(query, limit, default);
                        Assert.Equal(expected.Take(limit).Select(x => (x.Id, x.Score)), actual.Select(x => (x.Id, x.Score)));
                    }
                }
            }
            Assert.Equal(1, builds);
        }
        finally { Loc.SetLanguage(previous); }
    }

    [Fact]
    public async Task CancelledWaiterLeavesSharedBuildUsableAndExpiredRowsCanBeCollected()
    {
        int builds = 0;
        DateTime now = DateTime.UnixEpoch;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldItems = new List<WeakReference>();
        var provider = new AppSearchProvider(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            var app = new SpotlightSearchItem($"app:{++builds}", SpotlightResultKind.Application, "Editor", "App", "editor.exe");
            oldItems.Add(new WeakReference(app));
            return new[] { app };
        }, () => now);
        await ExerciseReaders();
        for (int i = 2; i <= 6; i++)
        {
            now = now.AddMinutes(31);
            Assert.Equal($"app:{i}", Assert.Single(await provider.SearchAsync("Editor", 1, default)).Id);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(oldItems.SkipLast(1), reference => Assert.False(reference.IsAlive));
        Assert.True(oldItems[^1].IsAlive);
        GC.KeepAlive(provider);

        async Task ExerciseReaders()
        {
            using var stop = new CancellationTokenSource();
            var cancelled = provider.SearchAsync("Editor", 1, stop.Token);
            var readers = Enumerable.Range(0, 8).Select(_ => provider.SearchAsync("Editor", 1, default)).ToArray();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            }
            finally { release.Set(); }
            foreach (var result in await Task.WhenAll(readers))
                Assert.Equal("app:1", Assert.Single(result).Id);
        }
    }

    [Fact]
    public async Task InstalledApplicationIndexIsReusedAndSearchRanksAndLimitsItsResults()
    {
        var provider = new AppSearchProvider();
        Assert.True(provider.IsAvailable);
        Assert.True(provider.IsInstant);
        var index = provider.WarmupAsync();
        await index.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(index, provider.WarmupAsync());
        var apps = (await index).Apps;
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
        await cancelled.CancelAsync();
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
