using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using Windows.Storage.Streams;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaThumbnailPipelineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("background")]
    [InlineData("foreground")]
    [InlineData("cache")]
    [InlineData("lookup")]
    [InlineData("search")]
    public void YouTubeDiscoveryValidatesTheTrackAndPublishesRecoveredMetadataAndArtwork(string discovery) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
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
        Assert.Equal(TimeSpan.FromSeconds(123), info.Duration);
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
        fixture.Artwork.Download = url => Task.FromResult(url.EndsWith("mqdefault.jpg", StringComparison.Ordinal) ? final : url.EndsWith("hqdefault.jpg", StringComparison.Ordinal) ? null : small);
        await FetchYouTube(fixture.Service, info, ct);
        Assert.Equal(new[] { "preferred.jpg", "maxresdefault.jpg", "sddefault.jpg", "hqdefault.jpg", "mqdefault.jpg" }, fixture.Artwork.Downloads.Select(url => url.Split('/').Last()));
        Assert.Same(final, info.Thumbnail);
        Assert.Single(fixture.Updates.Where(update => update.IsThumbnailOnlyUpdate));
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("thumbnail")]
    [InlineData("fetch")]
    [InlineData("track")]
    [InlineData("session")]
    public void PendingYouTubeMetadataCannotOverwriteAChangedOrCancelledTrack(string invalidation) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var info = fixture.Track("Browser");
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
        if (invalidation == "session") Set(fixture.Service, "_lastPublishedSessionInstanceKey", "replacement session");
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
        if (reason == "error") pending.SetException(new IOException("fixture image failure"));
        else pending.SetResult(reason == "missing" ? null : reason == "placeholder" ? Bitmap(100, 100) : Bitmap(640, 360));
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

    private static YouTubeLookupResult Result(string title, string id = "abcdefghijk") => new() { Id = id, Title = title, Author = "Fixture artist - Topic", Duration = TimeSpan.FromSeconds(123), Source = YouTubeLookupSource.DataApi, ThumbnailUrl = "https://fixture.invalid/preferred.jpg" };
    private static Task FetchYouTube(MediaDetectionService service, MediaInfo info, CancellationToken ct) => (Task)Invoke(service, "FetchYouTubeThumbnailAsync", info, ct, false, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey, 0, 0, true)!;
    private static Task FetchSoundCloud(MediaDetectionService service, MediaInfo info, CancellationToken ct) => (Task)Invoke(service, "FetchSoundCloudThumbnailAsync", info, ct, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey, 0, 0, true)!;
    private static T Field<T>(MediaDetectionService service, string name) => (T)typeof(MediaDetectionService).GetField(name, Private)!.GetValue(service)!;
    private static void Set(MediaDetectionService service, string name, object value) => typeof(MediaDetectionService).GetField(name, Private)!.SetValue(service, value);
    private static object? Invoke(MediaDetectionService service, string name, params object?[] args) => typeof(MediaDetectionService).GetMethod(name, Private)!.Invoke(service, args);

    private static BitmapImage Bitmap(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 60; pixels[i + 2] = 30; pixels[i + 3] = 255; }
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
        public Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string url, CancellationToken ct = default) => Task.FromResult(SoundCloudUrl);
        public Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default) { SoundCloudSearchCalls++; Assert.True(requireStrongMatch); return Task.FromResult(SoundCloudSearch); }
    }

    private sealed class Artwork : IMediaArtworkService
    {
        public BitmapImage? Image;
        public Func<string, Task<BitmapImage?>>? Download;
        public List<string> Downloads { get; } = new();
        public List<(string Source, bool Center)> Crops { get; } = new();
        public Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default) { Downloads.Add(url); return Download?.Invoke(url) ?? Task.FromResult(Image); }
        public BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false) { Crops.Add((mediaSource, forceCenterCrop)); return source; }
        public Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
        public void ConfigureSmartCrop(bool enabled) { }
        public SubjectBounds? GetDominantSubjectBounds(BitmapImage source) => null;
    }

    private sealed class Scanner : IWindowTitleScanner
    {
        public string? CurrentUrl, AnyUrl;
        public int Invalidations;
        public Action? OnInvalidated;
        public List<string> GetAllWindowTitles(bool isThrottled) => new();
        public string? TryGetBrowserUrl() => CurrentUrl;
        public string? TryGetMediaUrlFromAnyBrowser() => AnyUrl;
        public bool IsSpotifyWebPlayerOpen() => false;
        public bool IsPipActive(string? processName = null) => false;
        public bool TryGetPipWindow(out IntPtr hwnd, out string title, string? processName = null) { hwnd = IntPtr.Zero; title = ""; return false; }
        public void InvalidateUrlCaches() { Invalidations++; OnInvalidated?.Invoke(); }
    }
}
