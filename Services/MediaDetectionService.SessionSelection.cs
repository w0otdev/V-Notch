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
    private bool TryResolvePinnedSession(
        GlobalSystemMediaTransportControlsSessionManager sessionManager,
        PinnedSession pinned,
        out GlobalSystemMediaTransportControlsSession? resolvedSession)
    {
        resolvedSession = null;
        var sessions = sessionManager.GetSessions();
        GlobalSystemMediaTransportControlsSession? match = null;
        for (int i = 0; i < sessions.Count; i++)
        {
            var candidate = sessions[i];
            if (BuildSessionInstanceKey(candidate) == pinned.Key)
            {
                match = candidate;
                break;
            }
        }

        if (match != null)
        {
            RememberCompetingSessions(sessions, pinned);
            _pinReconnect.Reset();
            resolvedSession = match;
            return true;
        }

        if (_keepPinnedOnTrackChange && TryReconnectPinnedSession(sessions, pinned) is { } rebound)
        {
            resolvedSession = rebound;
            return true;
        }

        return ObserveMissingPin(pinned);
    }

    private void RememberCompetingSessions(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, PinnedSession pinned)
    {
        // Remember competing sessions so a later rebuild cannot bind the
        // pin to a different tab that was already playing alongside it.
        HashSet<string>? others = null;
        for (int i = 0; i < sessions.Count; i++)
        {
            string key = BuildSessionInstanceKey(sessions[i]);
            if (key == pinned.Key || pinned.OtherSessionKeys.Contains(key))
                continue;
            others ??= new HashSet<string>(pinned.OtherSessionKeys, StringComparer.Ordinal);
            others.Add(key);
        }
        if (others != null)
            Interlocked.CompareExchange(ref _pinnedSession, pinned with { OtherSessionKeys = others }, pinned);
    }

    private GlobalSystemMediaTransportControlsSession? TryReconnectPinnedSession(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, PinnedSession pinned)
    {
        // Some players replace their SMTC session on every track. Retain
        // the source preference and reconnect only to one unambiguous new
        // session from that application, never to a known competing tab.
        GlobalSystemMediaTransportControlsSession? replacement = null;
        int replacementCount = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            var candidate = sessions[i];
            if (!string.Equals(candidate.SourceAppUserModelId, pinned.SourceAppId, StringComparison.OrdinalIgnoreCase) ||
                pinned.OtherSessionKeys.Contains(BuildSessionInstanceKey(candidate)))
            {
                continue;
            }
            replacement = candidate;
            if (++replacementCount > 1)
                break;
        }

        if (replacementCount == 1 && replacement != null)
        {
            var rebound = pinned with
            {
                Key = BuildSessionInstanceKey(replacement),
                PreviousKey = pinned.Key,
                Session = replacement
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pinnedSession, rebound, pinned), pinned))
            {
                _pinReconnect.Reset();
                RuntimeLog.Info("MEDIA-PIN", "Reconnected pinned source after its media session was replaced.");
                return replacement;
            }
        }
        return null;
    }

    private bool ObserveMissingPin(PinnedSession pinned)
    {
        // Session lists can briefly be empty during track changes. Keep the
        // pinned snapshot until absence is confirmed, never select a rival.
        lock (_lifecycleLock)
        {
            if (_bgCts?.IsCancellationRequested != false || _disposed ||
                !ReferenceEquals(Volatile.Read(ref _pinnedSession), pinned)) return true;
            _pinReconnect.Start();
        }
        if (!_pinReconnect.HasExpired) return true;
        if (ReferenceEquals(Interlocked.CompareExchange(ref _pinnedSession, null, pinned), pinned))
        {
            _pinReconnect.Reset();
            UnsubscribeFromSession();
            _pinClosedThisUpdate = true;
            RuntimeLog.Info("MEDIA-PIN", "Pinned session closed; restored automatic selection.");
            return true;
        }
        return false;
    }

    private async Task<(GlobalSystemMediaTransportControlsSession? Session, string? SpotifyGroundTruth, GlobalSystemMediaTransportControlsSessionMediaProperties? Props)> TryReuseActiveDisplaySessionAsync(
        GlobalSystemMediaTransportControlsSession? osCurrentSession)
    {
        var active = _activeDisplaySession;
        if (active == null) return (null, null, null);

        string? spotifyGroundTruth = null;
        try
        {
            string sourceApp = active.SourceAppUserModelId ?? "";
            if (IsIgnoredSourceApp(sourceApp))
            {
                DetachActiveDisplaySession();
                return (null, null, null);
            }

            // Browser sessions must be rescored: another tab can start playing
            // while the previously selected tab still reports Playing.
            if (IsBrowserSourceApp(sourceApp) ||
                (osCurrentSession != null && !AreSameSession(osCurrentSession, active)))
            {
                return (null, null, null);
            }

            var playback = active.GetPlaybackInfo();
            if (playback == null || playback.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return (null, null, null);

            var props = await active.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrEmpty(props.Title))
                return (null, null, null);

            if (sourceApp.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase))
            {
                spotifyGroundTruth = _spotifyWindowTitle();
                if (string.IsNullOrEmpty(spotifyGroundTruth) || !SpotifyTitleContainsTrack(spotifyGroundTruth, props.Title))
                    return (null, spotifyGroundTruth, null);
            }

            return (active, spotifyGroundTruth, props);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-DETECT-ACTIVE", ex.ToString());
            return (null, spotifyGroundTruth, null);
        }
    }

    private static bool ContainsSpotifySession(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].SourceAppUserModelId?.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    private static bool IsAnyDedicatedMusicSessionPlaying(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            try
            {
                string otherId = sessions[i].SourceAppUserModelId ?? "";
                if ((otherId.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) ||
                     otherId.Contains("Music", StringComparison.OrdinalIgnoreCase)) &&
                    IsSessionPlayingStatus(sessions[i].GetPlaybackInfo().PlaybackStatus))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore individual session inspection error
            }
        }
        return false;
    }

    private bool ShouldHoldCurrentDisplaySession(
        GlobalSystemMediaTransportControlsSession bestSession,
        GlobalSystemMediaTransportControlsSession activeDisplay,
        GlobalSystemMediaTransportControlsSession? osCurrentSession)
    {
        string bestSessionKey = BuildSessionInstanceKey(bestSession);
        if (bestSessionKey != _pendingSessionInstanceKey)
        {
            _pendingSessionInstanceKey = bestSessionKey;
            _pendingSessionStartTime = DateTime.UtcNow;
        }

        string currentId = activeDisplay.SourceAppUserModelId ?? "";
        bool currentIsPremium = currentId.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) ||
                                currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

        bool isOsCurrent = AreSameSession(bestSession, osCurrentSession);
        bool isRecentLatestPlayback = !string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                      bestSessionKey == _latestPlayingSessionKey &&
                                      (DateTime.UtcNow - _latestPlayingSessionStartUtc).TotalSeconds < 8;
        bool currentStillPlaying = false;
        try
        {
            currentStillPlaying = IsSessionPlayingStatus(activeDisplay.GetPlaybackInfo().PlaybackStatus);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-DETECT-CURRENT-STATUS", ex.ToString());
        }

        bool hasFreshTimeline = false;
        try
        {
            var bestTimeline = bestSession.GetTimelineProperties();
            if (bestTimeline != null)
            {
                double timelineAge = (DateTimeOffset.UtcNow - bestTimeline.LastUpdatedTime).TotalSeconds;
                hasFreshTimeline = timelineAge >= 0 && timelineAge < 2.0;

                if (hasFreshTimeline)
                {
                    var currentTimeline = activeDisplay.GetTimelineProperties();
                    if (currentTimeline != null)
                    {
                        double currentTimelineAge = (DateTimeOffset.UtcNow - currentTimeline.LastUpdatedTime).TotalSeconds;
                        if (currentTimelineAge - timelineAge > 3.0)
                        {
                            RuntimeLog.Debug(MediaSessionLogTag, $"Switching session: fresh timeline New={timelineAge:F1}s vs Current={currentTimelineAge:F1}s");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-DETECT-FRESH-TIMELINE", ex.ToString());
        }

        return SessionSwitchArbiter.ShouldHoldCurrentSession(new SessionSwitchInputs
        {
            CurrentIsPremium = currentIsPremium,
            BestIsOsCurrent = isOsCurrent,
            BestIsRecentLatestPlayback = isRecentLatestPlayback,
            CurrentStillPlaying = currentStillPlaying,
            BestHasFreshTimeline = hasFreshTimeline,
            PendingElapsedSeconds = (DateTime.UtcNow - _pendingSessionStartTime).TotalSeconds,
        });
    }

    internal async Task<(GlobalSystemMediaTransportControlsSession? session, string? spotifyGroundTruth, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties)> ResolveActiveSessionAsync(bool forceRefresh)
    {
        // Stop clears the shared reference while this scan can be awaiting
        // metadata. Retain the manager for the lifetime of this scan.
        var sessionManager = _sessionManager;
        if (sessionManager == null) return (null, null, null);
        var pinned = Volatile.Read(ref _pinnedSession);
        if (pinned != null && TryResolvePinnedSession(sessionManager, pinned, out var pinnedSession))
            return (pinnedSession, null, null);

        GlobalSystemMediaTransportControlsSession? session = null;
        string? spotifyGroundTruth = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? resolvedMediaProps = null;
        var osCurrentSession = sessionManager.GetCurrentSession();

        if (_activeDisplaySession != null && !forceRefresh && !IsSessionStillPresent(_activeDisplaySession))
        {
            RuntimeLog.Debug(MediaSessionLogTag, "Active display session no longer listed (app closed) — forcing rescan.");
            DetachActiveDisplaySession();
        }

        if (_activeDisplaySession != null && !forceRefresh)
            (session, spotifyGroundTruth, resolvedMediaProps) = await TryReuseActiveDisplaySessionAsync(osCurrentSession);

        if (session != null)
            return (session, spotifyGroundTruth, resolvedMediaProps);

        GlobalSystemMediaTransportControlsSession? bestSession = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? bestProps = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? activeDisplayProps = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? fallbackActiveProps = null;
        int bestScore = -1;
        bool hasAnyActiveSession = false;
        GlobalSystemMediaTransportControlsSession? fallbackActiveSession = null;

        try
        {
            var sessions = sessionManager.GetSessions();
            if (spotifyGroundTruth == null && ContainsSpotifySession(sessions))
                spotifyGroundTruth = _spotifyWindowTitle();

            var newlyPlayingSessions = new List<GlobalSystemMediaTransportControlsSession>();
            var scanTimeUtc = DateTime.UtcNow;

            for (int idx = 0; idx < sessions.Count; idx++)
            {
                var s = sessions[idx];
                try
                {
                    var sourceApp = s.SourceAppUserModelId ?? "";
                    if (IsIgnoredSourceApp(sourceApp)) continue;

                    if (!IsSessionPlayingStatus(s.GetPlaybackInfo().PlaybackStatus))
                        continue;

                    hasAnyActiveSession = true;
                    if (!_sessionState.GetPlayingState(BuildSessionInstanceKey(s)))
                        newlyPlayingSessions.Add(s);

                    if (fallbackActiveSession == null || AreSameSession(s, osCurrentSession))
                        fallbackActiveSession = s;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-ACTIVE-SCAN", ex.ToString());
                }
            }

            // A first scan can discover several playing tabs at once. Enumeration
            // order is not playback recency; prefer the OS session in that case.
            var newlyPlayingSession = MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
                newlyPlayingSessions, osCurrentSession, AreSameSession);
            if (newlyPlayingSession != null)
            {
                _latestPlayingSessionKey = BuildSessionInstanceKey(newlyPlayingSession);
                _latestPlayingSessionStartUtc = scanTimeUtc;
            }

            for (int idx = 0; idx < sessions.Count; idx++)
            {
                var s = sessions[idx];
                try
                {
                    var sourceApp = s.SourceAppUserModelId ?? "";
                    if (IsIgnoredSourceApp(sourceApp)) continue;

                    string sessionInstanceKey = BuildSessionInstanceKey(s);
                    var status = s.GetPlaybackInfo().PlaybackStatus;
                    var nowUtc = DateTime.UtcNow;

                    bool isActive = IsSessionPlayingStatus(status);
                    bool isPrevActive = AreSameSession(_activeDisplaySession, s);
                    bool wasPlaying = _sessionState.GetPlayingState(sessionInstanceKey);

                    if (isActive && !wasPlaying)
                    {
                        _sessionState.SetPlayStartTime(sessionInstanceKey, scanTimeUtc);
                        ClearSessionSourceOverride(sessionInstanceKey, sourceApp);
                        _lastTrackSignature = "";
                    }

                    _sessionState.SetPlayingState(sessionInstanceKey, isActive);

                    GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                    for (int i = 0; i < 2; i++)
                    {
                        props = await s.TryGetMediaPropertiesAsync();
                        if (props != null && !string.IsNullOrEmpty(props.Title)) break;
                        if (i == 0) await Task.Delay(25, _bgCts?.Token ?? CancellationToken.None);
                    }

                    if (isPrevActive)
                        activeDisplayProps = props;
                    if (fallbackActiveSession != null && AreSameSession(s, fallbackActiveSession))
                        fallbackActiveProps = props;

                    bool hasTitle = props != null && !string.IsNullOrEmpty(props.Title);
                    bool hasArtist = props != null && !string.IsNullOrEmpty(props.Artist);

                    bool isSpotify = sourceApp.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase);
                    bool isMusic = sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase);
                    bool isYouTube = sourceApp.Contains(YouTubePlatformName, StringComparison.OrdinalIgnoreCase);
                    bool isBrowser = sourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                     sourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                                     sourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                                     sourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase);

                    bool isOsCurrent = AreSameSession(s, osCurrentSession);
                    bool isDedicatedMusicAppPlaying = isOsCurrent && (isBrowser || isYouTube) &&
                                                      IsAnyDedicatedMusicSessionPlaying(sessions);

                    if (isActive)
                        _sessionState.SetLastPlayingTime(sessionInstanceKey, nowUtc);

                    double? playStartAge = isActive && _sessionState.TryGetPlayStartTime(sessionInstanceKey, out var playStartUtc)
                        ? (nowUtc - playStartUtc).TotalSeconds
                        : null;

                    double? latestPlayingAge = !string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                               sessionInstanceKey == _latestPlayingSessionKey
                        ? (nowUtc - _latestPlayingSessionStartUtc).TotalSeconds
                        : null;

                    double? lastPlayingIdle = _sessionState.TryGetLastPlayingTime(sessionInstanceKey, out var lastPlaying)
                        ? (DateTime.UtcNow - lastPlaying).TotalSeconds
                        : null;

                    double? timelineAge = null;
                    bool timelineBoost = false;
                    bool timelinePenalty = false;
                    try
                    {
                        var timeline = s.GetTimelineProperties();
                        if (timeline != null)
                        {
                            timelineAge = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                            if (isActive)
                            {
                                var advance = _sessionState.RecordTimelinePosition(
                                    sessionInstanceKey, timeline.Position, nowUtc);

                                if (advance == MediaSessionState.TimelineAdvanceResult.Advanced ||
                                    _sessionState.IsRecentlyAdvancing(sessionInstanceKey, nowUtc, TimeSpan.FromSeconds(6)))
                                {
                                    timelineBoost = true;
                                }
                                else if (advance == MediaSessionState.TimelineAdvanceResult.Stalled)
                                {
                                    timelinePenalty = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-TIMELINE", ex.ToString());
                    }

                    int score = SessionScorer.Score(new SessionScoreInputs
                    {
                        HasTitle = hasTitle,
                        HasArtist = hasArtist,
                        HasThumbnail = props?.Thumbnail != null,
                        ArtistIsNonGeneric = hasArtist && props!.Artist != YouTubePlatformName && props.Artist != "Browser",
                        IsSpotify = isSpotify,
                        IsMusic = isMusic,
                        IsYouTube = isYouTube,
                        IsBrowser = isBrowser,
                        IsOsCurrent = isOsCurrent,
                        DedicatedMusicAppPlaying = isDedicatedMusicAppPlaying,
                        IsActive = isActive,
                        PlayStartAgeSeconds = playStartAge,
                        LatestPlayingAgeSeconds = latestPlayingAge,
                        LastPlayingIdleSeconds = lastPlayingIdle,
                        TimelineAgeSeconds = timelineAge,
                        TimelineBoost = timelineBoost,
                        TimelinePenalty = timelinePenalty,
                    });

                    bool eligible = hasAnyActiveSession
                        ? isActive
                        : (isActive || isPrevActive || isOsCurrent);

                    if (score > bestScore && eligible)
                    {
                        bestScore = score;
                        bestSession = s;
                        bestProps = props;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-SCORE", ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-DETECT-SESSIONS", ex.ToString());
        }

        if (hasAnyActiveSession && bestSession != null)
        {
            try
            {
                if (!IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus) && fallbackActiveSession != null)
                {
                    bestSession = fallbackActiveSession;
                    bestProps = fallbackActiveProps;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-BESTCHECK", ex.ToString());
            }
        }

        if (bestSession != null && _activeDisplaySession != null && !AreSameSession(bestSession, _activeDisplaySession))
        {
            bool bestIsPlaying = false;
            try
            {
                bestIsPlaying = IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-BEST-PLAYSTATE", ex.ToString());
            }

            if (!bestIsPlaying && IsSessionStillPresent(_activeDisplaySession))
            {
                bestSession = _activeDisplaySession;
                bestProps = activeDisplayProps;
            }
        }

        if (bestSession != null && _activeDisplaySession != null && !AreSameSession(bestSession, _activeDisplaySession))
        {
            if (ShouldHoldCurrentDisplaySession(bestSession, _activeDisplaySession, osCurrentSession))
            {
                bestSession = _activeDisplaySession;
                bestProps = activeDisplayProps;
            }
        }
        else
        {
            _pendingSessionInstanceKey = "";
        }

        if (bestSession != null)
        {
            session = bestSession;
            resolvedMediaProps = bestProps;
        }
        else if (hasAnyActiveSession && fallbackActiveSession != null)
        {
            session = fallbackActiveSession;
            resolvedMediaProps = fallbackActiveProps;
        }
        else
        {
            session = sessionManager.GetCurrentSession();
        }

        return (session, spotifyGroundTruth, resolvedMediaProps);
    }

}
