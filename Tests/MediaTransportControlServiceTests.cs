using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaTransportControlServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlayPauseUsesTheSessionAndFallsBackOnlyWhenItDeclines(bool success)
    {
        var session = new Session { ToggleResult = success };
        var keys = new List<byte>();
        var service = Create(session, keys);
        await service.PlayPauseAsync();
        Assert.Equal(1, session.ToggleCalls);
        Assert.Equal(success ? Array.Empty<byte>() : new[] { Win32Interop.VK_MEDIA_PLAY_PAUSE }, keys);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("next")]
    [InlineData("previous")]
    public async Task MissingOrDisconnectedSessionsUseTheCorrespondingMediaKey(string command)
    {
        foreach (bool throws in new[] { false, true })
        {
            var keys = new List<byte>();
            var service = new MediaTransportControlService(() => throws ? throw new InvalidOperationException("Disconnected") : null, keys.Add);
            await Execute(service, command);
            Assert.Equal(command switch { "play" => Win32Interop.VK_MEDIA_PLAY_PAUSE, "next" => Win32Interop.VK_MEDIA_NEXT_TRACK, _ => Win32Interop.VK_MEDIA_PREV_TRACK }, Assert.Single(keys));
        }
    }

    [Theory]
    [InlineData(true, true, 100, true, 1, 0, false)]
    [InlineData(true, false, 100, true, 1, 1, false)]
    [InlineData(false, true, 100, true, 0, 1, false)]
    [InlineData(false, true, 0, true, 0, 0, true)]
    [InlineData(false, true, 100, false, 0, 1, true)]
    public async Task NextFallsBackFromSkipToTimelineEndBeforeSendingAKey(bool enabled, bool skipResult, int end, bool seekResult, int skips, int seeks, bool key)
    {
        var session = new Session { Controls = new(enabled, true), NextResult = skipResult, Timeline = Timeline(0, end, 5), SeekResult = seekResult };
        var keys = new List<byte>();
        await Create(session, keys).NextTrackAsync();
        Assert.Equal(skips, session.NextCalls);
        Assert.Equal(seeks, session.Seeks.Count);
        if (seeks > 0) Assert.Equal(TimeSpan.FromSeconds(end).Ticks, session.Seeks[0]);
        Assert.Equal(key ? new[] { Win32Interop.VK_MEDIA_NEXT_TRACK } : Array.Empty<byte>(), keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextHandlesMissingControlsAndUnavailableTimelines(bool controlsThrow)
    {
        var session = new Session { Controls = null, ThrowControls = controlsThrow, Timeline = null };
        var keys = new List<byte>();
        await Create(session, keys).NextTrackAsync();
        Assert.Equal(Win32Interop.VK_MEDIA_NEXT_TRACK, Assert.Single(keys));
        Assert.Empty(session.Seeks);
    }

    [Fact]
    public async Task PreviousRestartsOnceThenDoubleClickSkipsAndAChangedSessionRestartsIndependently()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = new Session { Timeline = Timeline(10, 100, 20), SourceAppUserModelId = "fixture.exe" };
        Session current = first;
        var keys = new List<byte>();
        var service = new MediaTransportControlService(() => current, keys.Add, _ => false, _ => false, () => now);
        await service.PreviousTrackAsync();
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, Assert.Single(first.Seeks));
        Assert.Equal(0, first.PreviousCalls);
        now = now.AddSeconds(1);
        await service.PreviousTrackAsync();
        Assert.Equal(1, first.PreviousCalls);
        current = new Session { Timeline = Timeline(0, 100, 20), SourceAppUserModelId = "other.exe" };
        await service.PreviousTrackAsync();
        Assert.Equal(0, Assert.Single(current.Seeks));
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(0, 3.01, 0)]
    [InlineData(10, 12, 1)]
    [InlineData(10, 14, 0)]
    [InlineData(-10, 5, 0)]
    public async Task PreviousUsesTheReportedPositionRelativeToTheTimelineStart(double start, double position, int expectedSkips)
    {
        var session = new Session { Timeline = Timeline(start, 100, position) };
        var keys = new List<byte>();
        await Create(session, keys).PreviousTrackAsync();
        Assert.Equal(expectedSkips, session.PreviousCalls);
        if (expectedSkips == 0) Assert.Equal(TimeSpan.FromSeconds(Math.Max(0, start)).Ticks, Assert.Single(session.Seeks));
        Assert.Empty(keys);
    }

    [Fact]
    public async Task FailedRestartTriesTheTimelineEdgeBeforeSkippingPrevious()
    {
        var session = new Session { Timeline = Timeline(10, 100, 40), SeekResults = new Queue<bool>([false, true]) };
        var keys = new List<byte>();
        await Create(session, keys).PreviousTrackAsync();
        Assert.Equal(new[] { TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(10).Ticks }, session.Seeks);
        Assert.Equal(0, session.PreviousCalls);
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserPreviousUsesTheMatchedTabAndDoesNotBroadcastAKeyWhenBackIsUnavailable(bool navigated)
    {
        var session = new Session { Controls = new(false, false), Timeline = Timeline(0, 100, 1), Title = "Fixture video" };
        var keys = new List<byte>();
        MediaInfo? requested = null;
        var service = new MediaTransportControlService(() => session, keys.Add, _ => true, info => { requested = info; return navigated; });
        await service.PreviousTrackAsync();
        Assert.Equal("Fixture video", requested!.CurrentTrack);
        Assert.Equal(session.SourceAppUserModelId, requested.SourceAppId);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task BrowserNavigationCannotUseTheTitleOfASessionThatWasReplacedDuringMetadataFetch()
    {
        var title = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Session { Controls = null, Timeline = null, ReadTitle = () => { fetched.TrySetResult(); return title.Task; } };
        Session current = old;
        var keys = new List<byte>();
        int backCalls = 0;
        var service = new MediaTransportControlService(() => current, keys.Add, _ => true, _ => { backCalls++; return true; });
        Task operation = service.PreviousTrackAsync();
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current = new Session();
        title.SetResult("Old video");
        await operation;
        Assert.Equal(0, backCalls);
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData("relative", -90, 0)]
    [InlineData("relative", 10, 50)]
    [InlineData("relative", 90, 50)]
    [InlineData("absolute", -20, 0)]
    [InlineData("absolute", 10, 10)]
    [InlineData("absolute", 90, 50)]
    [InlineData("direct", -20, -20)]
    public async Task SeekModesRespectTheirTimelineAndForwardExactTicks(string mode, double seconds, double expected)
    {
        var session = new Session { Timeline = Timeline(0, 50, 40) };
        var keys = new List<byte>();
        var service = Create(session, keys);
        if (mode == "relative") await service.SeekRelativeAsync(seconds);
        else if (mode == "absolute") await service.SeekToAbsoluteAsync(TimeSpan.FromSeconds(seconds));
        else await service.SeekAsync(TimeSpan.FromSeconds(seconds));
        Assert.Equal(TimeSpan.FromSeconds(expected).Ticks, Assert.Single(session.Seeks));
        Assert.Empty(keys);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("direct")]
    public async Task CommandFailuresAreContainedAndOnlyTransportCommandsSendFallbackKeys(string command)
    {
        var session = new Session { ThrowCommands = true, Timeline = Timeline(0, 10, 1) };
        var keys = new List<byte>();
        await Execute(Create(session, keys), command);
        if (command is "play" or "next" or "previous") Assert.Single(keys);
        else Assert.Empty(keys);
    }

    private static MediaTransportControlService Create(Session session, List<byte> keys) => new(() => session, keys.Add, _ => false, _ => false);
    private static MediaTransportTimeline Timeline(double start, double end, double position) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), TimeSpan.FromSeconds(position));
    private static Task Execute(MediaTransportControlService service, string command) => command switch
    {
        "play" => service.PlayPauseAsync(),
        "next" => service.NextTrackAsync(),
        "previous" => service.PreviousTrackAsync(),
        "relative" => service.SeekRelativeAsync(2),
        "absolute" => service.SeekToAbsoluteAsync(TimeSpan.FromSeconds(2)),
        _ => service.SeekAsync(TimeSpan.FromSeconds(2))
    };

    private sealed class Session : IMediaTransportSession
    {
        public object Identity => this;
        public string SourceAppUserModelId { get; set; } = "fixture.exe";
        internal MediaTransportControls? Controls { get; set; } = new(true, true);
        internal MediaTransportTimeline? Timeline { get; set; } = MediaTransportControlServiceTests.Timeline(0, 100, 0);
        internal bool ToggleResult { get; init; } = true;
        internal bool NextResult { get; init; } = true;
        internal bool SeekResult { get; init; } = true;
        internal Queue<bool>? SeekResults { get; init; }
        internal bool ThrowControls { get; init; }
        internal bool ThrowCommands { get; init; }
        internal string? Title { get; init; }
        internal Func<Task<string?>>? ReadTitle { get; init; }
        internal int ToggleCalls { get; private set; }
        internal int NextCalls { get; private set; }
        internal int PreviousCalls { get; private set; }
        internal List<long> Seeks { get; } = [];
        public MediaTransportControls? GetControls() => ThrowControls ? throw new InvalidOperationException("Disconnected controls") : Controls;
        public MediaTransportTimeline? GetTimelineProperties() => Timeline;
        public Task<bool> TryTogglePlayPauseAsync() { ToggleCalls++; return Result(ToggleResult); }
        public Task<bool> TrySkipNextAsync() { NextCalls++; return Result(NextResult); }
        public Task<bool> TrySkipPreviousAsync() { PreviousCalls++; return Result(true); }
        public Task<bool> TryChangePlaybackPositionAsync(long ticks) { Seeks.Add(ticks); return Result(SeekResults?.Dequeue() ?? SeekResult); }
        public Task<string?> GetTitleAsync() => ReadTitle?.Invoke() ?? Task.FromResult(Title);
        private Task<bool> Result(bool result) => ThrowCommands ? Task.FromException<bool>(new InvalidOperationException("Disconnected command")) : Task.FromResult(result);
    }
}
