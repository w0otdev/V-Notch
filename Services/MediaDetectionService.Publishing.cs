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
    private async Task PublishMediaSnapshotAsync(MediaInfo info, bool forceRefresh, CancellationToken ct)
    {
        // Both native session snapshots and recovered window titles take this
        // path. Credits may remain pending, but playback can publish immediately.
        await ResolveSpotifyArtistsBeforePublishAsync(info, ct);
        ct.ThrowIfCancellationRequested();
        info.IsThrottled = _timelineSimulator.IsThrottled;
        UpdateDetectionMode(info);
        var currentSignature = info.GetSignature();
        if (UpdateEmptyMetadataHold(info, currentSignature)) return;
        bool isNewTrackForThumbnail = ComputeIsNewTrackForThumbnail(info);
        if (!await TryPublishMediaChangeAsync(info, currentSignature, isNewTrackForThumbnail, forceRefresh)) return;
        if (!info.IsMetadataPending)
            StartThumbnailFetchIfNeeded(info.Clone(), isNewTrackForThumbnail);
    }

    private void TrackNameChangeBookkeeping(MediaInfo info, bool resetThrottle = true)
    {
        if (info.CurrentTrack == _lastTrackName) return;

        if (!string.IsNullOrEmpty(_lastTrackName) && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            _isNewTrackThisUpdate = true;
        }
        _lastTrackName = info.CurrentTrack;
        _lastMetadataChangeTime = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(info.CurrentTrack))
        {
            _stableArtist = info.Platform == MediaPlatform.Spotify && !string.IsNullOrEmpty(info.CurrentArtist)
                ? info.CurrentArtist
                : "";
        }

        if (resetThrottle && _timelineSimulator.IsThrottled)
        {
            _timelineSimulator.Reset();
            _throttledStaleSmtcTrack = "";
        }
    }

    private void PreserveSoundCloudSourceIfNeeded(MediaInfo info, ref List<string>? windowTitles)
    {
        if (!ShouldPreserveSoundCloudSourceDuringTrackSwitch(info)) return;

        windowTitles ??= GetAllWindowTitles();
        bool hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, YouTubeLowerPlatformName);
        if (hasReliableYouTubeWindowMatch)
            return;

        bool hasYouTubeWindowHint = MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(windowTitles)) == MediaPlatform.YouTube;
        bool hasSoundCloudWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, SoundCloudPlatformName) ||
                                        windowTitles.Any(t => t.Contains(SoundCloudPlatformName, StringComparison.OrdinalIgnoreCase));
        if (hasSoundCloudWindowMatch || !hasYouTubeWindowHint)
        {
            MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
            SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());
        }
    }

    private bool UpdateEmptyMetadataHold(MediaInfo info, string currentSignature)
    {
        var r = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            info.CurrentTrack, info.IsAnyMediaPlaying, currentSignature, _lastSource,
            _emptyMetadataStartTime, _lastStableTrackSignature, DateTime.UtcNow);
        _emptyMetadataStartTime = r.emptyStart;
        _lastStableTrackSignature = r.stableSignature;
        return r.hold;
    }

    private bool UpdateTransientSessionGapHold(MediaInfo info)
    {
        DateTime previousGapStartUtc = _transientSessionGapStartedUtc;
        var decision = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession: info.IsAnyMediaPlaying,
            hasStableTrack: !string.IsNullOrWhiteSpace(_lastStableTrackSignature),
            lastSource: _lastSource,
            gapStartedUtc: previousGapStartUtc,
            nowUtc: DateTime.UtcNow);
        _transientSessionGapStartedUtc = decision.GapStartedUtc;

        if (!decision.ShouldHold)
            return false;

        if (previousGapStartUtc == DateTime.MinValue)
        {
            RuntimeLog.Debug("MEDIA-SESSION-GAP",
                $"Holding transient session gap for source='{_lastSource}' lastTrack='{_lastTrackName}'");
        }

        UpdateDetectionMode(info, isSessionGapRecovery: true);
        return true;
    }

    private bool ComputeIsNewTrackForThumbnail(MediaInfo info)
        => PublishedTrackMatcher.IsNewTrackForThumbnail(_lastPublishedTrackOnlyIdentity, info.CurrentTrack);

    private async Task<bool> TryPublishMediaChangeAsync(MediaInfo info, string currentSignature, bool isNewTrackForThumbnail, bool forceRefresh)
    {
        if (!AcceptsMediaUpdate(info)) return false;
        if (info.IsMetadataPending) info.Thumbnail = null;
        else ApplyPrePublishThumbnailFallbackAndCrop(info, isNewTrackForThumbnail);
        bool metadataChanged = currentSignature != _lastPublishedSignature;
        bool thumbnailChanged = !ReferenceEquals(info.Thumbnail, _lastPublishedInfo?.Thumbnail);
        bool playbackChanged = info.IsPlaying != _lastIsPlaying;
        bool timelineChanged = _lastPublishedInfo != null &&
            (info.IsTimelineIndeterminate != _lastPublishedInfo.IsTimelineIndeterminate ||
             info.IsTimelinePending != _lastPublishedInfo.IsTimelinePending ||
             (info.HasTimeline && info.Duration != _lastPublishedInfo.Duration));
        bool sourceChanged = info.MediaSource != _lastSource;
        bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;
        bool pipChanged = info.IsPictureInPicture != _lastIsPictureInPicture;
        bool throttleChanged = info.IsThrottled != _lastIsThrottled;

        var publishParams = new VnMediaPublishParams
        {
            ForceRefresh = forceRefresh ? (byte)1 : (byte)0,
            MetadataChanged = metadataChanged ? (byte)1 : (byte)0,
            ThumbnailChanged = thumbnailChanged ? (byte)1 : (byte)0,
            PlaybackChanged = playbackChanged ? (byte)1 : (byte)0,
            TimelineChanged = timelineChanged ? (byte)1 : (byte)0,
            SourceChanged = sourceChanged ? (byte)1 : (byte)0,
            SeekCapabilityChanged = seekCapabilityChanged ? (byte)1 : (byte)0,
            PipChanged = pipChanged ? (byte)1 : (byte)0,
            ThrottleChanged = throttleChanged ? (byte)1 : (byte)0,
            IsThrottled = info.IsThrottled ? (byte)1 : (byte)0,
            IsPlaying = info.IsPlaying ? (byte)1 : (byte)0,
            PositionTicks = info.Position.Ticks,
            LastPositionTicks = _lastPosition.Ticks,
            DurationTicks = info.Duration.Ticks,
            NowUtcTicks = DateTime.UtcNow.Ticks,
            StartupSyncUntilUtcTicks = _startupProgressSyncUntilUtc.Ticks
        };

        if (vn_media_detection_evaluate_should_publish(in publishParams, out byte shouldPublish) == 0 && shouldPublish == 0)
        {
            return true;
        }

        if (MediaMetadataRecoveryPolicy.ShouldSuppressActiveEmptyPublish(
            info.IsAnyMediaPlaying,
            info.CurrentTrack))
        {
            return false;
        }

        if (ShouldDebounceNewTrack(info, forceRefresh))
        {
            return false;
        }

        SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail);

        CommitPublishedState(info, currentSignature);

        await FireMediaChangedAsync(info);

        if (!forceRefresh && metadataChanged && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            _changes.Enqueue(ChangeTypes.ForceRefresh);
        }

        return true;
    }

    private bool ShouldDebounceNewTrack(MediaInfo info, bool forceRefresh)
    {
        var r = MediaTimingDecisions.EvaluateNewTrackDebounce(new NewTrackDebounceParams(
            info.CurrentTrack, info.CurrentArtist, info.IsPlaying, forceRefresh,
            _lastPublishedTrackIdentity, _pendingNewTrackKey, _pendingNewTrackSince, DateTime.UtcNow));
        _pendingNewTrackKey = r.pendingKey;
        _pendingNewTrackSince = r.pendingSince;
        return r.debounce;
    }

    private void CommitPublishedState(MediaInfo info, string currentSignature)
    {
        _lastPublishedInfo = info.Clone();
        _lastPublishedSignature = currentSignature;
        _lastTrackSignature = currentSignature;
        _lastIsPlaying = info.IsPlaying;
        _lastIsPictureInPicture = info.IsPictureInPicture;
        _lastSource = info.MediaSource;
        _lastPosition = info.Position;
        _lastSeekEnabled = info.IsSeekEnabled;
        _lastIsThrottled = info.IsThrottled;
        _lastPublishedTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
        _lastPublishedSourceAppId = info.SourceAppId ?? "";
        _lastPublishedSessionInstanceKey = info.SessionInstanceKey ?? "";
    }

    internal void SuppressIntermediateYouTubeThumbnail(MediaInfo info, bool isNewTrackForThumbnail)
    {
        bool willFetchYouTubeThumbnail = (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info)))
            && !string.IsNullOrEmpty(info.CurrentTrack)
            && !isNewTrackForThumbnail
            && (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120)
            && (CachedThumbnail == null || CachedThumbnail.PixelWidth < 200);

        if (willFetchYouTubeThumbnail)
        {
            info.Thumbnail = CachedThumbnail;
        }

        if (isNewTrackForThumbnail &&
            (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info))) &&
            info.Thumbnail != null)
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            if (thumbAspect > 1.3)
            {
                RuntimeLog.Debug("MEDIA-THUMB-STALE",
                    $"Suppressing stale wide SMTC thumb on new track publish: track='{info.CurrentTrack}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2}");
                info.Thumbnail = null;
            }
        }
    }

    private async Task FireMediaChangedAsync(MediaInfo info)
    {
        if (!AcceptsMediaUpdate(info) || _disposed || _state is ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed || (_bgCts?.IsCancellationRequested ?? (_lifecycleGeneration > 0)))
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
        {
            if (!string.IsNullOrEmpty(info.CurrentTrack))
            {
                string status = info.IsPlaying ? "Playing" : "Paused";
                string artist = !string.IsNullOrEmpty(info.CurrentArtist) ? $" by {info.CurrentArtist}" : "";
                string source = !string.IsNullOrEmpty(info.MediaSource) ? $" [{info.MediaSource}]" : "";
                RuntimeLog.Info("MEDIA", $"{status}: '{info.CurrentTrack}'{artist}{source}");
            }
            else if (!info.IsAnyMediaPlaying)
            {
                RuntimeLog.Info("MEDIA", "No media playback active (Idle).");
            }

            RuntimeLog.Debug("MEDIA-EVENT",
                $"Firing MediaChanged: Source={info.MediaSource}, App={info.SourceAppId}, Track='{info.CurrentTrack}', Artist='{info.CurrentArtist}', Pos={info.Position.TotalSeconds:F3}s, Dur={info.Duration.TotalSeconds:F3}s, IsPlaying={info.IsPlaying}, Rate={info.PlaybackRate:F3}, LastUpdated={info.LastUpdated:O}");

            if (dispatcher.CheckAccess())
            {
                if (AcceptsMediaUpdate(info)) MediaChanged?.Invoke(this, info.Clone());
                return;
            }

            try
            {
                var ct = _bgCts?.Token ?? CancellationToken.None;
                await dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && AcceptsMediaUpdate(info))
                    {
                        MediaChanged?.Invoke(this, info.Clone());
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal, ct);
            }
            catch (OperationCanceledException)
            {
                // Expected when background task or application is shutting down.
            }
        }
        else
        {
            RuntimeLog.Warn("MEDIA-EVENT", "No dispatcher, cannot fire MediaChanged event");
        }
    }

}
