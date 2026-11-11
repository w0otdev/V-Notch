using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightIncrementalSearchTests
{
    [Fact]
    public async Task FastResultsPublishBeforeASlowInstantProviderAndDeferredWorkStartsIndependently()
    {
        var slow = new PendingProvider(isInstant: true);
        var deferred = new PendingProvider(isInstant: false);
        var first = Item("first", 10);
        var service = new SpotlightSearchService([new ImmediateProvider(first), slow, deferred]);
        await using var updates = service.SearchUpdatesAsync("fixture", 10, default).GetAsyncEnumerator();

        do
        {
            Assert.True(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        } while (updates.Current.Results.Count == 0);
        Assert.Equal("first", Assert.Single(updates.Current.Results).Id);
        Assert.False(updates.Current.IsComplete);
        await deferred.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(slow.Completion.Task.IsCompleted);

        var highest = Item("highest", 100);
        deferred.Completion.SetResult([highest]);
        await MoveUntilAsync(updates, update => update.Results.Any(item => item.Id == "highest"));
        Assert.Equal(["highest", "first"], updates.Current.Results.Select(item => item.Id));
        Assert.False(updates.Current.IsComplete);

        slow.Completion.SetResult([Item("middle", 50)]);
        await MoveUntilAsync(updates, update => update.IsComplete);
        Assert.True(updates.Current.IsComplete);
        Assert.Equal(["highest", "middle", "first"], updates.Current.Results.Select(item => item.Id));
        var finalResults = updates.Current.Results;
        Assert.False(await updates.MoveNextAsync());
        var allAtOnce = await service.SearchAsync("fixture", 10, default);
        Assert.Equal(allAtOnce.Select(item => item.Id), finalResults.Select(item => item.Id));
    }

    [Fact]
    public async Task CancellingAnIncompleteStreamDoesNotWaitForAnUncooperativeProvider()
    {
        var slow = new PendingProvider(isInstant: true);
        var service = new SpotlightSearchService([new ImmediateProvider(Item("first", 10)), slow]);
        using var cancellation = new CancellationTokenSource();
        await using var updates = service.SearchUpdatesAsync("fixture", 10, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(slow.Completion.Task.IsCompleted);
        slow.Completion.SetResult([Item("late", 100)]);
    }

    [Fact]
    public async Task LaterProviderResultsPreserveAnExplicitSelectionAndDoNotDeclareNoResultsEarly()
    {
        var slow = new PendingProvider(isInstant: true);
        var service = new SpotlightSearchService([
            new ImmediateProvider(Item("first", 20), Item("selected", 10)), slow]);
        string usagePath = Path.Combine(Path.GetTempPath(), $"vnotch-incremental-{Guid.NewGuid():N}.json");
        await using var usage = new SpotlightUsageStore(usagePath, () => DateTime.UtcNow);
        using var vm = new SpotlightViewModel(service, usage);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ResultsPublished += (_, _) => { if (vm.Results.Count == 2) published.TrySetResult(); };
        Task search = vm.SearchAsync("fixture");
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsSearching);
        Assert.False(vm.HasNoResults);
        vm.SelectedResult = vm.Results.Single(item => item.Id == "selected");
        slow.Completion.SetResult([Item("highest", 100)]);
        await search.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsSearching);
        Assert.Equal("highest", vm.Results[0].Id);
        Assert.Equal("selected", vm.SelectedResult?.Id);
    }

    [Fact]
    public async Task PartialEmptyResultsAreNotReportedAsACompletedEmptyQuery()
    {
        var slow = new PendingProvider(isInstant: true);
        var service = new SpotlightSearchService([new ImmediateProvider(), slow]);
        await using var updates = service.SearchUpdatesAsync("fixture", 10, default).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Empty(updates.Current.Results);
        Assert.False(updates.Current.IsComplete);
        slow.Completion.SetResult([]);
        await MoveUntilAsync(updates, update => update.IsComplete);
        Assert.Empty(updates.Current.Results);
        Assert.True(updates.Current.IsComplete);
    }

    [Fact]
    public async Task EmptyImmediatePhaseClearsStaleRowsWhileDeferredSearchRemainsPending()
    {
        var deferred = new TaskCompletionSource<IReadOnlyList<SpotlightSearchItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SpotlightSearchService([
            new QueryProvider(true, query => Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(
                query == "initial" ? [Item("old", 10)] : [])),
            new QueryProvider(false, query => query == "initial"
                ? Task.FromResult<IReadOnlyList<SpotlightSearchItem>>([]) : deferred.Task)]);
        string usagePath = Path.Combine(Path.GetTempPath(), $"vnotch-empty-phase-{Guid.NewGuid():N}.json");
        await using var usage = new SpotlightUsageStore(usagePath, () => DateTime.UtcNow);
        using var vm = new SpotlightViewModel(service, usage);
        await vm.SearchAsync("initial");
        Assert.Equal("old", Assert.Single(vm.Results).Id);
        var staleRowsCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ResultsPublished += (_, _) => { if (vm.Results.Count == 0) staleRowsCleared.TrySetResult(); };

        Task replacement = vm.SearchAsync("replacement");
        await staleRowsCleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsSearching);
        Assert.False(vm.HasNoResults);
        Assert.False(replacement.IsCompleted);
        Assert.Null(vm.SelectedResult);

        deferred.SetResult([]);
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsSearching);
        Assert.True(vm.HasNoResults);
    }

    private static SpotlightSearchItem Item(string id, double score) =>
        new(id, SpotlightResultKind.Application, id, "fixture", $"shell:AppsFolder\\{id}!App") { Score = score };

    private static async Task MoveUntilAsync(IAsyncEnumerator<SpotlightSearchService.SearchUpdate> updates,
        Func<SpotlightSearchService.SearchUpdate, bool> condition)
    {
        do
        {
            Assert.True(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        } while (!condition(updates.Current));
    }

    private sealed class ImmediateProvider(params SpotlightSearchItem[] items) : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public bool IsInstant => true;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(items);
    }

    private sealed class QueryProvider(bool isInstant,
        Func<string, Task<IReadOnlyList<SpotlightSearchItem>>> search) : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public bool IsInstant => isInstant;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            search(query);
    }

    private sealed class PendingProvider(bool isInstant) : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public bool IsInstant => isInstant;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<SpotlightSearchItem>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Completion.Task;
        }
    }
}
