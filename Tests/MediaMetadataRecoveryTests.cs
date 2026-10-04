using System.IO;
using System.Reflection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaMetadataRecoveryTests
{
    [Theory]
    [InlineData(DetectionMode.AwaitingMetadata, true)]
    [InlineData(DetectionMode.EventDriven, false)]
    [InlineData(DetectionMode.Idle, false)]
    public void AwaitingMetadataUsesTheLongerDesktopScanCache(DetectionMode mode, bool expected)
    {
        using var fixture = new Fixture();
        Set(fixture.Service, "_currentMode", mode);
        Invoke(fixture.Service, "GetAllWindowTitles");
        Assert.Equal(expected, fixture.Scanner.LastThrottled);
    }

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Theory]
    [InlineData("Song - YouTube", "Song", "YouTube", "YouTube")]
    [InlineData("Singer - Song - SoundCloud", "Song", "Singer", "SoundCloud")]
    [InlineData("Song - by Famous Fixture Artist - SoundCloud", "Song", "by Famous Fixture Artist", "SoundCloud")]
    [InlineData("Song - SoundCloud", "Song", "SoundCloud", "SoundCloud")]
    public void EmptyBrowserMetadataUsesMatchingPlatformTitles(string title, string track, string artist, string platform)
    {
        using var fixture = new Fixture();
        var info = fixture.Info();
        Invoke(fixture.Service, "ApplyWindowTitleFallback", info, new List<string> { "YouTube", "YouTube - Home", title });
        Assert.Equal(track, info.CurrentTrack);
        Assert.Equal(artist, info.CurrentArtist);
        Assert.Equal(platform, info.MediaSource);
        Assert.True(info.IsAnyMediaPlaying);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void YouTubeTitleKeepsOnlyARecentlyConfirmedArtist(bool recent)
    {
        using var fixture = new Fixture();
        Set(fixture.Service, "_stableArtist", "Known singer");
        Set(fixture.Service, "_lastSourceConfirmedTime", DateTime.UtcNow.AddSeconds(recent ? -1 : -30));
        var info = fixture.Info();
        Invoke(fixture.Service, "ApplyWindowTitleFallback", info, new List<string> { "Song - YouTube" });
        Assert.Equal(recent ? "Known singer" : "YouTube", info.CurrentArtist);
    }

    [Fact]
    public void SpotifyWindowTitleRecoversAnEmptySessionAndPreventsBrowserFallback()
    {
        using var fixture = new Fixture("Singer - Song");
        var info = fixture.Info();
        Invoke(fixture.Service, "ApplyWindowTitleFallback", info, new List<string> { "Different - YouTube" });
        Assert.Equal("Spotify", info.MediaSource);
        Assert.Equal("Singer", info.CurrentArtist);
        Assert.Equal("Song", info.CurrentTrack);
        Assert.True(info.IsSpotifyPlaying);
        Assert.True(info.IsPlaying);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, false)]
    public void ABriefMetadataGapCanRecoverButAnOldStoppedSessionCannot(double age, bool recovered)
    {
        using var fixture = new Fixture();
        Set(fixture.Service, "_lastSource", "YouTube");
        Set(fixture.Service, "_emptyMetadataStartTime", DateTime.UtcNow.AddSeconds(-age));
        var info = fixture.Info();
        info.IsAnyMediaPlaying = false;
        Invoke(fixture.Service, "ApplyWindowTitleFallback", info, new List<string> { "Song - YouTube" });
        Assert.Equal(recovered ? "Song" : "", info.CurrentTrack);
    }

    [Theory]
    [InlineData("Old song", "Singer", "Singer - New song", "New song", "Singer")]
    [InlineData("Song", "Singer - Wrong", "Singer - Song", "Song", "Singer")]
    [InlineData("Song", "Singer", "Singer - Song (Live recording)", "Song (Live recording)", "Singer")]
    [InlineData("Song", "Singer - Other", "Singer - Other - Song", "Song", "Singer - Other")]
    public void SpotifyGroundTruthRepairsStaleOrTruncatedMetadata(string track, string artist, string title, string expectedTrack, string expectedArtist)
    {
        var info = new MediaInfo { CurrentTrack = track, CurrentArtist = artist };
        object?[] arguments = [info, title, null];
        typeof(MediaDetectionService).GetMethod("ApplySpotifyGroundTruthCorrection", Private)!.Invoke(null, arguments);
        Assert.Equal(expectedTrack, info.CurrentTrack);
        Assert.Equal(expectedArtist, info.CurrentArtist);
        Assert.Null(arguments[2]);
    }

    [Theory]
    [InlineData("YouTube", "Song - YouTube")]
    [InlineData("SoundCloud", "Song - SoundCloud")]
    [InlineData("Spotify", "Song - Spotify")]
    public void BrowserResolutionPublishesAndCachesTheMatchedPlatform(string platform, string title)
    {
        using var fixture = new Fixture();
        fixture.Scanner.Titles.Add(title);
        var info = fixture.Info("Song");
        Resolve(fixture, info);
        Assert.Equal(platform, info.MediaSource);
        Assert.Equal(platform, Get<string>(fixture.Service, "_stableSource"));
        Assert.True(fixture.Cache.TryGet(MediaHeuristics.BuildTrackIdentity("Song", ""), out var cached));
        Assert.Equal(platform, cached);
        Assert.Equal(platform == "YouTube", info.IsYouTubeRunning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoundCloudOverrideAndTrackCacheNeedAWindowThatMatchesTheCurrentTrack(bool sessionOverride)
    {
        using var fixture = new Fixture();
        var info = fixture.Info("Song");
        if (sessionOverride) Invoke(fixture.Service, "SetSessionSourceOverride", info, "SoundCloud");
        else fixture.Cache.SetBoth(MediaHeuristics.BuildTrackIdentity("Song", ""), MediaHeuristics.BuildTrackIdentity("Song", ""), "SoundCloud");
        fixture.Scanner.Titles.Add("Song - SoundCloud");
        Resolve(fixture, info);
        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
        fixture.Scanner.Titles.Clear();
        fixture.Scanner.Titles.Add("Song - YouTube");
        info.MediaSource = "Browser";
        Resolve(fixture, info);
        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Fact]
    public void AStableSourceSurvivesMissingTitlesAndAYouTubeAdTransition()
    {
        using var fixture = new Fixture();
        var info = fixture.Info("Song");
        Set(fixture.Service, "_stableSource", "YouTube");
        Set(fixture.Service, "_stableSourceTrackIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
        Set(fixture.Service, "_lastPublishedSessionInstanceKey", info.SessionInstanceKey);
        Resolve(fixture, info);
        Assert.Equal("YouTube", info.MediaSource);
        info.MediaSource = "Browser";
        info.CurrentTrack = "Advertisement";
        fixture.Scanner.Titles.Add("Original - YouTube");
        Resolve(fixture, info, completingAd: true);
        Assert.Equal("YouTube", info.MediaSource);
    }

    [Theory]
    [InlineData("Song - YouTube", "Song", "YouTube")]
    [InlineData("Singer - Song - YouTube", "Song", "Singer")]
    [InlineData("Song - by Famous Fixture Artist - YouTube", "Song", "by Famous Fixture Artist")]
    public void FrozenEmptyVideoTimelineRecoversTheNewTrackFromWindowMetadata(string title, string track, string artist)
    {
        using var fixture = new Fixture();
        var info = fixture.Info();
        info.MediaSource = "YouTube";
        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, new List<string> { "YouTube", title });
        Assert.Equal(track, info.CurrentTrack);
        Assert.Equal(artist, info.CurrentArtist);
        Assert.Equal(TimeSpan.FromSeconds(1.5), info.Position);
        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.True(info.IsThrottled);
        Assert.True(Get<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").IsThrottled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void FrozenEmptyVideoTimelineReusesRecoveredDurationForTheExistingTrack(int recoveredSeconds)
    {
        using var fixture = new Fixture();
        var info = fixture.Info();
        info.MediaSource = "YouTube";
        Set(fixture.Service, "_lastTrackName", "Song");
        Set(fixture.Service, "_lastMetadataChangeTime", DateTime.UtcNow.AddSeconds(-20));
        Get<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").RecoveredDuration = TimeSpan.FromSeconds(recoveredSeconds);
        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, new List<string> { "Song - YouTube" });
        Assert.Equal("Song", info.CurrentTrack);
        Assert.Equal(TimeSpan.FromSeconds(recoveredSeconds), info.Duration);
        Assert.InRange(info.Position.TotalSeconds, 19, 25);
        Assert.True(info.IsThrottled);
    }

    [Fact]
    public void SimulatedVideoPlaybackReturnsToNativeTimingWhenPositionAdvances()
    {
        using var fixture = new Fixture();
        var info = fixture.Info("Song");
        info.MediaSource = "YouTube";
        var simulator = Get<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator");
        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, new List<string>());
        Assert.True(simulator.IsThrottled);
        info.Position = TimeSpan.FromSeconds(10);
        info.Duration = TimeSpan.FromSeconds(200);
        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, new List<string>());
        Assert.False(simulator.IsThrottled);
        simulator.EnterThrottledMode();
        info.IsPlaying = false;
        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, new List<string>());
        Assert.False(simulator.IsThrottled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void YouTubeTransitionsQuarantineOnlyTheRecentBrowserCandidate(bool junk)
    {
        using var fixture = new Fixture();
        Set(fixture.Service, "_lastSource", "YouTube");
        Set(fixture.Service, "_lastStableTrackSignature", "original");
        Set(fixture.Service, "_lastPublishedSessionInstanceKey", "previous-session");
        fixture.Scanner.Titles.Add("Original video - YouTube");
        var info = fixture.Info("Advertisement");
        info.Duration = TimeSpan.FromSeconds(30);
        string method = junk ? "EvaluateYouTubeJunkTransition" : "EvaluateLikelyYouTubeAdTransition";
        var decision = (BrowserAdTransitionDecision)Invoke(fixture.Service, method, info, "chrome", (Func<List<string>>)(() => fixture.Scanner.Titles))!;
        Assert.True(decision.ShouldHold);
        Assert.NotEqual(DateTime.MinValue, Get<DateTime>(fixture.Service, "_youtubeAdTransitionStartedUtc"));
        Set(fixture.Service, "_youtubeAdTransitionStartedUtc", DateTime.UtcNow.AddSeconds(-50));
        decision = (BrowserAdTransitionDecision)Invoke(fixture.Service, method, info, "chrome", (Func<List<string>>)(() => fixture.Scanner.Titles))!;
        Assert.False(decision.ShouldHold);
        decision = (BrowserAdTransitionDecision)Invoke(fixture.Service, method, info, "Spotify", (Func<List<string>>)(() => throw new Exception("Desktop sessions must not scan browser windows")))!;
        Assert.False(decision.ShouldHold);
        Assert.Equal(DateTime.MinValue, decision.TransitionStartedUtc);
    }

    private static void Resolve(Fixture fixture, MediaInfo info, bool completingAd = false) => Invoke(fixture.Service, "ResolveBrowserMediaSource", info, "chrome", (Func<List<string>>)(() => fixture.Scanner.Titles), completingAd);
    private static object? Invoke(MediaDetectionService service, string name, params object?[] arguments) => typeof(MediaDetectionService).GetMethod(name, Private)!.Invoke(service, arguments);
    private static T Get<T>(MediaDetectionService service, string name) => (T)typeof(MediaDetectionService).GetField(name, Private)!.GetValue(service)!;
    private static void Set(MediaDetectionService service, string name, object value) => typeof(MediaDetectionService).GetField(name, Private)!.SetValue(service, value);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("vnotch-recovery-").FullName;
        public Scanner Scanner { get; } = new();
        public MediaSourceCache Cache { get; }
        public MediaDetectionService Service { get; }
        public Fixture(string spotifyTitle = "")
        {
            Cache = new MediaSourceCache(Path.Combine(_directory, "sources.json"));
            Service = new MediaDetectionService(new MediaMetadataLookupService(), new FakeMediaArtworkService(), Scanner, null, Cache, () => spotifyTitle);
        }
        public MediaInfo Info(string track = "") => new() { CurrentTrack = track, MediaSource = "Browser", SourceAppId = "chrome", SessionInstanceKey = "fixture-session", IsPlaying = true, IsAnyMediaPlaying = true };
        public void Dispose()
        {
            Service.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Scanner : IWindowTitleScanner
    {
        public List<string> Titles { get; } = new();
        public bool LastThrottled { get; private set; }
        public List<string> GetAllWindowTitles(bool isThrottled) { LastThrottled = isThrottled; return Titles; }
        public string? TryGetBrowserUrl() => null;
        public string? TryGetMediaUrlFromAnyBrowser() => null;
        public bool IsSpotifyWebPlayerOpen() => true;
        public bool IsPipActive(string? processName = null) => false;
        public bool TryGetPipWindow(out IntPtr hwnd, out string title, string? processName = null) { hwnd = IntPtr.Zero; title = ""; return false; }
        public void InvalidateUrlCaches() { }
    }
}
