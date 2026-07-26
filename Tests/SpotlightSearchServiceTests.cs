using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_MergesDeduplicatesLimitsAndUsesStableTieBreaks()
    {
        var service = new SpotlightSearchService(new ISpotlightProvider[]
        {
            new FixedProvider(
                Item("z", "Duplicate low", @"C:\Same", 10),
                Item("b", "Beta", @"C:\Beta", 50)),
            new FixedProvider(
                Item("a", "Duplicate high", @"c:\same", 90),
                Item("a", "Alpha", @"C:\Alpha", 50))
        });

        var results = await service.SearchAsync("query", 2, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Duplicate high", results[0].Title);
        Assert.Equal("a", results[1].Id);
        Assert.Single(results.Where(result =>
            string.Equals(result.Target, @"C:\Same", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SearchAsync_ProviderFailureDoesNotDiscardHealthyResults()
    {
        var expected = Item("ok", "Calculator", @"C:\Calculator", 100);
        var service = new SpotlightSearchService(new ISpotlightProvider[]
        {
            new ThrowingProvider(),
            new FixedProvider(expected)
        });

        var results = await service.SearchAsync("calc", 10, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(results));
    }

    [Fact]
    public async Task SearchAsync_PropagatesCallerCancellation()
    {
        var service = new SpotlightSearchService(new[] { new BlockingProvider() });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync("query", 10, cts.Token));
    }

    [Fact]
    public async Task SearchAsync_RejectsInvalidLimitsBeforeCallingProviders()
    {
        var provider = new CapturingProvider();
        var service = new SpotlightSearchService(new[] { provider });

        var results = await service.SearchAsync("query", 0, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SearchAsync_TrimsAndBoundsTheQueryAndResultLimit()
    {
        var provider = new CapturingProvider();
        var service = new SpotlightSearchService(new[] { provider });

        await service.SearchAsync($"  {new string('a', 300)}  ", 500, CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(256, provider.Query.Length);
        Assert.Equal(50, provider.Limit);
    }

    [Fact]
    public async Task ViewModel_DoesNotPublishAStaleQuery()
    {
        var provider = new ControlledProvider();
        string usagePath = Path.Combine(Path.GetTempPath(), $"vnotch-usage-{Guid.NewGuid():N}.json");
        var viewModel = new SpotlightViewModel(
            new SpotlightSearchService(new[] { provider }),
            new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));

        Task slowSearch = viewModel.SearchAsync("slow");
        await provider.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.SearchAsync("fast");
        provider.CompleteSlow(Item("slow", "Slow", @"C:\Slow", 100));
        await slowSearch;

        Assert.Equal("fast", viewModel.Query);
        Assert.Equal("Fast", Assert.Single(viewModel.Results).Title);
        viewModel.Dispose();
    }

    private static SpotlightSearchItem Item(
        string id, string title, string target, double score) =>
        new(id, SpotlightResultKind.File, title, target, target) { Score = score };

    private sealed class FixedProvider(params SpotlightSearchItem[] results) : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results);
    }

    private sealed class ThrowingProvider : ISpotlightProvider
    {
        public bool IsAvailable => false;

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider unavailable");
    }

    private sealed class BlockingProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Array.Empty<SpotlightSearchItem>();
        }
    }

    private sealed class CapturingProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public int CallCount { get; private set; }
        public string Query { get; private set; } = string.Empty;
        public int Limit { get; private set; }

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken)
        {
            CallCount++;
            Query = query;
            Limit = limit;
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(Array.Empty<SpotlightSearchItem>());
        }
    }

    private sealed class ControlledProvider : ISpotlightProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<SpotlightSearchItem>> _slowResults =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;
        public TaskCompletionSource SlowStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken)
        {
            if (query == "fast")
                return new[] { Item("fast", "Fast", @"C:\Fast", 100) };

            SlowStarted.TrySetResult();
            return await _slowResults.Task;
        }

        public void CompleteSlow(SpotlightSearchItem result) => _slowResults.TrySetResult(new[] { result });
    }
}
