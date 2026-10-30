using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class LocalFileSearchProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "local-search-tests", Guid.NewGuid().ToString("N"));

    public LocalFileSearchProviderTests() => Directory.CreateDirectory(_root);

    private string FileAt(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    [Fact]
    public async Task SearchesEveryConfiguredRootAndPreservesSameNamedFiles()
    {
        string first = FileAt(@"first\documents\travel-notes.txt");
        string second = FileAt(@"second\trips\travel-notes.txt");
        var provider = new LocalFileSearchProvider([Path.Combine(_root, "first"), Path.Combine(_root, "second")]);
        var results = await provider.SearchAsync("travel", 50, CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.Target == first);
        Assert.Contains(results, item => item.Target == second);
        Assert.All(results, item => Assert.Equal(SpotlightResultKind.File, item.Kind));
    }

    [Fact]
    public async Task ExplicitPathsFindUnindexedFilesAndFolderContents()
    {
        string path = FileAt(@"documents\nested\off-index-report.txt");
        var provider = new LocalFileSearchProvider([Path.Combine(_root, "missing")]);
        Assert.Equal(path, Assert.Single(await provider.SearchAsync(path, 10, CancellationToken.None)).Target);
        var results = await provider.SearchAsync(Path.Combine(_root, "documents") + "\\", 10, CancellationToken.None);
        Assert.Contains(results, item => item.Target == path);
    }

    [Fact]
    public async Task UsesUnicodeRankingAndReturnsTheBestMatchesWithinTheLimit()
    {
        string exact = FileAt("Báo cáo.pdf");
        FileAt("Báo cáo 2026.pdf");
        FileAt("Báo cáo final.pdf");
        var provider = new LocalFileSearchProvider([_root]);
        var results = await provider.SearchAsync("bao cao", 1, CancellationToken.None);
        Assert.Equal(exact, Assert.Single(results).Target);
    }

    [Fact]
    public async Task NormalSearchSkipsCachesButExplicitPathsStillWork()
    {
        string visible = FileAt(@"documents\unique-report.txt");
        string cached = FileAt(@"node_modules\unique-report.txt");
        var provider = new LocalFileSearchProvider([_root]);
        Assert.Equal(visible, Assert.Single(await provider.SearchAsync("unique-report", 10, CancellationToken.None)).Target);
        Assert.Equal(cached, Assert.Single(await provider.SearchAsync(cached, 10, CancellationToken.None)).Target);
    }

    [Fact]
    public async Task MissingAndNetworkRootsDoNotDiscardHealthyResults()
    {
        string path = FileAt("offline-drive-report.txt");
        var provider = new LocalFileSearchProvider([@"\\invalid.example\private", Path.Combine(_root, "missing"), _root]);
        Assert.Equal(path, Assert.Single(await provider.SearchAsync("offline-drive-report", 10, CancellationToken.None)).Target);
        Assert.Empty(await provider.SearchAsync(@"\\invalid.example\private", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ObservesCreatedAndDeletedFilesWithoutAStaleIndex()
    {
        var provider = new LocalFileSearchProvider([_root]);
        Assert.Empty(await provider.SearchAsync("fresh-document", 10, CancellationToken.None));
        string path = FileAt("fresh-document.txt");
        Assert.Single(await provider.SearchAsync("fresh-document", 10, CancellationToken.None));
        File.Delete(path);
        Assert.Empty(await provider.SearchAsync("fresh-document", 10, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAndTimeBudgetDoNotLeaveTheProviderBlocked()
    {
        FileAt("budget-report.txt");
        var provider = new LocalFileSearchProvider([_root], budgetMilliseconds: 0);
        var watch = Stopwatch.StartNew();
        Assert.Empty(await provider.SearchAsync("budget-report", 10, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync("budget-report", 10, cancelled.Token));
        Assert.Single(await new LocalFileSearchProvider([_root]).SearchAsync("budget-report", 10, CancellationToken.None));
    }

    [Fact]
    public async Task LocalFilesAreDeferredAndRespectSpotlightExclusions()
    {
        string hidden = FileAt(@"excluded\search-report.txt");
        string visible = FileAt(@"documents\search-report.txt");
        var provider = new LocalFileSearchProvider([_root]);
        var usage = new SpotlightUsageStore(Path.Combine(_root, "usage.json"), () => DateTime.UtcNow);
        usage.Preferences.SetExcludedFolders([Path.GetDirectoryName(hidden)!]);
        var service = new SpotlightSearchService([provider], usage);
        Assert.True(service.IsWindowsSearchAvailable);
        Assert.Empty(await service.SearchInstantAsync("search-report", 10, CancellationToken.None));
        Assert.Equal(visible, Assert.Single(await service.SearchDeferredAsync("search-report", 10, CancellationToken.None)).Target);
    }

    [Fact]
    public void NativeCancellationBeforeTheScanReturnsNoResults()
    {
        Assert.Equal(0, Create(out long handle));
        try
        {
            Assert.Equal(0, Cancel(handle));
            Assert.Equal(0, Run(handle, "report", 6, _root, _root.Length, "", 0, 10, 1500, out int count));
            Assert.Equal(0, count);
        }
        finally { Close(handle); }
    }

    [Fact]
    public async Task ExcludedFoldersCannotConsumeTheResultLimit()
    {
        FileAt(@"excluded\search-report.txt");
        string visible = FileAt(@"documents\search-report-final.txt");
        string excluded = Path.Combine(_root, "excluded");
        var provider = new LocalFileSearchProvider([_root])
        {
            IsExcluded = path => path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase),
            ExcludedFolders = () => new[] { excluded }
        };
        Assert.Equal(visible, Assert.Single(await provider.SearchAsync("search-report", 1, CancellationToken.None)).Target);
    }

    [Fact]
    public async Task ExplicitExecutablePathsKeepTheirApplicationClassification()
    {
        string path = FileAt("fixture-tool.exe");
        var result = Assert.Single(await new LocalFileSearchProvider([_root]).SearchAsync(path, 10, CancellationToken.None));
        Assert.Equal(SpotlightResultKind.Application, result.Kind);
    }

    [Fact]
    public async Task EmptyConfiguredRootsDoNotFallBackToScanningTheWholeMachine()
    {
        Assert.Empty(await new LocalFileSearchProvider([]).SearchAsync("report", 10, CancellationToken.None));
    }

    [Fact]
    public async Task FindsFilesBeyondMaxPathAndMarshalsPathsLargerThanTheStackBuffer()
    {
        string relative = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 10).Select(i => new string('a', 48) + i));
        string path = FileAt(Path.Combine(relative, "long-path-report.txt"));
        Assert.True(path.Length > 512);
        var results = await new LocalFileSearchProvider([_root]).SearchAsync("long-path-report", 10, CancellationToken.None);
        Assert.Equal(path, Assert.Single(results).Target);
    }

    [DllImport("VNotch.Native.dll", EntryPoint = "vn_local_file_search_create")]
    private static extern int Create(out long handle);
    [DllImport("VNotch.Native.dll", EntryPoint = "vn_local_file_search_cancel")]
    private static extern int Cancel(long handle);
    [DllImport("VNotch.Native.dll", EntryPoint = "vn_local_file_search_close")]
    private static extern int Close(long handle);
    [DllImport("VNotch.Native.dll", EntryPoint = "vn_local_file_search_run", CharSet = CharSet.Unicode)]
    private static extern int Run(long handle, string query, int queryLength, string roots, int rootsLength,
        string hidden, int hiddenLength, int limit, int budgetMilliseconds, out int count);

    public void Dispose()
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(_root))!;
        if (parent == Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "local-search-tests")))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* An asynchronous preference save may still be finishing. */ }
        }
    }
}
