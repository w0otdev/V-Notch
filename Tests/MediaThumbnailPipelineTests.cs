using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using Windows.Storage.Streams;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MediaThumbnailPipelineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void DelayedSessionArtworkReplacesCachedCoverWithoutAnotherTitleChange(string source) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        info.SourceAppId = "Helium";
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info);
        var first = Bitmap(150, 150, detailed: true);
        await ApplyArtwork(fixture.Service, info, first, true);
        var corrected = Bitmap(151, 151, detailed: true);
        await ApplyArtwork(fixture.Service, info, corrected, false);
        Assert.Same(corrected, info.Thumbnail);
        Assert.Same(corrected, fixture.Service.CachedThumbnail);
        await ApplyArtwork(fixture.Service, info, Bitmap(151, 151, detailed: true), false);
        Assert.Same(corrected, info.Thumbnail); // Repeated snapshots must not restart UI animations.
    });

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void NextTrackRejectsPreviousSessionCoverUntilNewArtworkArrives(string source) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        info.SourceAppId = "Helium";
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info);
        await ApplyArtwork(fixture.Service, info, Bitmap(150, 150, detailed: true), true);
        info.CurrentTrack = "Next track";
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info);
        await ApplyArtwork(fixture.Service, info, Bitmap(150, 150, detailed: true), true);
        Assert.Null(info.Thumbnail);
        Assert.Null(fixture.Service.CachedThumbnail);
        await ApplyArtwork(fixture.Service, info, Bitmap(150, 150, detailed: true), false);
        Assert.Null(info.Thumbnail);
        info.CurrentTrack = "Rapid next track";
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info);
        await ApplyArtwork(fixture.Service, info, Bitmap(150, 150, detailed: true), true);
        Assert.Null(info.Thumbnail);
        var current = Bitmap(151, 151, detailed: true);
        await ApplyArtwork(fixture.Service, info, current, false);
        Assert.Same(current, info.Thumbnail);
    });

    [Fact]
    public void SessionDecodeCompletingAfterNextCannotOverwriteCurrentArtwork() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var old = fixture.Track("SoundCloud");
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", old);
        var pending = new TaskCompletionSource<BitmapSource?>();
        var read = (Task)Invoke(fixture.Service, "ApplySessionArtworkAsync", old, true,
            (Func<Task<BitmapSource?>>)(() => pending.Task))!;
        var current = old.Clone();
        current.CurrentTrack = "Next track";
        Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", current);
        var cover = Bitmap(151, 151, detailed: true);
        await ApplyArtwork(fixture.Service, current, cover, true);
        pending.SetResult(Bitmap(150, 150, detailed: true));
        await read;
        Assert.Same(cover, fixture.Service.CachedThumbnail);
        Assert.Null(old.Thumbnail);
    });

    private static Task ApplyArtwork(MediaDetectionService service, MediaInfo info, BitmapSource artwork, bool changed)
        => (Task)Invoke(service, "ApplySessionArtworkAsync", info, changed,
            (Func<Task<BitmapSource?>>)(() => Task.FromResult<BitmapSource?>(artwork)))!;

    [Fact]
    public void PendingSoundCloudDownloadCannotReplaceNewSessionArtwork() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("SoundCloud");
        fixture.PublishIdentity(info);
        fixture.Metadata.SoundCloudSearch = "https://fixture.invalid/fallback.jpg";
        var download = new TaskCompletionSource<BitmapImage?>();
        fixture.Artwork.Download = _ => download.Task;
        var fetch = FetchSoundCloud(fixture.Service, info, ct);
        Assert.Single(fixture.Artwork.Downloads);
        var current = Bitmap(150, 150, detailed: true);
        await ApplyArtwork(fixture.Service, info, current, false);
        download.SetResult(Bitmap(500, 500));
        await fetch;
        Assert.Same(current, fixture.Service.CachedThumbnail);
        Assert.Same(current, info.Thumbnail);
        Assert.Empty(fixture.Updates);
    });

    [Theory]
    [InlineData("background", false)]
    [InlineData("foreground", false)]
    [InlineData("cache", false)]
    [InlineData("lookup", false)]
    [InlineData("search", false)]
    [InlineData("background", true)]
    [InlineData("foreground", true)]
    [InlineData("cache", true)]
    [InlineData("lookup", true)]
    [InlineData("search", true)]
    public void YouTubeDiscoveryRecoversMetadataAndArtworkWithoutInventingLiveDuration(string discovery, bool live) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        info.Duration = live ? TimeSpan.Zero : TimeSpan.FromSeconds(120);
        info.IsIndeterminate = live;
        fixture.PublishIdentity(info);
        var result = Result(info.CurrentTrack);
        fixture.Metadata.UrlResult = result;
        if (discovery == "background") fixture.Scanner.AnyUrl = "https://youtu.be/abcdefghijk";
        if (discovery == "foreground") fixture.Scanner.CurrentUrl = "https://music.youtube.com/watch?v=abcdefghijk";
        if (discovery == "cache") Invoke(fixture.Service, "CacheVideoIdForTrack", info.CurrentTrack, "abcdefghijk");
        if (discovery == "lookup") fixture.Metadata.TrackResult = result;
        if (discovery == "search") fixture.Metadata.SearchResult = result;
        fixture.Artwork.Image = Bitmap(640, 360);
        await FetchYouTube(fixture.Service, info, ct);
        Assert.Equal("abcdefghijk", info.YouTubeVideoId);
        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
        Assert.Equal(result.Author, info.CurrentArtist);
        Assert.Equal(live ? TimeSpan.Zero : TimeSpan.FromSeconds(123), info.Duration);
        Assert.Equal(info.Duration, Field<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").RecoveredDuration);
        Assert.Same(fixture.Artwork.Image, fixture.Service.CachedThumbnail);
        Assert.Same(fixture.Artwork.Image, info.Thumbnail);
        Assert.Equal("YouTube", fixture.Artwork.Crops.Single().Source);
        Assert.True(fixture.Artwork.Crops.Single().Center);
        Assert.Equal(new[] { "https://fixture.invalid/preferred.jpg" }, fixture.Artwork.Downloads);
        Assert.Contains(fixture.Updates, update => update.IsThumbnailOnlyUpdate && update.YouTubeVideoId == "abcdefghijk");
        Assert.True(fixture.Cache.HasSource(info.CurrentTrack, "", "YouTube"));
        Assert.Equal(0, Field<int>(fixture.Service, "_youTubeFetchInFlight"));
    });

    [Theory]
    [InlineData("background", true)]
    [InlineData("foreground", true)]
    [InlineData("cache", true)]
    [InlineData("lookup", true)]
    [InlineData("search", true)]
    [InlineData("lookup", false)]
    [InlineData("lookup-empty-author", true)]
    [InlineData("poll", true)]
    public void YouTubeDiscoveryRecoversMissingDurationBeforePublishingArtwork(string discovery, bool pending) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        info.IsTimelinePending = pending;
        fixture.PublishIdentity(info);
        Set(fixture.Service, "_lastPublishedInfo", info.Clone());
        var result = discovery == "lookup-empty-author"
            ? new YouTubeLookupResult { Id = "abcdefghijk", Title = info.CurrentTrack, Duration = TimeSpan.FromSeconds(123) }
            : Result(info.CurrentTrack);
        fixture.Metadata.UrlResult = result;
        if (discovery == "background") fixture.Scanner.AnyUrl = "https://youtu.be/abcdefghijk";
        if (discovery == "foreground") fixture.Scanner.CurrentUrl = "https://music.youtube.com/watch?v=abcdefghijk";
        if (discovery == "cache") Invoke(fixture.Service, "CacheVideoIdForTrack", info.CurrentTrack, "abcdefghijk");
        if (discovery is "lookup" or "lookup-empty-author") fixture.Metadata.TrackResult = result;
        if (discovery == "search") fixture.Metadata.SearchResult = result;
        if (discovery == "poll") fixture.Scanner.OnInvalidated = () => fixture.Scanner.AnyUrl = "https://youtu.be/abcdefghijk";
        fixture.Artwork.Image = Bitmap(640, 360);

        await FetchYouTube(fixture.Service, info, ct);

        Assert.Equal(result.Duration, info.Duration);
        Assert.False(info.IsTimelinePending);
        Assert.False(info.IsTimelineIndeterminate);
        Assert.Equal(result.Duration, Field<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").RecoveredDuration);
        Assert.Contains(fixture.Updates, update => !update.IsThumbnailOnlyUpdate && update.Duration == result.Duration && !update.IsTimelineIndeterminate);
        Assert.Contains(fixture.Updates, update => update.IsThumbnailOnlyUpdate && update.HasThumbnail);
    });

    [Fact]
    public void NewBrowserTrackWithMissingDurationWaitsForItsTimeline() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        Set(fixture.Service, "_lastTrackSignature", "previous track");
        Set(fixture.Service, "_lastMetadataChangeTime", now.AddSeconds(-10));
        var info = fixture.Track("YouTube");
        info.IsIndeterminate = true; // The solver cannot distinguish a missing duration from live.

        fixture.Service.ApplyTimelineAvailability(info, true, true, false, TimeSpan.Zero, now);

        Assert.True(info.IsTimelinePending);
        Assert.False(info.IsIndeterminate);
        Assert.True(info.IsTimelineIndeterminate);
        Assert.False(info.HasTimeline);
    });

    [Fact]
    public void BrowserWithoutDurationFallsBackToLiveAfterMetadataSettles() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        Set(fixture.Service, "_lastTrackSignature", "current track");
        Set(fixture.Service, "_lastMetadataChangeTime", now.AddSeconds(-3));
        var info = fixture.Track("YouTube");
        info.IsIndeterminate = true;
        fixture.Service.ApplyTimelineAvailability(info, true, false, false, TimeSpan.Zero, now);
        Assert.True(info.IsTimelinePending);
        Assert.True(info.IsTimelineIndeterminate);

        info.IsIndeterminate = true;
        fixture.Service.ApplyTimelineAvailability(info, true, false, false, TimeSpan.Zero, now.AddSeconds(2));

        Assert.False(info.IsTimelinePending);
        Assert.False(info.IsIndeterminate);
        Assert.True(info.IsTimelineIndeterminate);
    });

    [Fact]
    public void RepeatedMissingBrowserTimelineRetainsTheCurrentVideosDuration() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var last = fixture.Track("YouTube");
        last.Duration = TimeSpan.FromSeconds(123);
        Set(fixture.Service, "_lastPublishedInfo", last.Clone());
        var info = last.Clone();
        info.Duration = TimeSpan.Zero;
        info.IsIndeterminate = true;

        fixture.Service.ApplyTimelineAvailability(info, true, false, false, TimeSpan.Zero, DateTime.UtcNow);

        Assert.Equal(last.Duration, info.Duration);
        Assert.False(info.IsTimelinePending);
        Assert.False(info.IsTimelineIndeterminate);
        Assert.True(info.HasTimeline);
    });

    [Theory]
    [InlineData("track")]
    [InlineData("source")]
    [InlineData("session")]
    public void MissingBrowserDurationCannotBorrowAnotherVideosTimeline(string changed) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var last = fixture.Track("YouTube");
        last.Duration = TimeSpan.FromSeconds(123);
        Set(fixture.Service, "_lastPublishedInfo", last.Clone());
        var info = last.Clone();
        info.Duration = TimeSpan.Zero;
        info.IsIndeterminate = true;
        if (changed == "track") info.CurrentTrack = "Next video";
        if (changed == "source") info.SourceAppId = "msedge";
        if (changed == "session") info.SessionInstanceKey = "other-tab";
        var now = DateTime.UtcNow;
        Set(fixture.Service, "_lastTrackSignature", "previous track");
        Set(fixture.Service, "_lastMetadataChangeTime", now.AddSeconds(-10));

        fixture.Service.ApplyTimelineAvailability(info, true, changed == "track", false, TimeSpan.Zero, now);

        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.True(info.IsTimelinePending);
        Assert.True(info.IsTimelineIndeterminate);

        if (changed != "track")
        {
            Set(fixture.Service, "_lastPublishedInfo", info.Clone());
            info.IsIndeterminate = true;
            fixture.Service.ApplyTimelineAvailability(info, true, false, false, TimeSpan.Zero, now.AddSeconds(1));
            Assert.True(info.IsTimelinePending);
            Assert.Equal(TimeSpan.Zero, info.Duration);
        }
    });

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3456000)]
    public void ConfirmedLiveTimelineCannotReuseCachedVodDuration(bool classifiedLive, double reportedDuration) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var last = fixture.Track("YouTube");
        last.Duration = TimeSpan.FromSeconds(123);
        Set(fixture.Service, "_lastPublishedInfo", last.Clone());
        var info = last.Clone();
        info.Duration = TimeSpan.Zero;
        info.IsIndeterminate = true;

        fixture.Service.ApplyTimelineAvailability(info, true, false, classifiedLive, TimeSpan.FromSeconds(reportedDuration), DateTime.UtcNow);

        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.False(info.IsTimelinePending);
        Assert.True(info.IsIndeterminate);
        Assert.True(info.IsTimelineIndeterminate);
    });

    [Fact]
    public void ConfirmedLiveSurvivesAMissingTimelineWithoutAcceptingVodMetadata() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var last = fixture.Track("YouTube");
        last.Duration = TimeSpan.FromHours(2);
        last.IsIndeterminate = true;
        fixture.PublishIdentity(last);
        Set(fixture.Service, "_lastPublishedInfo", last.Clone());
        var info = last.Clone();
        info.Duration = TimeSpan.Zero;
        fixture.Service.ApplyTimelineAvailability(info, true, false, false, TimeSpan.Zero, DateTime.UtcNow);
        Assert.False(info.IsTimelinePending);
        Assert.True(info.IsIndeterminate);
        fixture.Metadata.TrackResult = Result(info.CurrentTrack);
        fixture.Artwork.Image = Bitmap(640, 360);

        await FetchYouTube(fixture.Service, info, ct);

        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.True(info.IsTimelineIndeterminate);
        Assert.Equal(TimeSpan.Zero, Field<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").RecoveredDuration);
        Assert.True(info.HasThumbnail);
    });

    [Fact]
    public void MissingBrowserTimelineDuringTransitionIsPending() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("YouTube");

        await (Task)Invoke(fixture.Service, "ApplyTimelinePropertiesAsync", info, null, false)!;

        Assert.True(info.IsTimelinePending);
        Assert.False(info.IsIndeterminate);
        Assert.True(info.IsTimelineIndeterminate);
    });

    [Fact]
    public void WindowTitleRecoveryWaitsForTheNewVideosDuration() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        info.CurrentTrack = "Previous video";
        info.Duration = TimeSpan.FromMinutes(2);
        info.IsPlaying = true;
        Set(fixture.Service, "_lastTrackName", info.CurrentTrack);
        Set(fixture.Service, "_lastMetadataChangeTime", DateTime.UtcNow.AddMinutes(-1));
        int scans = 0;
        fixture.Scanner.Titles = () => ++scans == 1
            ? new List<string> { "Previous video - YouTube - Google Chrome" }
            : new List<string> { "Next video - YouTube - Google Chrome" };

        Invoke(fixture.Service, "ApplyVideoTimelineRecovery", info, null);

        Assert.Equal("Next video", info.CurrentTrack);
        Assert.Equal(TimeSpan.Zero, info.Duration);
        Assert.True(info.IsTimelinePending);
        Assert.False(info.IsIndeterminate);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedBrowserVideoIsDiscardedBeforeTitleSearch(bool missingValidation) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        fixture.PublishIdentity(info);
        fixture.Scanner.AnyUrl = "https://www.youtube.com/watch?v=wrongvideo1";
        fixture.Metadata.UrlResult = missingValidation ? null : Result("A different track", "wrongvideo1");
        fixture.Metadata.SearchResult = Result(info.CurrentTrack);
        fixture.Artwork.Image = Bitmap(640, 360);
        await FetchYouTube(fixture.Service, info, ct);
        Assert.Equal("abcdefghijk", info.YouTubeVideoId);
        Assert.Equal(1, fixture.Metadata.SearchCalls);
        Assert.DoesNotContain(fixture.Artwork.Downloads, url => url.Contains("wrongvideo1", StringComparison.Ordinal));
        Assert.Equal(!missingValidation, (bool)Invoke(fixture.Service, "TryGetCachedMismatchVideoId", "wrongvideo1")!);
    });

    [Fact]
    public void YouTubeArtworkFallsBackThroughResolutionVariantsWhenImagesAreMissingOrTooSmall() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("YouTube");
        fixture.PublishIdentity(info);
        fixture.Metadata.TrackResult = Result(info.CurrentTrack);
        var small = Bitmap(128, 72);
        var final = Bitmap(320, 180);
        fixture.Artwork.Download = url =>
        {
            if (url.EndsWith("mqdefault.jpg", StringComparison.Ordinal)) return Task.FromResult<BitmapImage?>(final);
            if (url.EndsWith("hqdefault.jpg", StringComparison.Ordinal)) return Task.FromResult<BitmapImage?>(null);
            return Task.FromResult<BitmapImage?>(small);
        };
        await FetchYouTube(fixture.Service, info, ct);
        Assert.Equal(new[] { "preferred.jpg", "maxresdefault.jpg", "sddefault.jpg", "hqdefault.jpg", "mqdefault.jpg" }, fixture.Artwork.Downloads.Select(url => url.Split('/')[^1]));
        Assert.Same(final, info.Thumbnail);
        Assert.Single(fixture.Updates.Where(update => update.IsThumbnailOnlyUpdate));
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("thumbnail")]
    [InlineData("fetch")]
    [InlineData("track")]
    [InlineData("artist")]
    [InlineData("session")]
    [InlineData("timeline")]
    [InlineData("source")]
    public void PendingYouTubeMetadataCannotOverwriteAChangedOrCancelledTrack(string invalidation) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        info.Duration = TimeSpan.FromSeconds(120);
        fixture.PublishIdentity(info);
        var pending = new TaskCompletionSource<YouTubeLookupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Metadata.Lookup = (_, _, _) => pending.Task;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fetch = FetchYouTube(fixture.Service, info, request.Token);
        Assert.Equal(1, fixture.Metadata.TrackCalls);
        if (invalidation == "cancel") request.Cancel();
        if (invalidation == "thumbnail") Set(fixture.Service, "_thumbnailFetchGeneration", 1);
        if (invalidation == "fetch") Set(fixture.Service, "_youTubeFetchGeneration", 1);
        if (invalidation == "track") Set(fixture.Service, "_lastPublishedTrackIdentity", "different track");
        if (invalidation == "artist") Set(fixture.Service, "_lastPublishedTrackIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, "Different artist"));
        if (invalidation == "session") Set(fixture.Service, "_lastPublishedSessionInstanceKey", "replacement session");
        if (invalidation == "timeline")
        {
            var live = info.Clone();
            live.Duration = TimeSpan.Zero;
            live.IsIndeterminate = true;
            Set(fixture.Service, "_lastPublishedInfo", live);
        }
        if (invalidation == "source")
        {
            var resolved = info.Clone();
            resolved.MediaSource = "SoundCloud";
            Set(fixture.Service, "_lastPublishedInfo", resolved);
        }
        pending.SetResult(Result(info.CurrentTrack));
        await fetch;
        Assert.Null(info.YouTubeVideoId);
        Assert.Equal("Browser", info.MediaSource);
        Assert.Empty(fixture.Artwork.Downloads);
        Assert.Empty(fixture.Updates);
        Assert.Equal(0, fixture.Cache.Count);
    });

    [Fact]
    public void PollingFindsAValidatedBackgroundVideoAfterTheBrowserAddressBarCatchesUp() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        fixture.PublishIdentity(info);
        fixture.Scanner.OnInvalidated = () => fixture.Scanner.AnyUrl = "https://youtu.be/abcdefghijk";
        fixture.Metadata.UrlResult = Result(info.CurrentTrack);
        fixture.Artwork.Image = Bitmap(640, 360);
        await FetchYouTube(fixture.Service, info, ct);
        Assert.Equal("abcdefghijk", info.YouTubeVideoId);
        Assert.True(fixture.Scanner.Invalidations > 0);
        Assert.Single(fixture.Artwork.Downloads);
        Assert.Equal("YouTube", info.MediaSource);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoundCloudUsesValidatedArtworkOrSearchesWhenTheBrowserReturnsAPlaceholder(bool placeholder) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        fixture.PublishIdentity(info);
        fixture.Scanner.AnyUrl = "https://soundcloud.com/artist/test-track";
        fixture.Metadata.SoundCloudUrl = placeholder ? "https://a-v2.sndcdn.com/images/default_avatar_large.png" : "https://fixture.invalid/artwork.jpg";
        fixture.Metadata.SoundCloudSearch = "https://fixture.invalid/search-artwork.jpg";
        fixture.Artwork.Image = Bitmap(640, 360);
        await FetchSoundCloud(fixture.Service, info, ct);
        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
        Assert.Same(fixture.Artwork.Image, info.Thumbnail);
        Assert.Equal(placeholder ? 1 : 0, fixture.Metadata.SoundCloudSearchCalls);
        Assert.Equal(placeholder ? "https://fixture.invalid/search-artwork.jpg" : "https://fixture.invalid/artwork.jpg", fixture.Artwork.Downloads.Single());
        Assert.Equal("SoundCloud", fixture.Artwork.Crops.Single().Source);
        Assert.False(fixture.Artwork.Crops.Single().Center);
        Assert.True(fixture.Cache.HasSource(MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist), "", "SoundCloud"));
        Assert.Single(fixture.Updates);
        Assert.True(fixture.Updates.Single().IsThumbnailOnlyUpdate);
        Assert.Equal(0, Field<int>(fixture.Service, "_soundCloudFetchInFlight"));
    });

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void SoundCloudSessionArtworkIsKeptInsteadOfBeingRelabeledAsYouTube(string source) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        fixture.PublishIdentity(info);
        Invoke(fixture.Service, "SetSessionSourceOverride", info, "SoundCloud");
        var artwork = Bitmap(500, 500);
        info.Thumbnail = artwork;
        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", info, true);
        Assert.Same(artwork, info.Thumbnail);
        Assert.Same(artwork, fixture.Service.CachedThumbnail);
        Assert.Equal("SoundCloud", Field<string>(fixture.Service, "_cachedThumbnailSource"));
        Assert.Equal("SoundCloud", fixture.Artwork.Crops.Single().Source);
        Assert.Equal(0, fixture.Metadata.SoundCloudSearchCalls);
        Assert.Equal(0, fixture.Metadata.TrackCalls);
        Assert.Empty(fixture.Artwork.Downloads);
    });

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void SmallDetailedSessionArtworkIsPublishedWithoutWaitingForOnlineLookup(string source) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        info.SourceAppId = "Helium";
        fixture.PublishIdentity(info);
        var artwork = Bitmap(150, 150, detailed: true);
        Assert.True(ThumbnailHeuristics.IsLikelyArtworkCandidate(artwork));
        Assert.False(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(artwork));
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Accept,
            ThumbnailHeuristics.DecideSmtcThumbnail(new ThumbnailHeuristics.SmtcThumbnailInputs
            {
                IsSoundCloudSource = source == "SoundCloud",
                IsBrowserOrYouTubePlatform = source == "Browser",
                IsBrowserSession = true,
                TrackChanged = true,
                LikelySoundCloudArtwork = true,
                PixelWidth = 150,
                PixelHeight = 150,
            }));

        info.Thumbnail = artwork;
        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", info, true);
        Assert.Same(artwork, info.Thumbnail);
        Assert.Same(artwork, fixture.Service.CachedThumbnail);
        Assert.Equal(source, Field<string>(fixture.Service, "_cachedThumbnailSource"));
        Assert.Equal(0, fixture.Metadata.SoundCloudSearchCalls);
        Assert.Equal(0, fixture.Metadata.TrackCalls);
        Assert.Equal(0, fixture.Metadata.SearchCalls);
        Assert.Empty(fixture.Artwork.Downloads);
    });

    [Fact]
    public void UnidentifiedHeliumPlaybackDoesNotBorrowYouTubeArtworkForASoundCloudTrack() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
        info.SourceAppId = "Helium";
        info.CurrentTrack = "NO POLE ULTIMATE";
        info.CurrentArtist = "NV So Lit";
        info.Duration = TimeSpan.FromSeconds(323.524);
        fixture.PublishIdentity(info);
        fixture.Metadata.TrackResult = Result(info.CurrentTrack);
        fixture.Metadata.SoundCloudSearch = "https://fixture.invalid/soundcloud-cover.jpg";
        fixture.Artwork.Image = Bitmap(500, 500);

        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", info, true);
        await WpfFrameWaiter.UntilAsync(() => fixture.Updates.Any(update => update.IsThumbnailOnlyUpdate), "SoundCloud artwork published", ct);

        Assert.Equal(0, fixture.Metadata.TrackCalls);
        Assert.Equal(0, fixture.Metadata.SearchCalls);
        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.Equal("NV So Lit", info.CurrentArtist);
        Assert.Equal("SoundCloud", Field<string>(fixture.Service, "_cachedThumbnailSource"));
        Assert.Equal("https://fixture.invalid/soundcloud-cover.jpg", Assert.Single(fixture.Artwork.Downloads));
    });

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void ConfirmedSoundCloudArtworkReplacesACachedYouTubeFallback(string source) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        info.SourceAppId = "Helium";
        fixture.PublishIdentity(info);
        Invoke(fixture.Service, "SetSessionSourceOverride", info, "SoundCloud");
        var wrongCover = Bitmap(640, 360);
        info.Thumbnail = wrongCover;
        fixture.Service.CachedThumbnail = wrongCover;
        Set(fixture.Service, "_cachedThumbnailSource", "YouTube");
        fixture.Metadata.SoundCloudSearch = "https://fixture.invalid/soundcloud-cover.jpg";
        var correctCover = Bitmap(500, 500);
        fixture.Artwork.Image = correctCover;

        await FetchSoundCloud(fixture.Service, info, ct);

        Assert.Same(correctCover, info.Thumbnail);
        Assert.Same(correctCover, fixture.Service.CachedThumbnail);
        Assert.Equal("SoundCloud", Field<string>(fixture.Service, "_cachedThumbnailSource"));
        Assert.Single(fixture.Updates.Where(update => update.IsThumbnailOnlyUpdate));
    });

    [Fact]
    public void SoundCloudResolutionDiscardsTheYouTubeFallbackBeforeReusingSessionArtwork() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("SoundCloud");
        fixture.PublishIdentity(info);
        fixture.Service.CachedThumbnail = Bitmap(640, 360);
        Set(fixture.Service, "_cachedThumbnailSource", "YouTube");
        await (Task)Invoke(fixture.Service, "ApplySessionThumbnailAsync", info, null, false)!;
        Assert.Null(info.Thumbnail);
        Assert.Null(fixture.Service.CachedThumbnail);
        Assert.Null(Field<MediaTimelineSimulator>(fixture.Service, "_timelineSimulator").RecoveredThumbnail);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("placeholder")]
    [InlineData("thumbnail-generation")]
    [InlineData("track")]
    [InlineData("youtube")]
    [InlineData("cancel")]
    [InlineData("error")]
    public void RejectedOrStaleSoundCloudArtworkCannotReplaceTheCurrentThumbnail(string reason) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(reason == "youtube" ? "YouTube" : "SoundCloud");
        fixture.PublishIdentity(info);
        fixture.Metadata.SoundCloudSearch = "https://fixture.invalid/artwork.jpg";
        var pending = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Artwork.Download = _ => pending.Task;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fetch = FetchSoundCloud(fixture.Service, info, request.Token);
        if (reason == "thumbnail-generation") Set(fixture.Service, "_thumbnailFetchGeneration", 1);
        if (reason == "track") Set(fixture.Service, "_lastPublishedTrackIdentity", "different track");
        if (reason == "cancel") request.Cancel();
        if (reason == "error")
        {
            pending.SetException(new IOException("fixture image failure"));
        }
        else if (reason == "missing")
        {
            pending.SetResult(null);
        }
        else
        {
            pending.SetResult(reason == "placeholder" ? Bitmap(100, 100) : Bitmap(640, 360));
        }
        await fetch;
        Assert.Null(info.Thumbnail);
        Assert.Null(fixture.Service.CachedThumbnail);
        Assert.Empty(fixture.Updates);
        Assert.Empty(fixture.Artwork.Crops);
        Assert.Equal(0, Field<int>(fixture.Service, "_soundCloudFetchInFlight"));
    });

    [Fact]
    public void NewTrackCancelsThePreviousFetchAndRepeatedRequestsShareOneLookup() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("YouTube");
        fixture.PublishIdentity(info);
        var previous = new TaskCompletionSource<YouTubeLookupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Metadata.Lookup = (title, _, _) => title == info.CurrentTrack ? previous.Task : Task.FromResult<YouTubeLookupResult?>(Result(title));
        fixture.Artwork.Image = Bitmap(640, 360);
        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", info, true);
        await WpfFrameWaiter.UntilAsync(() => fixture.Metadata.TrackCalls == 1, "first artwork lookup", ct);
        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", info, false);
        Assert.Equal(1, fixture.Metadata.TrackCalls);
        var replacement = fixture.Track("YouTube");
        replacement.CurrentTrack = "Replacement track";
        fixture.PublishIdentity(replacement);
        Invoke(fixture.Service, "StartThumbnailFetchIfNeeded", replacement, true);
        await WpfFrameWaiter.UntilAsync(() => fixture.Updates.Any(update => update.IsThumbnailOnlyUpdate && update.CurrentTrack == replacement.CurrentTrack), "replacement artwork published", ct);
        previous.SetResult(Result(info.CurrentTrack));
        await WpfFrameWaiter.NextAsync(ct);
        Assert.DoesNotContain(fixture.Updates, update => update.CurrentTrack == info.CurrentTrack);
        Assert.Same(fixture.Artwork.Image, replacement.Thumbnail);
        Assert.Null(info.Thumbnail);
        Assert.Equal(2, fixture.Metadata.TrackCalls);
        Assert.Equal(0, Field<int>(fixture.Service, "_youTubeFetchInFlight"));
    });

    [Theory]
    [InlineData("YouTube")]
    [InlineData("Browser")]
    public void YouTubeArtworkSurvivesPublishingResolvedArtistWhileDownloadIsPending(string source) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track(source);
        fixture.PublishIdentity(info);
        Set(fixture.Service, "_lastThumbTrackIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, ""));
        fixture.Metadata.TrackResult = Result(info.CurrentTrack);
        var pending = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Artwork.Download = _ => pending.Task;
        var fetch = FetchYouTube(fixture.Service, info, ct);
        await WpfFrameWaiter.UntilAsync(() => fixture.Artwork.Downloads.Count == 1, "artwork download started", ct);
        Assert.Single(fixture.Artwork.Downloads);
        Assert.Equal("Fixture artist - Topic", info.CurrentArtist);
        Assert.False((bool)Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info)!);
        fixture.PublishIdentity(info);
        var image = Bitmap(640, 360);
        pending.SetResult(image);
        await fetch;
        Assert.Same(image, fixture.Service.CachedThumbnail);
        Assert.Same(image, info.Thumbnail);
        Assert.Contains(fixture.Updates, update => update.IsThumbnailOnlyUpdate && ReferenceEquals(update.Thumbnail, image));
        var replacement = info.Clone();
        replacement.CurrentTrack = "Replacement track";
        Assert.True((bool)Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", replacement)!);
        Assert.Null(fixture.Service.CachedThumbnail);
    });

    [Fact]
    public void LateSessionArtworkIsPublishedEvenWhenMetadataAndPlaybackAreUnchanged() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Spotify");
        Set(fixture.Service, "_bgCts", new CancellationTokenSource());
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", info, info.GetSignature(), false, true)!;
        Assert.Single(fixture.Updates);
        info.Thumbnail = Bitmap(300, 300);
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", info, info.GetSignature(), false, false)!;
        Assert.Equal(2, fixture.Updates.Count);
        Assert.Same(info.Thumbnail, fixture.Updates[^1].Thumbnail);
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", info, info.GetSignature(), false, false)!;
        Assert.Equal(2, fixture.Updates.Count);
    });

    [Fact]
    public void PendingTimelineExpiryPublishesWithoutOtherMediaChanges() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var loading = fixture.Track("YouTube");
        loading.IsTimelinePending = true;
        Set(fixture.Service, "_bgCts", new CancellationTokenSource());
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", loading, loading.GetSignature(), false, true)!;
        Assert.Single(fixture.Updates);

        var settled = loading.Clone();
        settled.IsTimelinePending = false;
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", settled, settled.GetSignature(), false, false)!;

        Assert.Equal(2, fixture.Updates.Count);
        Assert.False(fixture.Updates[^1].IsTimelinePending);
        Assert.True(fixture.Updates[^1].IsTimelineIndeterminate);
    });

    [Fact]
    public void TimelineModeChangesPublishWithoutPositionOrMetadataChanges() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var vod = fixture.Track("YouTube");
        vod.Duration = TimeSpan.FromHours(2);
        Set(fixture.Service, "_bgCts", new CancellationTokenSource());
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", vod, vod.GetSignature(), false, true)!;
        Assert.Single(fixture.Updates);

        var live = vod.Clone();
        live.IsIndeterminate = true;
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", live, live.GetSignature(), false, false)!;
        Assert.Equal(2, fixture.Updates.Count);
        Assert.True(fixture.Updates[^1].IsTimelineIndeterminate);

        live.Duration += TimeSpan.FromSeconds(1);
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", live, live.GetSignature(), false, false)!;
        Assert.Equal(2, fixture.Updates.Count);

        var replay = live.Clone();
        replay.IsIndeterminate = false;
        await (Task<bool>)Invoke(fixture.Service, "TryPublishMediaChangeAsync", replay, replay.GetSignature(), false, false)!;
        Assert.Equal(3, fixture.Updates.Count);
        Assert.True(fixture.Updates[^1].HasTimeline);
    });

    [Fact]
    public void NativePlayerChangingArtistForTheSameTitleInvalidatesItsArtwork() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Spotify");
        info.SourceAppId = "Spotify.exe";
        Set(fixture.Service, "_lastThumbTrackIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
        Assert.False((bool)Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info)!);
        info.CurrentArtist = "Different artist";
        Assert.True((bool)Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info)!);
        Assert.False((bool)Invoke(fixture.Service, "InvalidateThumbnailStateIfTrackChanged", info)!);
    });

    private static YouTubeLookupResult Result(string title, string id = "abcdefghijk") => new() { Id = id, Title = title, Author = "Fixture artist - Topic", Duration = TimeSpan.FromSeconds(123), Source = YouTubeLookupSource.DataApi, ThumbnailUrl = "https://fixture.invalid/preferred.jpg" };
    private static Task FetchYouTube(MediaDetectionService service, MediaInfo info, CancellationToken ct) => (Task)Invoke(service, "FetchYouTubeThumbnailAsync", info, ct, false, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey, 0, 0, true, null)!;
    private static Task FetchSoundCloud(MediaDetectionService service, MediaInfo info, CancellationToken ct) => (Task)Invoke(service, "FetchSoundCloudThumbnailAsync", info, ct, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey, 0, 0, true, null)!;
    private static T Field<T>(MediaDetectionService service, string name) => (T)typeof(MediaDetectionService).GetField(name, Private)!.GetValue(service)!;
    private static void Set(MediaDetectionService service, string name, object value) => typeof(MediaDetectionService).GetField(name, Private)!.SetValue(service, value);
    private static object? Invoke(MediaDetectionService service, string name, params object?[] args) => typeof(MediaDetectionService).GetMethod(name, Private)!.Invoke(service, args);

    private static BitmapImage Bitmap(int width, int height, bool detailed = false)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int x = i / 4 % width, y = i / 4 / width;
            pixels[i] = detailed ? (byte)(x * 255 / width) : (byte)180;
            pixels[i + 1] = detailed ? (byte)(y * 255 / height) : (byte)60;
            pixels[i + 2] = detailed ? (byte)((x + y) % 256) : (byte)30;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-thumbnail-tests-" + Guid.NewGuid().ToString("N"));
        public Metadata Metadata { get; } = new();
        public Artwork Artwork { get; } = new();
        public Scanner Scanner { get; } = new();
        public MediaSourceCache Cache { get; }
        public MediaDetectionService Service { get; }
        public List<MediaInfo> Updates { get; } = new();
        public Fixture()
        {
            _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            Cache = new MediaSourceCache(Path.Combine(_directory, "sources.json"));
            Service = new MediaDetectionService(Metadata, Artwork, Scanner, null, Cache);
            var stateField = typeof(MediaDetectionService).GetField("_state", Private)!;
            stateField.SetValue(Service, Enum.Parse(stateField.FieldType, "Running"));
            Service.MediaChanged += (_, update) => Updates.Add(update.Clone());
            Set(Service, "_youTubeFetchInFlight", 1);
            Set(Service, "_soundCloudFetchInFlight", 1);
        }
        public MediaInfo Track(string source) => new() { CurrentTrack = "Fixture track " + Guid.NewGuid().ToString("N"), CurrentArtist = "Fixture artist", MediaSource = source, SourceAppId = "chrome", SessionInstanceKey = "fixture-session" };
        public void PublishIdentity(MediaInfo info)
        {
            Set(Service, "_lastPublishedTrackIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
            Set(Service, "_lastPublishedTrackOnlyIdentity", MediaHeuristics.BuildTrackIdentity(info.CurrentTrack, ""));
            Set(Service, "_lastPublishedSourceAppId", info.SourceAppId);
            Set(Service, "_lastPublishedSessionInstanceKey", info.SessionInstanceKey);
        }
        public void Dispose()
        {
            Service.Dispose();
            string root = Path.GetFullPath(Path.GetTempPath());
            Assert.StartsWith(root, Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class Metadata : IMediaMetadataLookupService
    {
        public YouTubeLookupResult? UrlResult, TrackResult, SearchResult;
        public string? SoundCloudUrl, SoundCloudSearch;
        public int TrackCalls, SearchCalls, SoundCloudSearchCalls;
        public Func<string, string, CancellationToken, Task<YouTubeLookupResult?>>? Lookup;
        public Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default) => Task.FromResult(UrlResult);
        public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default) { Interlocked.Increment(ref TrackCalls); return Lookup?.Invoke(title, artist, ct) ?? Task.FromResult(TrackResult); }
        public Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default) { SearchCalls++; return Task.FromResult(SearchResult); }
        public Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default) => Task.FromResult(SoundCloudUrl);
        public Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default) { SoundCloudSearchCalls++; Assert.True(requireStrongMatch); return Task.FromResult(SoundCloudSearch); }
    }

    private sealed class Artwork : IMediaArtworkService
    {
        public BitmapImage? Image;
        public Func<string, Task<BitmapImage?>>? Download;
        public List<string> Downloads { get; } = new();
        public List<(string Source, bool Center)> Crops { get; } = new();
        public Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default) { Downloads.Add(url); return Download?.Invoke(url) ?? Task.FromResult(Image); }
        public BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false) { Crops.Add((mediaSource, forceCenterCrop)); return source; }
        public Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
        public void ConfigureSmartCrop(bool enabled) { }
        public SubjectBounds? GetDominantSubjectBounds(BitmapSource source) => null;
    }

    private sealed class Scanner : IWindowTitleScanner
    {
        public string? CurrentUrl, AnyUrl;
        public int Invalidations;
        public Action? OnInvalidated;
        public Func<List<string>>? Titles;
        public List<string> GetAllWindowTitles(bool isThrottled) => Titles?.Invoke() ?? new();
        public string? TryGetBrowserUrl() => CurrentUrl;
        public string? TryGetMediaUrlFromAnyBrowser() => AnyUrl;
        public bool IsSpotifyWebPlayerOpen() => false;
        public bool IsPipActive(string? processName = null) => false;
        public bool TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, string? processName = null) { pipHwnd = IntPtr.Zero; pipTitle = ""; return false; }
        public void InvalidateUrlCaches() { Invalidations++; OnInvalidated?.Invoke(); }
    }
}
