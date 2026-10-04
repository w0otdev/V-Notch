using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaChangeQueueTests
{
    [Fact]
    public async Task TimelineFloodPreservesEarlierSessionAndMetadataChanges()
    {
        var queue = new MediaChangeQueue();
        var critical = ChangeTypes.SessionChanged | ChangeTypes.MediaProperties | ChangeTypes.Playback;
        queue.Enqueue(critical);
        for (int i = 0; i < 100_000; i++) queue.Enqueue(ChangeTypes.Timeline);
        queue.Complete();

        var batches = new List<ChangeTypes>();
        await foreach (var changes in queue.ReadAllAsync(CancellationToken.None)) batches.Add(changes);
        Assert.Equal(critical | ChangeTypes.Timeline, Assert.Single(batches));
    }

    [Fact]
    public async Task ChangesArrivingDuringAnUpdateAreDeliveredInTheNextBatch()
    {
        var queue = new MediaChangeQueue();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator();
        queue.Enqueue(ChangeTypes.Timeline);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(ChangeTypes.Timeline, reader.Current);

        Parallel.For(0, 100_000, _ => queue.Enqueue(ChangeTypes.Heartbeat));
        queue.Enqueue(ChangeTypes.SessionChanged | ChangeTypes.MediaProperties);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(ChangeTypes.Heartbeat | ChangeTypes.SessionChanged | ChangeTypes.MediaProperties, reader.Current);
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumerPreserveEveryChangeType()
    {
        var queue = new MediaChangeQueue();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consume = Task.Run(async () =>
        {
            var seen = ChangeTypes.None;
            await foreach (var changes in queue.ReadAllAsync(cts.Token)) seen |= changes;
            return seen;
        });
        await Task.WhenAll(Enum.GetValues<ChangeTypes>().Select(changes => Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++) queue.Enqueue(changes);
        })));
        queue.Complete();
        Assert.Equal(ChangeTypes.Heartbeat | ChangeTypes.Timeline | ChangeTypes.Playback |
            ChangeTypes.MediaProperties | ChangeTypes.SessionChanged | ChangeTypes.ForceRefresh, await consume);
    }

    [Fact]
    public async Task ClearingForRestartDiscardsPreviousFlagsAndWakeups()
    {
        var queue = new MediaChangeQueue();
        queue.Enqueue(ChangeTypes.SessionChanged);
        queue.Clear();
        queue.Enqueue(ChangeTypes.ForceRefresh);
        queue.Complete();
        var batches = new List<ChangeTypes>();
        await foreach (var changes in queue.ReadAllAsync(CancellationToken.None)) batches.Add(changes);
        Assert.Equal(ChangeTypes.ForceRefresh, Assert.Single(batches));
    }
}
