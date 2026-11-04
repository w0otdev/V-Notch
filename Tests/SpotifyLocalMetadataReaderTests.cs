using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyLocalMetadataReaderTests
{
    [Theory]
    [InlineData("damsay (feat. Machiot) • SIVAN, Machiot", "damsay (feat. Machiot)", "SIVAN, Machiot")]
    [InlineData("Song • Live • Đen, Biên", "Song • Live", "Đen, Biên")]
    [InlineData("Song • Earth, Wind & Fire", "Song", "Earth, Wind & Fire")]
    [InlineData("Song • Singer", "Song", "Singer")]
    public void DocumentTitlePreservesTheEntireArtistList(string document, string title, string artists)
    {
        Assert.Equal(new SpotifyLocalMetadataReader.Track(title, artists),
            SpotifyLocalMetadataReader.ParseDocumentTitle(document));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Spotify Premium")]
    [InlineData("Song • ")]
    [InlineData(" • Singer")]
    public void MissingMetadataCannotBecomeAnArtistList(string? document)
        => Assert.Null(SpotifyLocalMetadataReader.ParseDocumentTitle(document));

    [Fact]
    public async Task TrackChangeWaitsForTheMatchingLocalSnapshotAndThenUsesItsCache()
    {
        int reads = 0;
        var reader = new SpotifyLocalMetadataReader(() => Interlocked.Increment(ref reads) == 1
            ? new("Previous song", "Previous singer")
            : new("Song", "Singer, Collaborator"));

        Assert.Equal("Singer, Collaborator", await reader.TryGetArtistsAsync("Song", "Singer", default));
        int completedReads = reads;
        Assert.Equal("Singer, Collaborator", await reader.TryGetArtistsAsync("Song", "Singer", default));
        Assert.Equal(completedReads, reads);
    }

    [Fact]
    public async Task SameTitleByADifferentArtistIsNeverUsed()
    {
        var reader = new SpotifyLocalMetadataReader(() => new("Song", "Singer Two, Collaborator"));
        Assert.Null(await reader.TryGetArtistsAsync("Song", "Singer", default));
    }

    [Fact]
    public async Task UnresponsiveProviderHasABoundedWaitAndSharesOneWorker()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var reader = new SpotifyLocalMetadataReader(() =>
        {
            Interlocked.Increment(ref reads);
            started.SetResult();
            release.Wait();
            finished.SetResult();
            return new("Song", "Singer, Collaborator");
        });

        try
        {
            reader.WarmUp();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var lookups = Task.WhenAll(reader.TryGetArtistsAsync("Song", "Singer", default),
                reader.TryGetArtistsAsync("Other song", "Singer", default));
            Assert.All(await lookups.WaitAsync(TimeSpan.FromSeconds(2)), Assert.Null);
            Assert.Equal(1, reads);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancellationStopsTheWaitWithoutLosingTheCompletedSnapshot()
    {
        var reader = new SpotifyLocalMetadataReader(() => new("Song", "Singer, Collaborator"));
        Assert.Equal("Singer, Collaborator", await reader.TryGetArtistsAsync("Song", "Singer", default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.TryGetArtistsAsync("Song", "Singer", cancelled.Token));
        Assert.Equal("Singer, Collaborator", await reader.TryGetArtistsAsync("Song", "Singer", default));
    }

    [Fact]
    public void NativeReaderDoesNotThrowOrCrashWhenInvoked()
    {
        var reader = new SpotifyLocalMetadataReader(includeTrackId: true);
        var ex = Record.Exception(() => reader.WarmUp());
        Assert.Null(ex);
    }
}
