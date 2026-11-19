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
    private MediaWindowTitle? FindMediaWindowTitle(bool recovery, List<string>? titles)
    {
        if (titles != null)
        {
            foreach (string title in titles)
                if (WindowTitleScanner.ParseMediaWindowTitle(title, recovery) is { } match) return match;
            return null;
        }
        return _windowTitleScanner.FindMediaWindowTitle(recovery,
            _timelineSimulator.IsThrottled || _currentMode == DetectionMode.AwaitingMetadata);
    }

    private void ApplyWindowTitleFallback(MediaInfo info, ref List<string>? windowTitles)
    {
        bool needsFallback = !info.IsAnyMediaPlaying || info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource);

        bool recentlyHadMedia = !info.IsAnyMediaPlaying &&
                                MediaPlatformExtensions.ParsePlatform(_lastSource) is (MediaPlatform.YouTube or MediaPlatform.Browser or MediaPlatform.SoundCloud) &&
                                _emptyMetadataStartTime != DateTime.MinValue &&
                                (DateTime.UtcNow - _emptyMetadataStartTime).TotalSeconds < 6.0;

        if (!needsFallback || !(info.IsAnyMediaPlaying || recentlyHadMedia)) return;
        ApplySpotifyWindowFallback(info);
        ApplyBrowserCaptionFallback(info, windowTitles);
    }

    private void ApplySpotifyWindowFallback(MediaInfo info)
    {
        if (info.Platform != MediaPlatform.Spotify)
        {
            var wTitle = _spotifyWindowTitle();
            if (!string.IsNullOrEmpty(wTitle))
            {
                info.IsSpotifyRunning = true;
                info.IsSpotifyPlaying = true;
                info.IsAnyMediaPlaying = true;
                info.IsPlaying = true;
                info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
                ParseSpotifyTitle(wTitle, info);
                ApplyCachedSpotifyArtists(info);
                if (!string.IsNullOrEmpty(info.CurrentArtist)) _stableArtist = info.CurrentArtist;
            }
        }
    }

    private void ApplyBrowserCaptionFallback(MediaInfo info, List<string>? windowTitles)
    {
        if (string.IsNullOrEmpty(info.CurrentTrack) &&
            MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(info.IsSpotifyPlaying, info.MediaSource) &&
            FindMediaWindowTitle(false, windowTitles) is { } match)
        {
            info.IsAnyMediaPlaying = true;
            info.CurrentTrack = match.Track;
            info.CurrentArtist = match.Artist;
            if (match.Platform == MediaPlatform.YouTube)
            {
                info.MediaSource = YouTubePlatformName;
                info.YouTubeTitle = match.ExtractedTitle;
                if (!string.IsNullOrEmpty(_stableArtist) && (DateTime.UtcNow - _lastSourceConfirmedTime).TotalSeconds < 15.0)
                    info.CurrentArtist = _stableArtist;
            }
            else
            {
                MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
                SetSessionSourceOverride(info, SoundCloudPlatformName);
            }
        }
    }

    internal void ApplyVideoTimelineRecovery(MediaInfo info, ref List<string>? windowTitles)
    {
        if (info.IsTimelineIndeterminate)
        {
            _timelineSimulator.Reset();
            _throttledStaleSmtcTrack = "";
            info.IsThrottled = false;
            return;
        }

        bool isVideoSource = info.Platform == MediaPlatform.YouTube ||
                             (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
        if (!isVideoSource || !info.IsPlaying)
        {
            _timelineSimulator.Reset();
            _throttledStaleSmtcTrack = "";
            return;
        }

        if (!ObserveStalledTimeline(info, out bool positionStuck, out bool atEndStuck)) return;

        if (!string.IsNullOrEmpty(info.CurrentTrack) && IsLikelyYouTube(info))
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
            _timelineSimulator.ApplySimulatedTimeline(info, atEndStuck);
            return;
        }

        if (FindMediaWindowTitle(true, windowTitles) is { } match && TryRecoverWindowTimeline(info, match, positionStuck))
            return;

        if (_timelineSimulator.IsThrottled)
        {
            _timelineSimulator.TryExitThrottleIfStalled(TimeSpan.FromSeconds(3.5));
            if (!_timelineSimulator.IsThrottled) _throttledStaleSmtcTrack = "";
        }
    }

    private bool ObserveStalledTimeline(MediaInfo info, out bool positionStuck, out bool atEndStuck)
    {
        _timelineSimulator.UpdateObservedPosition(info.Position);

        double progress = info.Duration.TotalSeconds > 0 ? info.Position.TotalSeconds / info.Duration.TotalSeconds : 0;
        positionStuck = _timelineSimulator.IsPositionStuck(TimeSpan.FromSeconds(1.5));
        atEndStuck = MediaTimelineSimulator.IsAtEndStuck(progress, _lastMetadataChangeTime, TimeSpan.FromSeconds(1.2));

        if (progress >= 1.0 && info.Duration.TotalSeconds > 0 && _timelineSimulator.IsThrottled)
        {
            _timelineSimulator.Reset();
            _throttledStaleSmtcTrack = "";
            _changes.Enqueue(ChangeTypes.ForceRefresh);
        }

        if (!positionStuck && !atEndStuck)
        {
            if (_timelineSimulator.IsThrottled)
            {
                _timelineSimulator.TryExitThrottleIfPositionResumed(TimeSpan.FromMilliseconds(500));
                if (!_timelineSimulator.IsThrottled) _throttledStaleSmtcTrack = "";
            }
            return false;
        }
        return true;
    }

    private bool TryRecoverWindowTimeline(MediaInfo info, MediaWindowTitle match, bool positionStuck)
    {
        string extractedTitle = match.ExtractedTitle;
        string trackName = match.Track;
        string artistName = match.Artist;

        bool isSameSplitTitle = !string.IsNullOrEmpty(_lastTrackName) &&
            (string.Equals(trackName, _lastTrackName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extractedTitle, _lastTrackName, StringComparison.OrdinalIgnoreCase));
        bool isNewTrack = !isSameSplitTitle &&
            (string.IsNullOrEmpty(_lastTrackName) ||
             PublishedTrackMatcher.IsNewTrackForThumbnail(BuildTrackIdentity(_lastTrackName, ""), trackName) ||
             !string.Equals(trackName, _lastTrackName, StringComparison.OrdinalIgnoreCase));

        if (isNewTrack)
        {
            _throttledStaleSmtcTrack = info.CurrentTrack;
            ApplyThrottledYouTubeTrackMetadata(info, trackName, artistName);
            _timelineSimulator.ResetRecoveredData();
            info.Duration = TimeSpan.Zero;
            info.Position = TimeSpan.FromSeconds(1.5);
            info.LastUpdated = DateTimeOffset.Now;
            ApplyTimelineAvailability(info, true, true, false, TimeSpan.Zero, DateTime.UtcNow);
            return true;
        }

        if (positionStuck || _timelineSimulator.IsThrottled)
        {
            ApplyThrottledYouTubeTrackMetadata(info, trackName, artistName);
            info.Duration = _timelineSimulator.RecoveredDuration.TotalSeconds > 0
                ? _timelineSimulator.RecoveredDuration
                : TimeSpan.Zero;
            if (_timelineSimulator.RecoveredThumbnail != null)
                info.Thumbnail = _timelineSimulator.RecoveredThumbnail;

            info.Position = DateTime.UtcNow - _lastMetadataChangeTime;
            info.LastUpdated = DateTimeOffset.Now;
            ApplyTimelineAvailability(info, true, false, false, info.Duration, DateTime.UtcNow);
            return true;
        }
        return false;
    }

    private void ApplyThrottledYouTubeTrackMetadata(MediaInfo info, string trackName, string artistName)
    {
        info.CurrentTrack = trackName;
        info.CurrentArtist = artistName;
        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
        info.IsYouTubeRunning = true;
        info.IsThrottled = true;
        _timelineSimulator.EnterThrottledMode();
    }

    private void ApplyPrePublishThumbnailFallbackAndCrop(MediaInfo info, bool isNewTrackForThumbnail)
    {
        // Metadata may briefly disappear between tracks while the source session remains open.
        if (!info.IsAnyMediaPlaying && string.IsNullOrEmpty(info.CurrentTrack) &&
            string.IsNullOrEmpty(info.SourceAppId))
        {
            info.Thumbnail = null;
            CachedThumbnail = null;
            _lastInitialCroppedThumbnail = null;
            _lastSessionArtworkFingerprint = null;
            _sessionArtworkGeneration = -1;
            _cachedThumbnailSource = "";
            _lastThumbTrackIdentity = "";
            _timelineSimulator.RecoveredThumbnail = null;
            CancelAndClearThumbnailCts();
            return;
        }
        if (!isNewTrackForThumbnail && info.Thumbnail == null && CachedThumbnail != null)
        {
            info.Thumbnail = CachedThumbnail;
            if (CachedThumbnail.PixelWidth >= 200)
            {
                _timelineSimulator.RecoveredThumbnail = CachedThumbnail;
            }
        }

        bool needsFetch = isNewTrackForThumbnail || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
        if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !isNewTrackForThumbnail)
        {
            needsFetch = false;
        }

        if (needsFetch && !isNewTrackForThumbnail && CachedThumbnail != null && CachedThumbnail.PixelWidth >= 200)
        {
            needsFetch = false;
            info.Thumbnail = CachedThumbnail;
            _timelineSimulator.RecoveredThumbnail = CachedThumbnail;
        }

        if (needsFetch && info.Thumbnail != null &&
            !IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail) && (info.Thumbnail.PixelWidth >= 200 || IsLikelySoundCloudArtworkCandidate(info.Thumbnail)))
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
            bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                  info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

            if (isNearSquare || isTopicChannel)
            {
                bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                                    TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                                    MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;
                bool isPotentialYouTube = info.Platform == MediaPlatform.YouTube ||
                                          (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
                bool isPotentialSoundCloud = info.Platform == MediaPlatform.SoundCloud || hasSoundCloudSessionOverride;
                RuntimeLog.Debug(MediaThumbCropLogTag,
                    $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
                string thumbnailSource = isPotentialSoundCloud && !isPotentialYouTube
                    ? SoundCloudPlatformName
                    : info.MediaSource ?? YouTubePlatformName;
                var cropped = CropToSquare(info.Thumbnail, thumbnailSource, forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
                info.Thumbnail = cropped;
                CachedThumbnail = cropped;
                _cachedThumbnailSource = isPotentialYouTube ? YouTubePlatformName : thumbnailSource;
                _timelineSimulator.RecoveredThumbnail = cropped;
                _lastInitialCroppedThumbnail = cropped;
            }
        }
    }

}
