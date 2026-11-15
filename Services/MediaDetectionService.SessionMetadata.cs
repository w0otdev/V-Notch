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
    private async Task<bool> TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return false;
        _isNewTrackThisUpdate = false;

        try
        {
            var (session, spotifyGroundTruth, mediaProperties) = await ResolveActiveSessionAsync(forceRefresh);
            if (session == null)
            {
                DetachActiveDisplaySession();
                return false;
            }

            SwitchActiveDisplaySessionIfNeeded(session);

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true;
            info.SourceAppId = sessionSourceApp;
            info.SessionInstanceKey = BuildSessionInstanceKey(session);

            ApplyMediaSourceFromAppId(info, sessionSourceApp);

            mediaProperties ??= await session.TryGetMediaPropertiesAsync();
            string sessionTitle = mediaProperties?.Title ?? "";
            string sessionArtist = mediaProperties?.Artist ?? "";
            string sessionAlbum = mediaProperties?.AlbumTitle ?? "";
            string lowerAlbum = sessionAlbum.ToLowerInvariant();

            RefineMediaSourceFromMetadata(info, sessionTitle.ToLowerInvariant(), sessionArtist.ToLowerInvariant(), lowerAlbum);

            var pbInfo = session.GetPlaybackInfo();
            info.IsPlaying = pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.PlaybackRate = pbInfo?.PlaybackRate ?? 1.0;
            info.IsAnyMediaPlaying = true;
            info.CurrentTrack = sessionTitle;
            info.CurrentArtist = sessionArtist;
            info.CurrentAlbum = sessionAlbum;

            bool isJunkTitle = TryHandleJunkSessionTitle(info, sessionTitle, sessionArtist);
            if (isJunkTitle)
            {
                if (!MediaMetadataRecoveryPolicy.ShouldContinueAfterJunkTitle(
                    isJunkTitle,
                    IsBrowserSourceApp(sessionSourceApp)))
                {
                    return false;
                }

                info.CurrentTrack = "";
                if (info.Platform != MediaPlatform.YouTube)
                {
                    info.CurrentArtist = "";
                }

                await ApplyTimelinePropertiesAsync(info, session, forceRefresh);
                ApplyFinalPlaybackInfo(info, session);
                if (EvaluateYouTubeJunkTransition(
                    info,
                    sessionSourceApp,
                    windowTitleFactory).ShouldHold)
                {
                    return true;
                }

                return false;
            }

            if (info.Platform == MediaPlatform.Spotify)
            {
                ApplySpotifyGroundTruthCorrection(info, spotifyGroundTruth, ref mediaProperties);
                ApplyCachedSpotifyArtists(info);
            }

            string prevTrackName = _lastTrackName;
            DateTime prevMetadataChangeTime = _lastMetadataChangeTime;
            string prevStableArtist = _stableArtist;

            if (!_timelineSimulator.IsThrottled ||
                string.IsNullOrEmpty(_throttledStaleSmtcTrack) ||
                !string.Equals(info.CurrentTrack, _throttledStaleSmtcTrack, StringComparison.Ordinal))
            {
                _throttledStaleSmtcTrack = "";
                TrackNameChangeBookkeeping(info);
            }

            await ApplyTimelinePropertiesAsync(info, session, forceRefresh);
            ApplyFinalPlaybackInfo(info, session);

            var adTransition = EvaluateLikelyYouTubeAdTransition(
                info,
                sessionSourceApp,
                windowTitleFactory);
            if (adTransition.ShouldHold)
            {
                _lastTrackName = prevTrackName;
                _lastMetadataChangeTime = prevMetadataChangeTime;
                _stableArtist = prevStableArtist;
                return true;
            }

            StabilizeArtist(info);

            var pinned = Volatile.Read(ref _pinnedSession);
            if (pinned != null && pinned.Key == info.SessionInstanceKey && !string.IsNullOrWhiteSpace(info.CurrentTrack))
            {
                string track = BuildTrackIdentity(info.CurrentTrack, "");
                if (!string.Equals(pinned.Track, track, StringComparison.Ordinal))
                {
                    if (_keepPinnedOnTrackChange || string.IsNullOrEmpty(pinned.Track))
                        Interlocked.CompareExchange(ref _pinnedSession, pinned with { Track = track }, pinned);
                    else if (ReferenceEquals(Interlocked.CompareExchange(ref _pinnedSession, null, pinned), pinned))
                        _changes.Enqueue(ChangeTypes.ForceRefresh);
                }
            }

            bool trackChangedForThisPass = InvalidateThumbnailStateIfTrackChanged(info);

            ResolveBrowserMediaSource(
                info,
                sessionSourceApp,
                windowTitleFactory,
                adTransition.WasTransitionActive);

            await ApplySessionThumbnailWithRefreshAsync(info, session, mediaProperties, trackChangedForThisPass, forceRefresh);

            _lastTrackSignature = info.GetSignature();
            return false;
        }
        catch (Exception ex)
        {
            Log("UpdateError", ex.Message);
            return false;
        }
    }

    private BrowserAdTransitionDecision EvaluateLikelyYouTubeAdTransition(
        MediaInfo info,
        string sessionSourceApp,
        Func<List<string>> windowTitleFactory)
    {
        bool isBrowserSession = IsBrowserSourceApp(sessionSourceApp);
        bool hasYouTubeWindow = false;
        bool currentTrackMatchesYouTubeWindow = false;

        if (isBrowserSession)
        {
            var windowTitles = windowTitleFactory();
            hasYouTubeWindow =
                MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(windowTitles)) ==
                MediaPlatform.YouTube;
            currentTrackMatchesYouTubeWindow =
                HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, YouTubeLowerPlatformName);
        }

        DateTime previousTransitionStartUtc = _youtubeAdTransitionStartedUtc;
        var decision = BrowserMediaTransitionPolicy.EvaluateLikelyYouTubeAd(
            isBrowserSession,
            hasStableTrack: !string.IsNullOrWhiteSpace(_lastStableTrackSignature),
            lastSource: _lastSource,
            lastPublishedSessionInstanceKey: _lastPublishedSessionInstanceKey,
            currentSessionInstanceKey: info.SessionInstanceKey,
            currentTrack: info.CurrentTrack,
            currentDuration: info.Duration,
            hasYouTubeWindow,
            currentTrackMatchesYouTubeWindow,
            transitionStartedUtc: previousTransitionStartUtc,
            nowUtc: DateTime.UtcNow);
        _youtubeAdTransitionStartedUtc = decision.TransitionStartedUtc;

        if (decision.ShouldHold &&
            previousTransitionStartUtc == DateTime.MinValue)
        {
            RuntimeLog.Debug("MEDIA-AD-TRANSITION",
                $"Quarantining browser ad candidate track='{info.CurrentTrack}' artist='{info.CurrentArtist}' duration={info.Duration.TotalSeconds:F1}s session='{info.SessionInstanceKey}'");
        }

        return decision;
    }

    private BrowserAdTransitionDecision EvaluateYouTubeJunkTransition(
        MediaInfo info,
        string sessionSourceApp,
        Func<List<string>> windowTitleFactory)
    {
        bool isBrowserSession = IsBrowserSourceApp(sessionSourceApp);
        bool hasYouTubeWindow = false;
        if (isBrowserSession)
        {
            hasYouTubeWindow =
                MediaPlatformExtensions.ParsePlatform(
                    DetectPlatformHint(windowTitleFactory())) ==
                MediaPlatform.YouTube;
        }

        DateTime previousTransitionStartUtc = _youtubeAdTransitionStartedUtc;
        var decision = BrowserMediaTransitionPolicy.EvaluateYouTubeJunkMetadata(
            isBrowserSession,
            hasStableTrack: !string.IsNullOrWhiteSpace(_lastStableTrackSignature),
            lastSource: _lastSource,
            hasYouTubeWindow,
            transitionStartedUtc: previousTransitionStartUtc,
            nowUtc: DateTime.UtcNow);
        _youtubeAdTransitionStartedUtc = decision.TransitionStartedUtc;

        if (decision.ShouldHold &&
            previousTransitionStartUtc == DateTime.MinValue)
        {
            RuntimeLog.Debug("MEDIA-AD-TRANSITION",
                $"Quarantining junk browser metadata for session='{info.SessionInstanceKey}'");
        }

        return decision;
    }

    private void SwitchActiveDisplaySessionIfNeeded(GlobalSystemMediaTransportControlsSession session)
    {
        if (AreSameSession(_activeDisplaySession, session)) return;

        string previousSourceApp =
            _activeDisplaySession?.SourceAppUserModelId ??
            _lastPublishedSourceAppId;
        string nextSourceApp = session.SourceAppUserModelId ?? "";
        bool preserveSameBrowserMediaCache =
            IsBrowserSourceApp(previousSourceApp) &&
            IsBrowserSourceApp(nextSourceApp) &&
            string.Equals(
                previousSourceApp,
                nextSourceApp,
                StringComparison.OrdinalIgnoreCase);

        DetachActiveDisplaySession();

        _activeDisplaySession = session;

        if (!AreSameSession(_activeDisplaySession, _currentSession))
        {
            try
            {
                _activeDisplaySession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _activeDisplaySession.PlaybackInfoChanged += OnPlaybackChanged;
                _activeDisplaySession.TimelinePropertiesChanged += OnTimelineChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-SUB", ex.ToString());
            }
        }

        _lastTrackSignature = "";
        if (!preserveSameBrowserMediaCache)
        {
            _lastThumbTrackIdentity = "";
            CachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            CancelAndClearThumbnailCts();
            _youTubeFetchIdentity = "";
            Interlocked.Increment(ref _youTubeFetchGeneration);
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            _soundCloudFetchIdentity = "";
            _lastSoundCloudArtworkIdentity = "";
            _soundCloudArtworkAttemptCount = 0;
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            Interlocked.Increment(ref _thumbnailFetchGeneration);
        }
    }

    private static void ApplyMediaSourceFromAppId(MediaInfo info, string sessionSourceApp)
        => MediaSourceClassifier.ApplyFromAppId(info, sessionSourceApp);

    private static void RefineMediaSourceFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
        => MediaSourceClassifier.RefineFromMetadata(info, lowerTitle, lowerArtist, lowerAlbum);

    private static bool TryHandleJunkSessionTitle(MediaInfo info, string sessionTitle, string sessionArtist)
        => MediaSourceClassifier.TryHandleJunkTitle(info, sessionTitle, sessionArtist);

    private void StabilizeArtist(MediaInfo info)
    {
        var r = MediaTimingDecisions.EvaluateArtistStabilization(
            info.CurrentArtist, _stableArtist, _lastSourceConfirmedTime, DateTime.UtcNow);
        info.CurrentArtist = r.artist;
        _stableArtist = r.stableArtist;
    }

#pragma warning disable S3776
    private static void ApplySpotifyGroundTruthCorrection(MediaInfo info, string? spotifyGroundTruth, ref GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties)
    {
        bool isStaleSMTC = false;
        if (!string.IsNullOrEmpty(spotifyGroundTruth) &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            !SpotifyTitleContainsTrack(spotifyGroundTruth, info.CurrentTrack))
        {
            ParseSpotifyTitle(spotifyGroundTruth, info);
            isStaleSMTC = true;
        }

        if (!isStaleSMTC && !string.IsNullOrEmpty(spotifyGroundTruth) &&
            !string.IsNullOrEmpty(info.CurrentArtist))
        {
            Span<char> trackBuf = stackalloc char[512];
            Span<char> artistBuf = stackalloc char[512];
            string curTrack = info.CurrentTrack ?? string.Empty;
            if (vn_media_detection_evaluate_spotify_secondary_correction(
                    spotifyGroundTruth.AsSpan(), spotifyGroundTruth.Length,
                    curTrack.AsSpan(), curTrack.Length,
                    info.CurrentArtist.AsSpan(), info.CurrentArtist.Length,
                    trackBuf, trackBuf.Length, out int tLen,
                    artistBuf, artistBuf.Length, out int aLen,
                    out byte corrected) == 0 && corrected != 0)
            {
                info.CurrentTrack = new string(trackBuf[..tLen]);
                info.CurrentArtist = new string(artistBuf[..aLen]);
                isStaleSMTC = true;
            }
        }

        if (isStaleSMTC)
        {
            mediaProperties = null;
        }
    }

    private static string BuildSpotifyArtistCacheKey(string track, string artist, string? album, TimeSpan duration)
        => $"{track.Trim()}|{artist.Trim()}|{(album ?? string.Empty).Trim()}|{(int)Math.Round(duration.TotalSeconds)}";

    private static string BuildSpotifyArtistTrackOnlyKey(string track, string artist)
        => $"{track.Trim()}|{artist.Trim()}||0";

    private bool ApplyCachedSpotifyArtists(MediaInfo info)
    {
        if (info.Platform != MediaPlatform.Spotify ||
            string.IsNullOrWhiteSpace(info.CurrentTrack) ||
            string.IsNullOrWhiteSpace(info.CurrentArtist))
        {
            return false;
        }

        string key = BuildSpotifyArtistCacheKey(info.CurrentTrack, info.CurrentArtist, info.CurrentAlbum, info.Duration);
        string albumZeroKey = BuildSpotifyArtistCacheKey(info.CurrentTrack, info.CurrentArtist, info.CurrentAlbum, TimeSpan.Zero);
        string trackOnlyKey = BuildSpotifyArtistTrackOnlyKey(info.CurrentTrack, info.CurrentArtist);
        if ((_spotifyArtistCache.TryGetValue(key, out string? enriched) ||
             _spotifyArtistCache.TryGetValue(albumZeroKey, out enriched) ||
             _spotifyArtistCache.TryGetValue(trackOnlyKey, out enriched)) && !string.IsNullOrEmpty(enriched))
        {
            info.CurrentArtist = enriched;
            _stableArtist = enriched;
            return true;
        }
        return false;
    }

    private void StoreSpotifyArtistCache(string track, string primaryArtist, string album, TimeSpan duration, string enriched)
    {
        // shortcut: full clear at 512 entries (~85 tracks), upgrade to ring buffer/LRU if cache eviction causes re-lookups
        if (_spotifyArtistCache.Count >= MaxSpotifyArtistCacheEntries)
        {
            _spotifyArtistCache.Clear();
        }

        _spotifyArtistCache[BuildSpotifyArtistCacheKey(track, primaryArtist, album, duration)] = enriched;
        _spotifyArtistCache[BuildSpotifyArtistCacheKey(track, primaryArtist, album, TimeSpan.Zero)] = enriched;
        _spotifyArtistCache[BuildSpotifyArtistTrackOnlyKey(track, primaryArtist)] = enriched;
        _spotifyArtistCache[BuildSpotifyArtistCacheKey(track, enriched, album, duration)] = enriched;
        _spotifyArtistCache[BuildSpotifyArtistCacheKey(track, enriched, album, TimeSpan.Zero)] = enriched;
        _spotifyArtistCache[BuildSpotifyArtistTrackOnlyKey(track, enriched)] = enriched;
    }

    private Task ResolveSpotifyArtistsBeforePublishAsync(MediaInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        info.IsMetadataPending = false;
        // The Web Player publishes its artist credits in the browser media session.
        if (info.Platform != MediaPlatform.Spotify || IsBrowserSourceApp(info.SourceAppId) ||
            string.IsNullOrWhiteSpace(info.CurrentTrack) ||
            string.IsNullOrWhiteSpace(info.CurrentArtist))
        {
            CancelSpotifyArtistLookup();
            return Task.CompletedTask;
        }

        if (ApplyCachedSpotifyArtists(info))
        {
            CancelSpotifyArtistLookup();
            return Task.CompletedTask;
        }
        string track = info.CurrentTrack.Trim();
        string artist = info.CurrentArtist.Trim();
        string album = (info.CurrentAlbum ?? string.Empty).Trim();
        TimeSpan duration = info.Duration;
        StartSpotifyArtistLookup(info, track, artist, album, duration, ct);
        // A synchronous/local result can reveal both text fields immediately.
        // Otherwise publish current controls/timeline with a pending text state;
        // the lookup wakes the normal processor to read a fresh media snapshot.
        info.IsMetadataPending = !ApplyCachedSpotifyArtists(info);
        if (info.IsMetadataPending) info.Thumbnail = null;
        return Task.CompletedTask;
    }

    private bool InvalidateThumbnailStateIfTrackChanged(MediaInfo info)
    {
        // Browser artist metadata can be enriched while artwork is downloading.
        // Native players retain the artist identity for equally named songs.
        bool isBrowserSession = IsBrowserSourceApp(info.SourceAppId) ||
                                info.Platform is MediaPlatform.Browser or MediaPlatform.YouTube or MediaPlatform.SoundCloud;
        string currentTrackOnlyIdentityForThumb = BuildTrackIdentity(info.CurrentTrack, isBrowserSession ? "" : info.CurrentArtist);
        bool trackChangedForThisPass = !string.Equals(currentTrackOnlyIdentityForThumb, _lastThumbTrackIdentity, StringComparison.Ordinal);
        if (trackChangedForThisPass)
        {
            CachedThumbnail = null;
            info.Thumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            CancelAndClearThumbnailCts();
            _youTubeFetchIdentity = "";
            Interlocked.Increment(ref _youTubeFetchGeneration);
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            _soundCloudFetchIdentity = "";
            _lastSoundCloudArtworkIdentity = "";
            _soundCloudArtworkAttemptCount = 0;
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            Interlocked.Increment(ref _thumbnailFetchGeneration);
            RuntimeLog.Debug("MEDIA-THUMB-INVALIDATE",
                $"Track changed: old='{_lastThumbTrackIdentity}' new='{currentTrackOnlyIdentityForThumb}' — cleared all thumbnail state");
        }
        _lastThumbTrackIdentity = currentTrackOnlyIdentityForThumb;
        return trackChangedForThisPass;
    }

    private static void ApplyFinalPlaybackInfo(MediaInfo info, GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            var playbackInfo = session?.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                var status = playbackInfo.PlaybackStatus;
                info.IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                info.PlaybackRate = playbackInfo.PlaybackRate ?? 1.0;
                info.SourceAppId = session?.SourceAppUserModelId ?? "";

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                {
                    info.IsIndeterminate = true;
                }

                var controls = playbackInfo.Controls;
                info.IsSeekEnabled = controls.IsPlaybackPositionEnabled;
            }
            else
            {
                info.IsPlaying = info.IsAnyMediaPlaying;
            }
        }
        catch { info.IsPlaying = info.IsAnyMediaPlaying; }
    }

#pragma warning disable S3776
    private static bool HasSpotifyWebCaptionMatch(List<string> windowTitles, string track, string artist)
    {
        for (int i = 0; i < windowTitles.Count; i++)
        {
            if (MediaSourceClassifier.MatchesSpotifyWebCaption(windowTitles[i], track, artist))
                return true;
        }
        return false;
    }

    private void ResolveBrowserMediaSource(
        MediaInfo info,
        string sessionSourceApp,
        Func<List<string>> windowTitleFactory,
        bool isCompletingAdTransition)
    {
        var pinned = Volatile.Read(ref _pinnedSession);
        if (pinned != null && pinned.Key == info.SessionInstanceKey)
        {
            info.MediaSource = pinned.Source;
            return;
        }

        bool isBrowserSession = IsBrowserSourceApp(sessionSourceApp);
        bool hasBrowserSourceApp = !string.IsNullOrEmpty(sessionSourceApp) && isBrowserSession;
        List<string>? hintedWindowTitles = null;
        if (info.Platform == MediaPlatform.Browser && isBrowserSession)
        {
            hintedWindowTitles = windowTitleFactory();
            if (HasSpotifyWebCaptionMatch(hintedWindowTitles, info.CurrentTrack, info.CurrentArtist) &&
                !HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, YouTubeLowerPlatformName))
            {
                MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.Spotify);
                info.IsSpotifyPlaying = info.IsPlaying;
            }
        }
        if (info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource))
        {
            bool isSameSession = !string.IsNullOrEmpty(info.SessionInstanceKey) &&
                                 string.Equals(info.SessionInstanceKey, _lastPublishedSessionInstanceKey, StringComparison.Ordinal);
            string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool sameTrackAsStable = !string.IsNullOrEmpty(currentTrackIdentity) &&
                                     currentTrackIdentity == _stableSourceTrackIdentity;
            bool sourceFromBrowserOverride = false;
            bool sourceFromTrackCache = false;

            string browserPlatformHint = "";
            bool hasReliableYouTubeWindowMatch = false;
            if (hasBrowserSourceApp)
            {
                hintedWindowTitles ??= windowTitleFactory();
                hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, YouTubeLowerPlatformName);
                browserPlatformHint = hasReliableYouTubeWindowMatch
                    ? YouTubePlatformName
                    : DetectPlatformHint(hintedWindowTitles);
            }

            if (BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
                isBrowserSession: isBrowserSession,
                currentBrowserPlatformHint: browserPlatformHint,
                lastSource: _lastSource,
                stableSource: _stableSource,
                isCompletingAdTransition))
            {
                MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.YouTube);
            }

            if (hasBrowserSourceApp &&
                TryGetSessionSourceOverride(info, out var sessionOverride) &&
                MediaPlatformExtensions.ParsePlatform(sessionOverride) == MediaPlatform.SoundCloud &&
                !hasReliableYouTubeWindowMatch)
            {
                hintedWindowTitles ??= windowTitleFactory();
                if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, SoundCloudPlatformName))
                {
                    MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
                    sourceFromBrowserOverride = true;
                }
            }

            if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
                !string.IsNullOrEmpty(info.CurrentTrack))
            {
                bool hasCachedSource = _sourceCache.TryGet(currentTrackIdentity, out var cachedSource) ||
                                       _sourceCache.TryGet(BuildTrackIdentity(info.CurrentTrack, ""), out cachedSource);

                if (hasCachedSource &&
                    string.Equals(cachedSource, SoundCloudPlatformName, StringComparison.OrdinalIgnoreCase) &&
                    !hasReliableYouTubeWindowMatch)
                {
                    hintedWindowTitles ??= windowTitleFactory();
                    if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, SoundCloudPlatformName))
                    {
                        MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
                        sourceFromTrackCache = true;
                    }
                }
            }

            if (isSameSession && !string.IsNullOrEmpty(_stableSource) && MediaPlatformExtensions.ParsePlatform(_stableSource) != MediaPlatform.Browser && sameTrackAsStable)
            {
                hintedWindowTitles = windowTitleFactory();
                string hintedSource = DetectPlatformHint(hintedWindowTitles);

                bool canKeepStable = string.IsNullOrEmpty(hintedSource) ||
                                     string.Equals(hintedSource, _stableSource, StringComparison.OrdinalIgnoreCase);
                if (canKeepStable)
                {
                    MediaSourceClassifier.SetPlatformRunning(info, MediaPlatformExtensions.ParsePlatform(_stableSource));
                }
            }

            if (info.Platform == MediaPlatform.Browser ||
                string.IsNullOrEmpty(info.MediaSource) ||
                sourceFromBrowserOverride ||
                sourceFromTrackCache)
            {
                var windowTitles = hintedWindowTitles ?? windowTitleFactory();
                string trackTitleLower = info.CurrentTrack.ToLowerInvariant();
                string trackTitleNormalized = NormalizeForLooseMatch(trackTitleLower);
                bool hasTrack = !string.IsNullOrEmpty(trackTitleLower) && trackTitleLower != "browser" && trackTitleLower != "now playing";
                bool reliableYouTubeMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, YouTubeLowerPlatformName);
                string platformHint = reliableYouTubeMatch
                    ? YouTubePlatformName
                    : DetectPlatformHint(windowTitles);
                if ((!hasTrack && string.Equals(platformHint, YouTubePlatformName, StringComparison.OrdinalIgnoreCase)) ||
                    reliableYouTubeMatch)
                {
                    MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.YouTube);
                }

                MediaSourceClassifier.DetectFromWindowTitles(info, windowTitles, trackTitleLower, trackTitleNormalized, hasTrack);
            }
        }

        if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
            hasBrowserSourceApp &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            !IsLikelyYouTube(info) &&
            !IsKnownNonSpotifyTrack(info) &&
            _windowTitleScanner.IsSpotifyWebPlayerOpen())
        {
            info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
            info.IsSpotifyPlaying = true;
            info.IsSpotifyRunning = true;
        }

        if (hasBrowserSourceApp &&
            !string.IsNullOrEmpty(info.MediaSource) &&
            info.Platform != MediaPlatform.Browser)
        {
            if (info.Platform is MediaPlatform.YouTube or MediaPlatform.Spotify ||
                !TryGetSessionSourceOverride(info, out var existingOverride) ||
                MediaPlatformExtensions.ParsePlatform(existingOverride) != MediaPlatform.YouTube)
            {
                SetSessionSourceOverride(info, info.MediaSource);
            }
        }

        if (info.Platform != MediaPlatform.Browser && !string.IsNullOrEmpty(info.MediaSource))
        {
            _stableSource = info.MediaSource;
            _lastSourceConfirmedTime = DateTime.UtcNow;
            _stableSourceTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);

            if (!string.IsNullOrEmpty(info.CurrentTrack))
            {
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool cacheChanged = !_sourceCache.TryGet(_stableSourceTrackIdentity, out var cachedSource) ||
                                    !string.Equals(cachedSource, info.MediaSource, StringComparison.Ordinal) ||
                                    !_sourceCache.TryGet(trackOnlyIdentity, out var cachedTrackOnlySource) ||
                                    !string.Equals(cachedTrackOnlySource, info.MediaSource, StringComparison.Ordinal);
                if (cacheChanged)
                {
                    _sourceCache.SetBoth(_stableSourceTrackIdentity, trackOnlyIdentity, info.MediaSource);
                    if (info.Platform is MediaPlatform.YouTube or MediaPlatform.SoundCloud)
                        _sourceCache.QueueSave();
                }
            }
        }
    }

}
