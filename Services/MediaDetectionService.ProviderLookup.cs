using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaDetectionService : IMediaDetectionService, IAsyncDisposable
{
    public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, CancellationToken ct = default)
        => _metadataLookup.TryGetYouTubeVideoIdWithInfoAsync(title, ct);

    private bool TryGetCachedMismatchVideoId(string videoId)
        => _videoIds.IsMismatch(videoId);

    private void CacheMismatchVideoId(string videoId)
        => _videoIds.MarkMismatch(videoId);

    private void ClearMismatchCache()
        => _videoIds.ClearMismatches();

    private Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkUrlAsync(title, artist, requireStrongMatch, ct);

    private Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, string expectedTitle, string expectedArtist, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkFromUrlAsync(soundCloudUrl, expectedTitle, expectedArtist, ct);

    private Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
        => _artworkService.DownloadImageAsync(url, ct);

    private BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false)
        => _artworkService.CropToSquare(source, mediaSource, forceCenterCrop);

    private Task<BitmapImage?> ConvertToWpfBitmapAsync(global::Windows.Storage.Streams.IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
        => _artworkService.ConvertToWpfBitmapAsync(stream, ct);

    private List<string> GetAllWindowTitles()
        // Fast metadata probes reuse the longer desktop-title cache while waiting.
        => _windowTitleScanner.GetAllWindowTitles(
            _timelineSimulator.IsThrottled || _currentMode == DetectionMode.AwaitingMetadata);

    private string? TryExtractVideoIdFromBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetBrowserUrl();
            return ExtractYouTubeVideoIdFromUrl(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Browser URL extraction failed: {ex.Message}");
            return null;
        }
    }

    private string? TryExtractVideoIdFromAnyBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            return ExtractYouTubeVideoIdFromUrl(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Background browser URL extraction failed: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractYouTubeVideoIdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        Span<char> buf = stackalloc char[16];
        return vn_media_detection_extract_youtube_video_id(url.AsSpan(), url.Length, buf, buf.Length, out int len) == 0 && len == 11
            ? new string(buf[..len])
            : null;
    }

    private string? TryExtractSoundCloudUrlFromAnyBrowser()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(url)) return null;

            string? trackUrl = SoundCloudMatching.ExtractTrackUrl(url);
            if (!string.IsNullOrEmpty(trackUrl))
            {
                System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted SoundCloud URL from browser: {trackUrl}");
                return trackUrl;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] SoundCloud browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    private readonly VideoIdCache _videoIds = new();

    private void CacheVideoIdForTrack(string? trackIdentity, string videoId)
        => _videoIds.Set(trackIdentity, videoId);

    private string? GetCachedVideoIdForTrack(string? track)
        => _videoIds.Get(track);

    private void ForgetVideoIdCacheExceptForTrack(string? currentTrackIdentity)
        => _videoIds.ForgetExcept(currentTrackIdentity);

    private void EvictVideoIdCacheEntry(string? track, string staleVideoId)
        => _videoIds.Evict(track, staleVideoId);

    private bool IsLikelyYouTube(MediaInfo info)
    {
        if (info.Platform == MediaPlatform.YouTube) return true;
        if (info.Platform == MediaPlatform.SoundCloud) return false;

        if (info.IsPictureInPicture && (info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) && !string.IsNullOrEmpty(info.CurrentTrack) &&
            MediaPlatformExtensions.ParsePlatform(_stableSource) == MediaPlatform.YouTube)
        {
            return true;
        }

        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.SourceAppId) &&
            TryGetSessionSourceOverride(info, out var sOver) &&
            MediaPlatformExtensions.ParsePlatform(sOver) == MediaPlatform.YouTube)
        {
            string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
            if (string.IsNullOrEmpty(info.CurrentTrack) ||
                _sourceCache.HasSource(trackIdentity, trackOnlyIdentity, YouTubePlatformName))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist.Equals(YouTubePlatformName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            try
            {
                if (_windowTitleScanner.HasYouTubeWindowMatch(info.CurrentTrack,
                        _timelineSimulator.IsThrottled || _currentMode == DetectionMode.AwaitingMetadata))
                    return true;
            }
            catch
            {
                // Window title scan best-effort
            }
        }

        return false;
    }

    private bool IsKnownNonSpotifyTrack(MediaInfo info)
    {
        if (string.IsNullOrEmpty(info.CurrentTrack)) return false;

        string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");

        if ((_sourceCache.TryGet(trackIdentity, out var cachedSource) ||
             _sourceCache.TryGet(trackOnlyIdentity, out cachedSource)) &&
            !string.IsNullOrEmpty(cachedSource) &&
            !string.Equals(cachedSource, SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(cachedSource, "Browser", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetSessionSourceOverride(info, out var sessionOverride) &&
            !string.IsNullOrEmpty(sessionOverride) &&
            MediaPlatformExtensions.ParsePlatform(sessionOverride) != MediaPlatform.Spotify)
        {
            return true;
        }

        return false;
    }

    private bool ShouldPreserveSoundCloudSourceDuringTrackSwitch(MediaInfo info)
    {
        bool hasOverride = TryGetSessionSourceOverride(info, out var sessionOverride);
        return MediaTimingDecisions.ShouldPreserveSoundCloud(new SoundCloudPreserveParams(
            info.MediaSource, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey,
            _lastSource, _lastPublishedSessionInstanceKey, _lastMetadataChangeTime, DateTime.UtcNow,
            hasOverride, sessionOverride));
    }

    private static void ParseSpotifyTitle(string title, MediaInfo info)
    {
        var (artist, track) = PlatformDetector.ParseSpotifyTitle(title);
        info.CurrentArtist = artist;
        info.CurrentTrack = track;
    }

    private static bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
        => MediaHeuristics.SpotifyTitleContainsTrack(spotifyWindowTitle, smtcTrack);

}
