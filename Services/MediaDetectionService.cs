using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public class MediaDetectionService : IMediaDetectionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly IMediaMetadataLookupService _metadataLookup;
    private readonly IMediaArtworkService _artworkService;
    private readonly IWindowTitleScanner _windowTitleScanner;
    private readonly MediaSessionVolumeService _volumeService;
    private readonly MediaTransportControlService _transportService;

    private readonly Channel<ChangeType> _changeChannel;
    private CancellationTokenSource? _bgCts;
    private Task? _processingTask;
    private Task? _heartbeatTask;
    private DetectionMode _currentMode = DetectionMode.Idle;
    private long _lastEventTimeTicks;
    private int _awaitingMetadataPollGeneration;
    private DateTime _transientSessionGapStartedUtc = DateTime.MinValue;
    private DateTime _youtubeAdTransitionStartedUtc = DateTime.MinValue;
    private DateTime _startupProgressSyncUntilUtc = DateTime.MinValue;

    public event EventHandler<MediaInfo>? MediaChanged;

    private string _lastTrackSignature = "";
    private string _lastThumbTrackIdentity = "";
    private string _cachedSource = "";
    private BitmapImage? _cachedThumbnail;
    private string _cachedThumbnailSource = "";

    private string _pendingSessionAppId = "";
    private DateTime _pendingSessionStartTime = DateTime.MinValue;
    private string _pendingNewTrackKey = "";
    private DateTime _pendingNewTrackSince = DateTime.MinValue;

    private CancellationTokenSource? _thumbCts;
    private string _lastStableTrackSignature = "";
    private DateTime _emptyMetadataStartTime = DateTime.MinValue;
    private readonly MediaSessionState _sessionState = new();
    private readonly MediaSourceCache _sourceCache;
    private string _latestPlayingSessionKey = "";
    private DateTime _latestPlayingSessionStartUtc = DateTime.MinValue;

    private DateTime _lastMetadataChangeTime = DateTime.MinValue;
    private string _lastTrackName = "";
    private readonly MediaTimelineSimulator _timelineSimulator = new();
    private string _lastSoundCloudArtworkIdentity = "";
    private DateTime _lastSoundCloudArtworkAttemptTimeUtc = DateTime.MinValue;
    private string _youTubeFetchIdentity = "";
    private DateTime _lastYouTubeThumbnailAttemptTimeUtc = DateTime.MinValue;
    private int _youTubeFetchGeneration;
    private int _youTubeFetchInFlight;
    private string _soundCloudFetchIdentity = "";
    private int _soundCloudFetchGeneration = 0;
    private int _soundCloudFetchInFlight = 0;
    private int _thumbnailFetchGeneration = 0;
    private static readonly TimeSpan YouTubeThumbnailRetryInterval = TimeSpan.FromSeconds(1.1);
    private static readonly TimeSpan SoundCloudArtworkRetryInterval = TimeSpan.FromSeconds(1.1);

    private string _lastSource = "";
    private bool _lastIsPlaying = false;
    private bool _lastIsThrottled = false;
    private bool _lastSeekEnabled = false;
    private TimeSpan _lastPosition = TimeSpan.Zero;
    private string _lastPublishedSignature = "";
    private string _lastPublishedTrackIdentity = "";
    private string _lastPublishedTrackOnlyIdentity = "";
    private string _lastPublishedSourceAppId = "";
    private string _lastPublishedSessionInstanceKey = "";

    private DateTime _lastSourceConfirmedTime = DateTime.MinValue;
    private string _stableSource = "";
    private string _stableArtist = "";
    private string _stableSourceTrackIdentity = "";

    public MediaDetectionService(
        IMediaMetadataLookupService metadataLookup,
        IMediaArtworkService artworkService,
        IWindowTitleScanner windowTitleScanner)
    {
        _metadataLookup = metadataLookup;
        _artworkService = artworkService;
        _windowTitleScanner = windowTitleScanner;
        _volumeService = new MediaSessionVolumeService();
        _transportService = new MediaTransportControlService(GetActiveSession);

        _changeChannel = Channel.CreateBounded<ChangeType>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _sourceCache = new MediaSourceCache();
        _sourceCache.Load();
    }
    public IMediaArtworkService ArtworkService => _artworkService;

    private GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        if (_activeDisplaySession != null)
            return _activeDisplaySession;

        if (!string.IsNullOrEmpty(_lastPublishedSourceAppId))
        {
            var match = FindSessionBySourceAppId(_lastPublishedSourceAppId);
            if (match != null)
                return match;
        }

        return _currentSession ?? _sessionManager?.GetCurrentSession();
    }

    private GlobalSystemMediaTransportControlsSession? FindSessionBySourceAppId(string sourceAppId)
    {
        try
        {
            var sessions = _sessionManager?.GetSessions();
            if (sessions == null) return null;

            foreach (var s in sessions)
            {
                var id = s.SourceAppUserModelId ?? "";
                if (string.Equals(id, sourceAppId, StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-FIND-SESSION", $"Failed to find session for {sourceAppId}: {ex.Message}");
        }
        return null;
    }

    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private GlobalSystemMediaTransportControlsSession? _activeDisplaySession;
    private DateTime _lastSessionSwitchTime = DateTime.MinValue;

    public void Start()
    {
        if (_disposed) return;
        if (_sessionManager != null) return;

        _startupProgressSyncUntilUtc = DateTime.UtcNow.AddSeconds(5);

        StartCoreAsync().SafeFireAndForget("MEDIA-START");
    }

    private async Task StartCoreAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnSessionChanged;
            _sessionManager.SessionsChanged += OnSessionsChanged;

            await SubscribeToCurrentSession();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-INIT", $"Failed to init SMTC: {ex.Message}");
        }

        _bgCts = new CancellationTokenSource();
        var ct = _bgCts.Token;
        _processingTask = Task.Run(() => ProcessingLoopAsync(ct), ct);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), ct);

        _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
        _ = Task.Run(async () =>
        {
            int[] stagedDelaysMs = { 120, 350, 800 };
            foreach (int delayMs in stagedDelaysMs)
            {
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch
                {
                    return;
                }

                _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
            }
        }, ct);
    }

    public void Stop()
    {
        if (_disposed) return;
        UnsubscribeFromSession();
        _bgCts?.Cancel();
    }

    private async Task SubscribeToCurrentSession()
    {
        UnsubscribeFromSession();

        if (_sessionManager == null) return;

        try
        {
            _currentSession = _sessionManager.GetCurrentSession();
            if (_currentSession != null)
            {
                _currentSession.TimelinePropertiesChanged += OnTimelineChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackChanged;
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                RuntimeLog.Debug("MEDIA-SESSION", () => $"Subscribed to session: {_currentSession.SourceAppUserModelId}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-SESSION", $"Failed to subscribe to session: {ex.Message}");
        }
    }

    private void UnsubscribeFromSession()
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.TimelinePropertiesChanged -= OnTimelineChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackChanged;
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-UNSUBSCRIBE", ex.ToString());
            }
            _currentSession = null;
        }
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.Timeline);
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.Playback);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changeChannel.Writer.TryWrite(ChangeType.MediaProperties);
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Log("Session Changed", "System-wide session focus shifted");
        _changeChannel.Writer.TryWrite(ChangeType.SessionChanged);
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Log("Sessions Changed", "Session list changed (app opened/closed)");
        _changeChannel.Writer.TryWrite(ChangeType.SessionChanged);
    }

    private void Log(string tag, string message)
    {
        RuntimeLog.Log($"MEDIA-{tag}", message);
    }

    #region Background Processing

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        await foreach (var change in _changeChannel.Reader.ReadAllAsync(ct))
        {
            try
            {

                await Task.Delay(50, ct);
                var types = change;
                while (_changeChannel.Reader.TryRead(out var extra))
                {
                    types |= extra;
                }

                bool forceRefresh = types.HasFlag(ChangeType.MediaProperties)
                    || types.HasFlag(ChangeType.Playback)
                    || types.HasFlag(ChangeType.SessionChanged)
                    || types.HasFlag(ChangeType.ForceRefresh);

                if (types.HasFlag(ChangeType.SessionChanged))
                {
                    await SubscribeToCurrentSession();
                }

                await UpdateMediaInfoAsync(forceRefresh);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ProcessError", ex.Message);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = _currentMode switch
            {
                DetectionMode.Idle => TimeSpan.FromSeconds(5),
                DetectionMode.AwaitingMetadata => TimeSpan.FromSeconds(3),
                DetectionMode.EventDriven => TimeSpan.FromSeconds(3),
                DetectionMode.ThrottledMedia => TimeSpan.FromMilliseconds(1500),
                _ => TimeSpan.FromSeconds(3)
            };

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) { break; }

            if (_currentMode == DetectionMode.AwaitingMetadata)
            {
                continue;
            }

            var lastEvtTicks = Interlocked.Read(ref _lastEventTimeTicks);
            if (_currentMode == DetectionMode.Idle
                && lastEvtTicks > 0
                && (DateTime.UtcNow - new DateTime(lastEvtTicks)).TotalSeconds > 10)
            {
                continue;
            }

            _changeChannel.Writer.TryWrite(ChangeType.Heartbeat);
        }
    }

    private void UpdateDetectionMode(MediaInfo info, bool isSessionGapRecovery = false)
    {
        var oldMode = _currentMode;
        _currentMode = MediaMetadataRecoveryPolicy.SelectDetectionMode(
            info.IsAnyMediaPlaying,
            info.CurrentTrack,
            info.IsThrottled,
            isSessionGapRecovery);

        if (oldMode != DetectionMode.AwaitingMetadata &&
            _currentMode == DetectionMode.AwaitingMetadata)
        {
            StartAwaitingMetadataPolling();
        }
        else if (oldMode == DetectionMode.AwaitingMetadata &&
                 _currentMode != DetectionMode.AwaitingMetadata)
        {
            Interlocked.Increment(ref _awaitingMetadataPollGeneration);
        }

        if (oldMode != _currentMode)
        {
            RuntimeLog.Debug("MEDIA-MODE", () => $"Detection mode: {oldMode} -> {_currentMode} (Playing={info.IsAnyMediaPlaying}, Track='{info.CurrentTrack}', Throttled={info.IsThrottled})");
        }
    }

    private void StartAwaitingMetadataPolling()
    {
        var cts = _bgCts;
        if (cts == null || cts.IsCancellationRequested)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _awaitingMetadataPollGeneration);
        DateTime startedUtc = DateTime.UtcNow;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (generation == Volatile.Read(ref _awaitingMetadataPollGeneration))
                {
                    var interval = MediaMetadataRecoveryPolicy.GetAwaitingMetadataPollInterval(
                        DateTime.UtcNow - startedUtc);
                    await Task.Delay(interval, token);

                    if (generation != Volatile.Read(ref _awaitingMetadataPollGeneration))
                    {
                        return;
                    }

                    _changeChannel.Writer.TryWrite(ChangeType.Heartbeat);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-METADATA-POLL", ex.ToString());
            }
        }, token);
    }

    #endregion

    private async Task UpdateMediaInfoAsync(bool forceRefresh = false)
    {

        if (!await _updateLock.WaitAsync(forceRefresh ? 500 : 0)) return;

        try
        {
            var info = new MediaInfo();
            List<string>? windowTitles = null;

            bool shouldQuarantineBrowserAd = await TryGetMediaSessionInfoAsync(info, forceRefresh, () =>
            {
                windowTitles ??= GetAllWindowTitles();
                return windowTitles;
            });

            if (UpdateTransientSessionGapHold(info))
                return;

            if (shouldQuarantineBrowserAd)
            {
                UpdateDetectionMode(info, isSessionGapRecovery: true);
                return;
            }

            ApplyWindowTitleFallback(info, ref windowTitles);
            ApplyVideoTimelineRecovery(info, ref windowTitles);

            TrackNameChangeBookkeeping(info);
            PreserveSoundCloudSourceIfNeeded(info, ref windowTitles);

            info.IsThrottled = _timelineSimulator.IsThrottled;
            UpdateDetectionMode(info);
            var currentSignature = info.GetSignature();

            if (UpdateEmptyMetadataHold(info, currentSignature))
                return;

            bool isNewTrackForThumbnail = ComputeIsNewTrackForThumbnail(info);

            if (!await TryPublishMediaChangeAsync(info, currentSignature, isNewTrackForThumbnail, forceRefresh))
                return;

            StartThumbnailFetchIfNeeded(info, isNewTrackForThumbnail);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private void TrackNameChangeBookkeeping(MediaInfo info)
    {
        if (info.CurrentTrack == _lastTrackName) return;

        _lastTrackName = info.CurrentTrack;
        _lastMetadataChangeTime = DateTime.Now;

        if (!string.IsNullOrEmpty(info.CurrentTrack))
        {
            _stableArtist = "";
        }

        if (_timelineSimulator.IsThrottled) _timelineSimulator.Reset();
    }

    private void PreserveSoundCloudSourceIfNeeded(MediaInfo info, ref List<string>? windowTitles)
    {
        if (!ShouldPreserveSoundCloudSourceDuringTrackSwitch(info)) return;

        windowTitles ??= GetAllWindowTitles();
        bool hasYouTubeWindowHint = MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(windowTitles)) == MediaPlatform.YouTube;
        bool hasSoundCloudWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "soundcloud");
        if (!hasYouTubeWindowHint && hasSoundCloudWindowMatch)
        {
            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
            info.IsSoundCloudRunning = true;
            SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());
        }
    }

    private bool UpdateEmptyMetadataHold(MediaInfo info, string currentSignature)
    {
        var r = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            info.CurrentTrack, info.IsAnyMediaPlaying, currentSignature, _lastSource,
            _emptyMetadataStartTime, _lastStableTrackSignature, DateTime.Now);
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
            RuntimeLog.Debug("MEDIA-SESSION-GAP", () =>
                $"Holding transient session gap for source='{_lastSource}' " +
                $"lastTrack='{_lastTrackName}'");
        }

        UpdateDetectionMode(info, isSessionGapRecovery: true);
        return true;
    }

    private bool ComputeIsNewTrackForThumbnail(MediaInfo info)
        => PublishedTrackMatcher.IsNewTrackForThumbnail(_lastPublishedTrackOnlyIdentity, info.CurrentTrack);

    private async Task<bool> TryPublishMediaChangeAsync(MediaInfo info, string currentSignature, bool isNewTrackForThumbnail, bool forceRefresh)
    {
        bool metadataChanged = currentSignature != _lastPublishedSignature;
        bool playbackChanged = info.IsPlaying != _lastIsPlaying;
        bool sourceChanged = info.MediaSource != _lastSource;
        bool seekCapabilityChanged = info.IsSeekEnabled != _lastSeekEnabled;
        bool significantJump = Math.Abs((info.Position - _lastPosition).TotalSeconds) >= (info.IsThrottled ? 5.0 : 1.5);
        bool throttleChanged = info.IsThrottled != _lastIsThrottled;
        bool inStartupSyncWindow = DateTime.UtcNow <= _startupProgressSyncUntilUtc;
        bool startupTimelineSync = inStartupSyncWindow &&
                                   info.IsPlaying &&
                                   (info.Duration.TotalSeconds > 0 || info.Position.TotalSeconds > 0) &&
                                   Math.Abs((info.Position - _lastPosition).TotalSeconds) >= 0.2;

        if (!(forceRefresh || metadataChanged || playbackChanged || sourceChanged || (significantJump && !info.IsThrottled) || seekCapabilityChanged || throttleChanged || startupTimelineSync))
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

        CommitPublishedState(info, currentSignature);

        SuppressIntermediateYouTubeThumbnail(info, isNewTrackForThumbnail);

        await FireMediaChangedAsync(info);

        if (!forceRefresh && metadataChanged && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
        }

        return true;
    }

    private bool ShouldDebounceNewTrack(MediaInfo info, bool forceRefresh)
    {
        var r = MediaTimingDecisions.EvaluateNewTrackDebounce(
            info.CurrentTrack, info.CurrentArtist, info.IsPlaying, forceRefresh,
            _lastPublishedTrackIdentity, _pendingNewTrackKey, _pendingNewTrackSince, DateTime.UtcNow);
        _pendingNewTrackKey = r.pendingKey;
        _pendingNewTrackSince = r.pendingSince;
        return r.debounce;
    }

    private void CommitPublishedState(MediaInfo info, string currentSignature)
    {
        _lastPublishedSignature = currentSignature;
        _lastTrackSignature = currentSignature;
        _lastIsPlaying = info.IsPlaying;
        _lastSource = info.MediaSource;
        _lastPosition = info.Position;
        _lastSeekEnabled = info.IsSeekEnabled;
        _lastIsThrottled = info.IsThrottled;
        _lastPublishedTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
        _lastPublishedSourceAppId = info.SourceAppId ?? "";
        _lastPublishedSessionInstanceKey = info.SessionInstanceKey ?? "";
    }

    private void SuppressIntermediateYouTubeThumbnail(MediaInfo info, bool isNewTrackForThumbnail)
    {
        bool willFetchYouTubeThumbnail = (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info)))
            && !string.IsNullOrEmpty(info.CurrentTrack)
            && !isNewTrackForThumbnail
            && (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120)
            && (_cachedThumbnail == null || _cachedThumbnail.PixelWidth < 200);

        bool hasGoodSmtcArtwork = false;
        if (willFetchYouTubeThumbnail && info.Thumbnail != null && info.Thumbnail.PixelWidth >= 200)
        {
            double smtcAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            hasGoodSmtcArtwork = smtcAspect >= 0.85 && smtcAspect <= 1.15;
        }

        if (willFetchYouTubeThumbnail && !hasGoodSmtcArtwork)
        {
            if (isNewTrackForThumbnail)
            {
                info.Thumbnail = null;
            }
            else if (_cachedThumbnail != null && _cachedThumbnail.PixelWidth >= 200)
            {
                info.Thumbnail = _cachedThumbnail;
            }
            else
            {
                info.Thumbnail = _cachedThumbnail;
            }
        }

        if (isNewTrackForThumbnail &&
            (info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info))) &&
            info.Thumbnail != null)
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            if (thumbAspect > 1.3)
            {
                RuntimeLog.Debug("MEDIA-THUMB-STALE", () =>
                    $"Suppressing stale wide SMTC thumb on new track publish: " +
                    $"track='{info.CurrentTrack}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2}");
                info.Thumbnail = null;
            }
        }
    }

    private async Task FireMediaChangedAsync(MediaInfo info)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            RuntimeLog.Debug("MEDIA-EVENT", () =>
                $"Firing MediaChanged: Source={info.MediaSource}, App={info.SourceAppId}, Track='{info.CurrentTrack}', Artist='{info.CurrentArtist}', " +
                $"Pos={info.Position.TotalSeconds:F3}s, Dur={info.Duration.TotalSeconds:F3}s, IsPlaying={info.IsPlaying}, " +
                $"Rate={info.PlaybackRate:F3}, LastUpdated={info.LastUpdated:O}");
            await dispatcher.InvokeAsync(() => MediaChanged?.Invoke(this, info));
        }
        else
        {
            RuntimeLog.Warn("MEDIA-EVENT", "No dispatcher, cannot fire MediaChanged event");
        }
    }

    private void ApplyWindowTitleFallback(MediaInfo info, ref List<string>? windowTitles)
    {
        bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.Platform == MediaPlatform.Browser) || info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource);

        bool recentlyHadMedia = !info.IsAnyMediaPlaying &&
                                (MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.YouTube || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.Browser || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.SoundCloud) &&
                                _emptyMetadataStartTime != DateTime.MinValue &&
                                (DateTime.Now - _emptyMetadataStartTime).TotalSeconds < 6.0;

        if (needsFallback && (info.IsAnyMediaPlaying || recentlyHadMedia))
        {
            windowTitles ??= GetAllWindowTitles();

            if (info.Platform != MediaPlatform.Spotify)
            {
                var spotifyProcesses = Process.GetProcessesByName("Spotify");
                if (spotifyProcesses.Length > 0)
                {
                    foreach (var proc in spotifyProcesses)
                    {
                        string wTitle = proc.MainWindowTitle;
                        if (!string.IsNullOrEmpty(wTitle) && wTitle != "Spotify" && !wTitle.ToLower().EndsWith("spotify"))
                        {
                            info.IsSpotifyRunning = true;
                            info.IsSpotifyPlaying = true;
                            info.IsAnyMediaPlaying = true;
                            info.IsPlaying = true;
                            info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
                            ParseSpotifyTitle(wTitle, info);
                            if (!string.IsNullOrEmpty(info.CurrentArtist)) _stableArtist = info.CurrentArtist;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(info.CurrentTrack))
            {
                foreach (var title in windowTitles)
                {
                    var lowerTitle = title.ToLower();

                    if (lowerTitle.Contains("youtube") && !lowerTitle.StartsWith("youtube -") && lowerTitle != "youtube")
                    {
                        if (MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(
                            info.IsSpotifyPlaying,
                            info.MediaSource))
                        {
                            info.IsAnyMediaPlaying = true;
                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.YouTubeTitle = ExtractVideoTitle(title, "YouTube");
                            info.CurrentTrack = info.YouTubeTitle;

                            info.CurrentArtist = !string.IsNullOrEmpty(_stableArtist) && (DateTime.Now - _lastSourceConfirmedTime).TotalSeconds < 15.0 ? _stableArtist : "YouTube";
                            break;
                        }
                    }

                    if (lowerTitle.Contains("soundcloud") && !lowerTitle.StartsWith("soundcloud -") && lowerTitle != "soundcloud")
                    {
                        if (MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(
                            info.IsSpotifyPlaying,
                            info.MediaSource))
                        {
                            info.IsAnyMediaPlaying = true;
                            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                            info.IsSoundCloudRunning = true;

                            string extractedTitle = ExtractVideoTitle(title, "SoundCloud");
                            if (extractedTitle.Contains(" - "))
                            {
                                int lastSep = extractedTitle.LastIndexOf(" - ", StringComparison.Ordinal);
                                string firstPart = extractedTitle.Substring(0, lastSep).Trim();
                                string secondPart = extractedTitle.Substring(lastSep + 3).Trim();

                                int firstWordCount = firstPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                                int secondWordCount = secondPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

                                bool likelyTrackFirst = (firstWordCount <= 3 && secondWordCount > firstWordCount) ||
                                                       secondPart.StartsWith("by ", StringComparison.OrdinalIgnoreCase) ||
                                                       secondPart.Contains(" by ", StringComparison.OrdinalIgnoreCase);

                                if (likelyTrackFirst)
                                {
                                    info.CurrentTrack = firstPart;
                                    info.CurrentArtist = secondPart;
                                }
                                else
                                {
                                    info.CurrentArtist = firstPart;
                                    info.CurrentTrack = secondPart;
                                }
                            }
                            else
                            {
                                info.CurrentTrack = extractedTitle;
                                info.CurrentArtist = "SoundCloud";
                            }

                            SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                            break;
                        }
                    }
                }
            }
        }
    }

    private void ApplyVideoTimelineRecovery(MediaInfo info, ref List<string>? windowTitles)
    {
        bool isVideoSource = info.Platform == MediaPlatform.YouTube ||
                             (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
        if (isVideoSource && info.IsPlaying)
        {

            _timelineSimulator.UpdateObservedPosition(info.Position);

            double progress = info.Duration.TotalSeconds > 0 ? info.Position.TotalSeconds / info.Duration.TotalSeconds : 0;
            bool positionStuck = _timelineSimulator.IsPositionStuck(TimeSpan.FromSeconds(1.5));
            bool atEndStuck = _timelineSimulator.IsAtEndStuck(progress, _lastMetadataChangeTime, TimeSpan.FromSeconds(1.2));

            if (progress >= 1.0 && info.Duration.TotalSeconds > 0 && _timelineSimulator.IsThrottled)
            {
                _timelineSimulator.Reset();
                _changeChannel.Writer.TryWrite(ChangeType.ForceRefresh);
            }

            if (positionStuck || atEndStuck)
            {
                bool foundRecovery = false;

                if (!string.IsNullOrEmpty(info.CurrentTrack) && IsLikelyYouTube(info))
                {

                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                    info.IsYouTubeRunning = true;

                    _timelineSimulator.ApplySimulatedTimeline(info, atEndStuck);
                    foundRecovery = true;
                }

                if (!foundRecovery)
                {

                    windowTitles ??= GetAllWindowTitles();

                    foreach (var title in windowTitles)
                    {
                        var lowerWinTitle = title.ToLower();
                        if (lowerWinTitle.Contains("youtube") && !lowerWinTitle.StartsWith("youtube -") && lowerWinTitle != "youtube")
                        {
                            var extractedTitle = ExtractVideoTitle(title, "YouTube");
                            string trackName = extractedTitle;
                            string artistName = "YouTube";

                            if (extractedTitle.Contains(" - "))
                            {
                                int lastSep = extractedTitle.LastIndexOf(" - ", StringComparison.Ordinal);
                                string firstPart = extractedTitle.Substring(0, lastSep).Trim();
                                string secondPart = extractedTitle.Substring(lastSep + 3).Trim();

                                int firstWordCount = firstPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                                int secondWordCount = secondPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

                                bool likelyTrackFirst = (firstWordCount <= 3 && secondWordCount > firstWordCount) ||
                                                       secondPart.StartsWith("by ", StringComparison.OrdinalIgnoreCase) ||
                                                       secondPart.Contains(" by ", StringComparison.OrdinalIgnoreCase);

                                if (likelyTrackFirst)
                                {
                                    trackName = firstPart;
                                    artistName = secondPart;
                                }
                                else
                                {
                                    trackName = secondPart;
                                    artistName = firstPart;
                                }
                            }

                            bool isNewTrack = trackName != _lastTrackName && !_lastTrackName.Contains(trackName);

                            if (isNewTrack)
                            {

                                info.CurrentTrack = trackName;
                                info.CurrentArtist = artistName;
                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                info.IsYouTubeRunning = true;
                                info.IsThrottled = true;
                                _timelineSimulator.EnterThrottledMode();

                                _timelineSimulator.ResetRecoveredData();
                                info.Duration = TimeSpan.Zero;

                                info.Position = TimeSpan.FromSeconds(1.5);
                                info.LastUpdated = DateTimeOffset.Now;
                                foundRecovery = true;
                                break;
                            }
                            else if (positionStuck || _timelineSimulator.IsThrottled)
                            {

                                info.CurrentTrack = trackName;
                                info.CurrentArtist = artistName;
                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                info.IsYouTubeRunning = true;
                                info.IsThrottled = true;
                                _timelineSimulator.EnterThrottledMode();

                                if (_timelineSimulator.RecoveredDuration.TotalSeconds > 0)
                                {
                                    info.Duration = _timelineSimulator.RecoveredDuration;
                                }
                                else
                                {

                                    info.Duration = TimeSpan.Zero;
                                }

                                if (_timelineSimulator.RecoveredThumbnail != null) info.Thumbnail = _timelineSimulator.RecoveredThumbnail;

                                var timeOnTrack = DateTime.Now - _lastMetadataChangeTime;
                                info.Position = timeOnTrack;
                                info.LastUpdated = DateTimeOffset.Now;
                                foundRecovery = true;
                                break;
                            }
                        }
                    }
                }

                if (!foundRecovery && _timelineSimulator.IsThrottled)
                {
                    _timelineSimulator.TryExitThrottleIfStalled(TimeSpan.FromSeconds(3.5));
                }
            }
            else if (_timelineSimulator.IsThrottled)
            {
                _timelineSimulator.TryExitThrottleIfPositionResumed(TimeSpan.FromMilliseconds(500));
            }
        }
        else
        {
            _timelineSimulator.Reset();
        }
    }

    private void StartThumbnailFetchIfNeeded(MediaInfo info, bool isNewTrackForThumbnail)
    {
        bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                            TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                            MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;

        bool browserHasYouTubeTabOpen = false;
        if (info.Platform == MediaPlatform.Browser && !hasSoundCloudSessionOverride)
        {
            try
            {
                var browserUrl = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
                if (!string.IsNullOrEmpty(browserUrl) &&
                    (browserUrl!.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                     browserUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)))
                {
                    browserHasYouTubeTabOpen = true;
                }
            }
            catch { }
        }

        var sourcePlan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsYouTube = info.Platform == MediaPlatform.YouTube,
            PlatformIsBrowser = info.Platform == MediaPlatform.Browser,
            PlatformIsSoundCloud = info.Platform == MediaPlatform.SoundCloud,
            HasSourceApp = !string.IsNullOrEmpty(info.SourceAppId),
            IsLikelyYouTube = IsLikelyYouTube(info),
            HasSoundCloudSessionOverride = hasSoundCloudSessionOverride,
            BrowserHasYouTubeTabOpen = browserHasYouTubeTabOpen,
            HasTrack = !string.IsNullOrEmpty(info.CurrentTrack),
            ThumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail),
        });
        bool isPotentialYouTube = sourcePlan.IsPotentialYouTube;
        bool isPotentialSoundCloud = sourcePlan.IsPotentialSoundCloud;

        string youTubeTrackIdentity = isPotentialYouTube
            ? $"{BuildTrackIdentity(info.CurrentTrack, "")}|{info.SourceAppId}|{info.SessionInstanceKey}"
            : "";
        string soundCloudTrackIdentity = isPotentialSoundCloud
            ? BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist)
            : "";

        bool forceFetchForTrackChange = isNewTrackForThumbnail;
        bool needsFetch = forceFetchForTrackChange || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
        if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !forceFetchForTrackChange) needsFetch = false;

        if (forceFetchForTrackChange)
        {
            _windowTitleScanner.InvalidateUrlCaches();
            ClearMismatchCache();
            ForgetVideoIdCacheExceptForTrack(BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
        }

        if (needsFetch && !forceFetchForTrackChange && _cachedThumbnail != null && _cachedThumbnail.PixelWidth >= 200)
        {
            needsFetch = false;
            info.Thumbnail = _cachedThumbnail;
            _timelineSimulator.RecoveredThumbnail = _cachedThumbnail;
        }

        if (needsFetch && info.Thumbnail != null && info.Thumbnail.PixelWidth >= 200)
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
            bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                  info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

            if (isNearSquare || isTopicChannel)
            {
                needsFetch = false;
                RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                    $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                    $"thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} " +
                    $"isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
                var cropped = CropToSquare(info.Thumbnail, info.MediaSource ?? "YouTube", forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
                info.Thumbnail = cropped;
                _cachedThumbnail = cropped;
                _cachedThumbnailSource = MediaPlatform.YouTube.ToDisplayString();
                _timelineSimulator.RecoveredThumbnail = cropped;
            }
        }

        if (isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
        {
            bool sameTrackFetchRunning =
                Volatile.Read(ref _youTubeFetchInFlight) == 1 &&
                string.Equals(_youTubeFetchIdentity, youTubeTrackIdentity, StringComparison.Ordinal);
            bool shouldStartYouTubeFetch = ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
            {
                IsNewTrack = forceFetchForTrackChange,
                SameTrackFetchRunning = sameTrackFetchRunning,
                NeedsBetterThumbnail = needsFetch,
                RetryIntervalElapsed =
                    (DateTime.UtcNow - _lastYouTubeThumbnailAttemptTimeUtc) >= YouTubeThumbnailRetryInterval,
            });

            if (!shouldStartYouTubeFetch)
                return;

            _lastYouTubeThumbnailAttemptTimeUtc = DateTime.UtcNow;
            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            var token = _thumbCts.Token;
            int fetchGeneration = Interlocked.Increment(ref _youTubeFetchGeneration);
            _youTubeFetchIdentity = youTubeTrackIdentity;
            Interlocked.Exchange(ref _youTubeFetchInFlight, 1);

            Interlocked.Increment(ref _soundCloudFetchGeneration);
            _soundCloudFetchIdentity = "";
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            bool shouldForceThumbFetch = forceFetchForTrackChange;

            string trackDuringFetch = info.CurrentTrack;
            string artistDuringFetch = info.CurrentArtist;
            string sourceAppDuringFetch = info.SourceAppId ?? "";
            string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
            int generationAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

            bool isBrowserIcon = info.Thumbnail != null && info.Platform == MediaPlatform.Browser;
            bool needsBetterThumb = shouldForceThumbFetch || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

            _ = Task.Run(() => FetchYouTubeThumbnailAsync(
                info, token, shouldForceThumbFetch, trackDuringFetch, artistDuringFetch,
                sourceAppDuringFetch, sessionKeyDuringFetch, generationAtStart,
                fetchGeneration, needsBetterThumb), token);
        }
        else if (isPotentialSoundCloud && !isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            bool isNewSoundCloudTrack = !string.IsNullOrEmpty(soundCloudTrackIdentity) &&
                                        !string.Equals(soundCloudTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
            bool sameTrackFetchRunning =
                Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                string.Equals(_soundCloudFetchIdentity, soundCloudTrackIdentity, StringComparison.Ordinal);

            bool shouldStartSoundCloudFetch = ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
            {
                IsNewSoundCloudTrack = isNewSoundCloudTrack,
                SameTrackFetchRunning = sameTrackFetchRunning,
                ThumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail),
                HasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != MediaPlatform.SoundCloud,
                RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
            });

            if (shouldStartSoundCloudFetch)
            {
                _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
                _lastSoundCloudArtworkIdentity = soundCloudTrackIdentity;
                _thumbCts?.Cancel();
                _thumbCts = new CancellationTokenSource();
                var token = _thumbCts.Token;
                int fetchGeneration = Interlocked.Increment(ref _soundCloudFetchGeneration);
                _soundCloudFetchIdentity = soundCloudTrackIdentity;
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);

                Interlocked.Increment(ref _youTubeFetchGeneration);
                _youTubeFetchIdentity = "";
                Interlocked.Exchange(ref _youTubeFetchInFlight, 0);

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;
                string sourceAppDuringFetch = info.SourceAppId ?? "";
                string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
                int thumbGenAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

                bool requireStrongMatch = true;

                _ = Task.Run(() => FetchSoundCloudThumbnailAsync(info, token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, requireStrongMatch), token);
            }
        }
        else
        {

            _thumbCts?.Cancel();
            Interlocked.Increment(ref _youTubeFetchGeneration);
            _youTubeFetchIdentity = "";
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            _soundCloudFetchIdentity = "";
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
        }
    }
    private async Task FetchYouTubeThumbnailAsync(
        MediaInfo info,
        CancellationToken token,
        bool shouldForceThumbFetch,
        string trackDuringFetch,
        string artistDuringFetch,
        string sourceAppDuringFetch,
        string sessionKeyDuringFetch,
        int generationAtStart,
        int fetchGeneration,
        bool needsBetterThumb)
    {
        try
        {
            bool IsCurrentFetch() =>
                !token.IsCancellationRequested &&
                generationAtStart == Volatile.Read(ref _thumbnailFetchGeneration) &&
                fetchGeneration == Volatile.Read(ref _youTubeFetchGeneration) &&
                IsStillSamePublishedTrack(
                    trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch);

            async Task<bool> ApplyIfCurrentAsync(Action apply)
            {
                await _updateLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrentFetch())
                        return false;

                    apply();
                    return true;
                }
                finally
                {
                    _updateLock.Release();
                }
            }

            if (!IsCurrentFetch())
                return;

            string? videoId = shouldForceThumbFetch ? null : info.YouTubeVideoId;
            string? preferredThumbnailUrl = null;
            int retryCount = 0;
            bool titleSearchAttempted = false;

            if (string.IsNullOrEmpty(videoId))
            {
                videoId = TryExtractVideoIdFromAnyBrowserUrl();
            }

            if (string.IsNullOrEmpty(videoId))
            {
                videoId = TryExtractVideoIdFromBrowserUrl();
            }

            if (string.IsNullOrEmpty(videoId))
            {
                videoId = GetCachedVideoIdForTrack(trackDuringFetch);
            }

            if (!string.IsNullOrEmpty(videoId) && TryGetCachedMismatchVideoId(videoId!))
            {
                videoId = null;
            }

            if (!string.IsNullOrEmpty(videoId) && videoId != info.YouTubeVideoId)
            {
                string ytUrl = $"https://www.youtube.com/watch?v={videoId}";
                var urlResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(ytUrl, token);
                if (!IsCurrentFetch())
                    return;

                if (urlResult != null)
                {
                    bool videoMatchesTrack = urlResult.TitleMatches(trackDuringFetch) ||
                                            (!string.IsNullOrEmpty(urlResult.Author) &&
                                             !string.IsNullOrEmpty(artistDuringFetch) &&
                                             urlResult.Author.Contains(artistDuringFetch, StringComparison.OrdinalIgnoreCase));

                    if (!videoMatchesTrack)
                    {
                        RuntimeLog.Debug("META-YOUTUBE-API", () =>
                            $"video-mismatch: api-title='{urlResult.Title}' smtc-track='{trackDuringFetch}' videoId={videoId} -> discarding stale videoId");
                        if (!await ApplyIfCurrentAsync(() =>
                            {
                                CacheMismatchVideoId(videoId!);
                                EvictVideoIdCacheEntry(trackDuringFetch, videoId!);
                            }))
                        {
                            return;
                        }

                        videoId = null;
                    }
                    else
                    {
                        if (!await ApplyIfCurrentAsync(() =>
                        {
                            CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                            CacheVideoIdForTrack(trackDuringFetch, videoId!);

                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.IsYouTubeRunning = true;
                            SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.Save();

                            if (!string.IsNullOrEmpty(urlResult.Author) && urlResult.Author != "YouTube")
                            {
                                info.CurrentArtist = urlResult.Author;
                                _stableArtist = urlResult.Author;
                                _lastSourceConfirmedTime = DateTime.Now;
                            }

                            if (urlResult.Duration.TotalSeconds > 0)
                            {
                                _timelineSimulator.RecoveredDuration = urlResult.Duration;
                                info.Duration = urlResult.Duration;
                            }

                            if (urlResult.Source == YouTubeLookupSource.DataApi &&
                                !string.IsNullOrWhiteSpace(urlResult.ThumbnailUrl))
                            {
                                preferredThumbnailUrl = urlResult.ThumbnailUrl;
                            }
                        }))
                        {
                            return;
                        }
                    }
                }
                else if (shouldForceThumbFetch)
                {
                    RuntimeLog.Debug("META-YOUTUBE-API", () =>
                        $"validation-failed: oEmbed returned null for videoId={videoId} track='{trackDuringFetch}' -> discarding unvalidated videoId");
                    videoId = null;
                }
            }

            while (retryCount < 3 && !token.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(videoId))
                {
                    var result = await TryGetYouTubeVideoIdWithInfoAsync(trackDuringFetch, artistDuringFetch);
                    if (!IsCurrentFetch())
                        return;

                    if (result != null)
                    {
                        videoId = result.Id;

                        bool highConfidence = result.TitleMatches(trackDuringFetch) || info.Platform == MediaPlatform.YouTube;
                        if (!await ApplyIfCurrentAsync(() =>
                        {
                            if (highConfidence)
                            {
                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                info.IsYouTubeRunning = true;
                                SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                _sourceCache.Save();
                            }

                            if (!string.IsNullOrEmpty(result.Author) && result.Author != "YouTube")
                            {
                                info.CurrentArtist = result.Author;
                                _stableArtist = result.Author;
                                _lastSourceConfirmedTime = DateTime.Now;
                            }

                            if (result.Duration.TotalSeconds > 0)
                            {
                                _timelineSimulator.RecoveredDuration = result.Duration;
                                info.Duration = result.Duration;
                            }

                            if (result.Source == YouTubeLookupSource.DataApi &&
                                !string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                            {
                                preferredThumbnailUrl = result.ThumbnailUrl;
                            }
                        }))
                        {
                            return;
                        }
                    }
                }

                if (string.IsNullOrEmpty(videoId) && !titleSearchAttempted)
                {
                    titleSearchAttempted = true;
                    var searchResult = await _metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
                    if (!IsCurrentFetch())
                        return;

                    if (searchResult != null)
                    {
                        videoId = searchResult.Id;

                        if (!await ApplyIfCurrentAsync(() =>
                        {
                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.IsYouTubeRunning = true;
                            SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.Save();

                            CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                            CacheVideoIdForTrack(trackDuringFetch, videoId!);

                            if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != "YouTube")
                            {
                                info.CurrentArtist = searchResult.Author;
                                _stableArtist = searchResult.Author;
                                _lastSourceConfirmedTime = DateTime.Now;
                            }

                            if (searchResult.Duration.TotalSeconds > 0)
                            {
                                _timelineSimulator.RecoveredDuration = searchResult.Duration;
                                info.Duration = searchResult.Duration;
                            }

                            if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                            {
                                preferredThumbnailUrl = searchResult.ThumbnailUrl;
                            }
                        }))
                        {
                            return;
                        }

                        RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH", () =>
                            $"title-search-resolved track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                    }
                }

                if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                {
                    if (!IsCurrentFetch())
                        return;

                    info.YouTubeVideoId = videoId;

                    if (!string.IsNullOrEmpty(_stableArtist) &&
                        _stableArtist != "YouTube" &&
                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            await dispatcher.InvokeAsync(() =>
                            {
                                if (IsCurrentFetch())
                                {
                                    _lastTrackSignature = info.GetSignature();
                                    MediaChanged?.Invoke(this, info);
                                }
                            });
                        }
                    }

                    if (needsBetterThumb || info.Platform == MediaPlatform.YouTube)
                    {
                        BitmapImage? frameBitmap = null;
                        string thumbnailUrl;

                        if (!string.IsNullOrWhiteSpace(preferredThumbnailUrl))
                        {
                            thumbnailUrl = preferredThumbnailUrl!;
                            frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 200)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/sddefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (!IsCurrentFetch())
                                return;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl);
                            if (!IsCurrentFetch())
                                return;
                        }

                        if (frameBitmap != null && IsCurrentFetch())
                        {
                            bool isYtFetchTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                         info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                            RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                                $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' " +
                                $"thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
                            frameBitmap = CropToSquare(frameBitmap, "YouTube", forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                            _cachedThumbnail = frameBitmap;
                            _cachedThumbnailSource = MediaPlatform.YouTube.ToDisplayString();
                            info.Thumbnail = frameBitmap;

                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                            if (dispatcher != null)
                            {
                                await dispatcher.InvokeAsync(() =>
                                {
                                    if (IsCurrentFetch())
                                    {
                                        var thumbnailUpdate = info.Clone();
                                        thumbnailUpdate.IsThumbnailOnlyUpdate = true;
                                        _lastTrackSignature = thumbnailUpdate.GetSignature();
                                        MediaChanged?.Invoke(this, thumbnailUpdate);
                                    }
                                });
                            }
                            break;
                        }
                    }
                    else break;
                }

                retryCount++;
                if (retryCount < 3 && IsCurrentFetch())
                {
                    if (videoId == null)
                    {
                        const int pollIntervalMs = 200;
                        const int maxPolls = 5;
                        bool resolved = false;

                        for (int poll = 0; poll < maxPolls && !token.IsCancellationRequested; poll++)
                        {
                            await Task.Delay(pollIntervalMs, token);
                            if (!IsCurrentFetch())
                                return;

                            _windowTitleScanner.InvalidateUrlCaches();

                            string? polledId = TryExtractVideoIdFromAnyBrowserUrl();
                            if (string.IsNullOrEmpty(polledId))
                                polledId = TryExtractVideoIdFromBrowserUrl();
                            if (string.IsNullOrEmpty(polledId))
                                continue;

                            if (TryGetCachedMismatchVideoId(polledId))
                                continue;

                            string pollUrl = $"https://www.youtube.com/watch?v={polledId}";
                            var pollResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(pollUrl, token);
                            if (!IsCurrentFetch())
                                return;

                            if (pollResult == null)
                                continue;

                            bool pollMatches = pollResult.TitleMatches(trackDuringFetch) ||
                                               (!string.IsNullOrEmpty(pollResult.Author) &&
                                                !string.IsNullOrEmpty(artistDuringFetch) &&
                                                pollResult.Author.Contains(artistDuringFetch, StringComparison.OrdinalIgnoreCase));

                            if (!pollMatches)
                            {
                                CacheMismatchVideoId(polledId);
                                continue;
                            }

                            videoId = polledId;
                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.IsYouTubeRunning = true;
                            SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.Save();

                            if (!string.IsNullOrEmpty(pollResult.Author) && pollResult.Author != "YouTube")
                            {
                                info.CurrentArtist = pollResult.Author;
                                _stableArtist = pollResult.Author;
                                _lastSourceConfirmedTime = DateTime.Now;
                            }
                            if (pollResult.Duration.TotalSeconds > 0)
                            {
                                _timelineSimulator.RecoveredDuration = pollResult.Duration;
                                info.Duration = pollResult.Duration;
                            }
                            if (pollResult.Source == YouTubeLookupSource.DataApi &&
                                !string.IsNullOrWhiteSpace(pollResult.ThumbnailUrl))
                            {
                                preferredThumbnailUrl = pollResult.ThumbnailUrl;
                            }
                            resolved = true;
                            break;
                        }

                        if (!resolved)
                        {
                            videoId = null;
                            preferredThumbnailUrl = null;

                            if (!titleSearchAttempted && IsCurrentFetch())
                            {
                                titleSearchAttempted = true;
                                var searchResult = await _metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
                                if (!IsCurrentFetch())
                                    return;

                                if (searchResult != null)
                                {
                                    videoId = searchResult.Id;
                                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                    info.IsYouTubeRunning = true;
                                    SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                    _sourceCache.Save();
                                    CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId!);
                                    CacheVideoIdForTrack(trackDuringFetch, videoId!);

                                    if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != "YouTube")
                                    {
                                        info.CurrentArtist = searchResult.Author;
                                        _stableArtist = searchResult.Author;
                                        _lastSourceConfirmedTime = DateTime.Now;
                                    }
                                    if (searchResult.Duration.TotalSeconds > 0)
                                    {
                                        _timelineSimulator.RecoveredDuration = searchResult.Duration;
                                        info.Duration = searchResult.Duration;
                                    }
                                    if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                                        preferredThumbnailUrl = searchResult.ThumbnailUrl;

                                    RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH", () =>
                                        $"title-search-after-poll track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                                }
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(retryCount * 350, token);
                        if (!IsCurrentFetch())
                            return;

                        videoId = null;
                        preferredThumbnailUrl = null;

                        videoId = TryExtractVideoIdFromAnyBrowserUrl();
                        if (string.IsNullOrEmpty(videoId))
                            videoId = TryExtractVideoIdFromBrowserUrl();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-YOUTUBE-FETCH", ex.ToString());
        }
        finally
        {
            if (fetchGeneration == Volatile.Read(ref _youTubeFetchGeneration))
            {
                Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            }
        }
    }

    private async Task FetchSoundCloudThumbnailAsync(MediaInfo info, CancellationToken token, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int thumbGenAtStart, int fetchGeneration, bool requireStrongMatch)
    {
        try
        {
            string? artworkUrl = null;
            string? browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
            if (!string.IsNullOrEmpty(browserSoundCloudUrl))
            {
                artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, token);
            }

            if (string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl))
            {
                artworkUrl = await TryGetSoundCloudArtworkUrlAsync(trackDuringFetch, artistDuringFetch, requireStrongMatch, token);
            }

            if (string.IsNullOrEmpty(artworkUrl) ||
                IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl) ||
                token.IsCancellationRequested)
            {
                return;
            }

            var frameBitmap = await DownloadImageAsync(artworkUrl);
            if (frameBitmap == null ||
                IsLikelySoundCloudPlaceholderThumbnail(frameBitmap) ||
                token.IsCancellationRequested)
            {
                return;
            }

            if (!IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
            {
                return;
            }

            if (Volatile.Read(ref _thumbnailFetchGeneration) != thumbGenAtStart)
            {
                return;
            }

            if (info.Platform == MediaPlatform.YouTube ||
                IsLikelyYouTube(info) ||
                MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube)
            {
                RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                    $"soundcloud-fetch suppressed: track resolved as YouTube " +
                    $"track='{trackDuringFetch}' cachedSource='{_cachedThumbnailSource}'");
                return;
            }

            frameBitmap = CropToSquare(frameBitmap, "SoundCloud") ?? frameBitmap;
            _timelineSimulator.RecoveredThumbnail = frameBitmap;
            _cachedThumbnail = frameBitmap;
            _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
            if (info.Platform == MediaPlatform.Browser)
            {
                info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                info.IsSoundCloudRunning = true;
                SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string currentTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                if (!string.IsNullOrEmpty(currentTrackIdentity))
                {
                    _sourceCache.SetBoth(currentTrackIdentity, currentTrackOnlyIdentity, MediaPlatform.SoundCloud.ToDisplayString());
                    _sourceCache.Save();
                }
            }
            info.Thumbnail = frameBitmap;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested &&
                        Volatile.Read(ref _thumbnailFetchGeneration) == thumbGenAtStart &&
                        IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch))
                    {
                        var thumbnailUpdate = info.Clone();
                        thumbnailUpdate.IsThumbnailOnlyUpdate = true;
                        _lastTrackSignature = thumbnailUpdate.GetSignature();
                        MediaChanged?.Invoke(this, thumbnailUpdate);
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SOUNDCLOUD-FETCH", ex.ToString());
        }
        finally
        {
            if (fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration))
            {
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            }
        }
    }

    private string GetSpotifyWindowTitle()
    {
        try
        {
            var spotifyProcesses = Process.GetProcessesByName("Spotify");
            foreach (var proc in spotifyProcesses)
            {
                string title = proc.MainWindowTitle;
                if (!string.IsNullOrEmpty(title) &&
                    title != "Spotify" &&
                    title != "Spotify Premium" &&
                    title != "Spotify Free" &&
                    !title.ToLower().EndsWith("spotify"))
                {
                    return title;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SPOTIFY-TITLE", ex.ToString());
        }
        return "";
    }

    private static string BuildTrackIdentity(string track, string artist)
        => MediaHeuristics.BuildTrackIdentity(track, artist);

    private bool IsStillSamePublishedTrack(string expectedTrack, string expectedArtist, string expectedSourceAppId, string expectedSessionInstanceKey = "")
        => PublishedTrackMatcher.IsSameTrack(
            new PublishedTrackSnapshot(
                _lastPublishedTrackIdentity,
                _lastPublishedTrackOnlyIdentity,
                _lastPublishedSourceAppId,
                _lastPublishedSessionInstanceKey),
            expectedTrack, expectedArtist, expectedSourceAppId, expectedSessionInstanceKey);

    private static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
        => MediaHeuristics.BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);

    private static bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
        => MediaHeuristics.IsTrackCompatibleWithWindowTitle(track, windowTitle);

    private static bool HasReliablePlatformWindowMatch(IEnumerable<string> windowTitles, string track, string platform)
    {
        return PlatformDetector.HasReliableWindowMatch(windowTitles, track, platform);
    }

    private bool TryGetSessionSourceOverride(MediaInfo info, out string sessionOverride)
    {
        sessionOverride = string.Empty;

        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) ||
            !_sessionState.TryGetSourceOverride(key, out string? resolvedOverride) ||
            string.IsNullOrWhiteSpace(resolvedOverride))
        {
            return false;
        }

        sessionOverride = resolvedOverride;
        return true;
    }

    private void SetSessionSourceOverride(MediaInfo info, string mediaSource)
    {
        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(mediaSource))
        {
            return;
        }

        _sessionState.SetSourceOverride(key, mediaSource);
    }

    private void ClearSessionSourceOverride(string sessionInstanceKey, string sourceAppId)
    {
        string key = BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);
        if (!string.IsNullOrEmpty(key))
        {
            _sessionState.RemoveSourceOverride(key);
        }
    }

    private static string BuildSessionInstanceKey(GlobalSystemMediaTransportControlsSession session)
    {
        string sourceApp = session.SourceAppUserModelId ?? "";

        int instanceHash = RuntimeHelpers.GetHashCode(session);
        return $"{sourceApp}|{instanceHash}";
    }

    private static bool IsSessionPlayingStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private bool IsSessionStillPresent(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var sessions = _sessionManager?.GetSessions();
            if (sessions == null) return false;

            foreach (var s in sessions)
            {
                if (ReferenceEquals(s, session))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SESSION-PRESENT", ex.ToString());
            return true;
        }

        return false;
    }

    private static bool IsBrowserSourceApp(string sourceAppId)
    {
        return PlatformDetector.IsBrowserApp(sourceAppId);
    }

    private static bool IsIgnoredSourceApp(string sourceAppId)
        => MediaHeuristics.IsIgnoredSourceApp(sourceAppId);

    private static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        return PlatformDetector.DetectPlatformHint(windowTitles);
    }

    private static string NormalizeForLooseMatch(string value)
    {
        return PlatformDetector.NormalizeForLooseMatch(value);
    }

    private static bool IsLikelySoundCloudPlaceholderThumbnail(BitmapImage? thumbnail)
        => ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(thumbnail);

    private static bool IsLikelySoundCloudArtworkCandidate(BitmapImage? thumbnail)
        => ThumbnailHeuristics.IsLikelyArtworkCandidate(thumbnail);

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
        => MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url);

    private static bool HasLowEntropyMonochromeProfile(BitmapImage thumbnail)
        => ThumbnailHeuristics.HasLowEntropyMonochromeProfile(thumbnail);

    private async Task<bool> TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return false;

        try
        {
            var (session, spotifyGroundTruth) = await ResolveActiveSessionAsync(forceRefresh);
            if (session == null) return false;

            SwitchActiveDisplaySessionIfNeeded(session);

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true;
            info.SourceAppId = sessionSourceApp;
            info.SessionInstanceKey = BuildSessionInstanceKey(session);

            ApplyMediaSourceFromAppId(info, sessionSourceApp);

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            string sessionTitle = mediaProperties?.Title ?? "";
            string sessionArtist = mediaProperties?.Artist ?? "";
            string lowerAlbum = (mediaProperties?.AlbumTitle ?? "").ToLower();

            RefineMediaSourceFromMetadata(info, sessionTitle.ToLower(), sessionArtist.ToLower(), lowerAlbum);

            var pbInfo = session.GetPlaybackInfo();
            info.IsPlaying = pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.PlaybackRate = pbInfo?.PlaybackRate ?? 1.0;
            info.IsAnyMediaPlaying = true;
            info.CurrentTrack = sessionTitle;
            info.CurrentArtist = sessionArtist;

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
                ApplySpotifyGroundTruthCorrection(info, spotifyGroundTruth, ref mediaProperties);

            await ApplyTimelinePropertiesAsync(info, session, forceRefresh);
            ApplyFinalPlaybackInfo(info, session);

            var adTransition = EvaluateLikelyYouTubeAdTransition(
                info,
                sessionSourceApp,
                windowTitleFactory);
            if (adTransition.ShouldHold)
            {
                return true;
            }

            StabilizeArtist(info);

            string currentTrackOnlyIdentityForThumb = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool trackChangedForThisPass = InvalidateThumbnailStateIfTrackChanged(currentTrackOnlyIdentityForThumb);

            ResolveBrowserMediaSource(
                info,
                sessionSourceApp,
                windowTitleFactory,
                adTransition.WasTransitionActive);

            await ApplySessionThumbnailAsync(info, mediaProperties, trackChangedForThisPass, forceRefresh);

            _lastTrackSignature = info.GetSignature();
            _lastThumbTrackIdentity = currentTrackOnlyIdentityForThumb;
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
                HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "youtube");
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
            RuntimeLog.Debug("MEDIA-AD-TRANSITION", () =>
                $"Quarantining browser ad candidate track='{info.CurrentTrack}' " +
                $"artist='{info.CurrentArtist}' duration={info.Duration.TotalSeconds:F1}s " +
                $"session='{info.SessionInstanceKey}'");
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
            RuntimeLog.Debug("MEDIA-AD-TRANSITION", () =>
                $"Quarantining junk browser metadata for session='{info.SessionInstanceKey}'");
        }

        return decision;
    }

    private void SwitchActiveDisplaySessionIfNeeded(GlobalSystemMediaTransportControlsSession session)
    {
        if (_activeDisplaySession == session) return;

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

        if (_activeDisplaySession != null)
        {
            try
            {
                _activeDisplaySession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _activeDisplaySession.PlaybackInfoChanged -= OnPlaybackChanged;
                _activeDisplaySession.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-UNSUB", ex.ToString());
            }
        }

        _activeDisplaySession = session;

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

        _lastTrackSignature = "";
        if (!preserveSameBrowserMediaCache)
        {
            _lastThumbTrackIdentity = "";
            _cachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            _thumbCts?.Cancel();
            _thumbCts = null;
            _youTubeFetchIdentity = "";
            Interlocked.Increment(ref _youTubeFetchGeneration);
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            _soundCloudFetchIdentity = "";
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            Interlocked.Increment(ref _thumbnailFetchGeneration);
        }
    }

    private void ApplyMediaSourceFromAppId(MediaInfo info, string sessionSourceApp)
        => MediaSourceClassifier.ApplyFromAppId(info, sessionSourceApp);

    private void RefineMediaSourceFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
        => MediaSourceClassifier.RefineFromMetadata(info, lowerTitle, lowerArtist, lowerAlbum);

    private bool TryHandleJunkSessionTitle(MediaInfo info, string sessionTitle, string sessionArtist)
        => MediaSourceClassifier.TryHandleJunkTitle(info, sessionTitle, sessionArtist);

    private void StabilizeArtist(MediaInfo info)
    {
        var r = MediaTimingDecisions.EvaluateArtistStabilization(
            info.CurrentArtist, _stableArtist, _lastSourceConfirmedTime, DateTime.Now);
        info.CurrentArtist = r.artist;
        _stableArtist = r.stableArtist;
    }

    private void ApplySpotifyGroundTruthCorrection(MediaInfo info, string? spotifyGroundTruth, ref GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties)
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
            bool needsCorrection = false;
            string correctedTrack = "";
            string correctedArtist = "";

            if (info.CurrentArtist.Contains(" - ", StringComparison.Ordinal))
            {
                string artistPrefix = info.CurrentArtist + " - ";
                bool artistMatchesWindowTitle = spotifyGroundTruth.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase);

                if (!artistMatchesWindowTitle)
                {
                    int firstSep = spotifyGroundTruth.IndexOf(" - ", StringComparison.Ordinal);
                    if (firstSep > 0)
                    {
                        string wtArtistFirst = spotifyGroundTruth.Substring(0, firstSep).Trim();
                        string wtTrackFirst = spotifyGroundTruth.Substring(firstSep + 3).Trim();

                        if (!wtArtistFirst.Contains(" - ", StringComparison.Ordinal) &&
                            !string.IsNullOrEmpty(wtTrackFirst))
                        {
                            correctedArtist = wtArtistFirst;
                            correctedTrack = wtTrackFirst;
                            needsCorrection = true;
                        }
                    }
                }
            }

            if (!needsCorrection && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                string artistPrefix = info.CurrentArtist + " - ";
                if (spotifyGroundTruth.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string realTrack = spotifyGroundTruth.Substring(artistPrefix.Length).Trim();
                    if (!string.IsNullOrEmpty(realTrack) &&
                        !string.Equals(realTrack, info.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
                        realTrack.Contains(info.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
                        realTrack.Length > info.CurrentTrack.Length + 3)
                    {
                        correctedTrack = realTrack;
                        correctedArtist = info.CurrentArtist;
                        needsCorrection = true;
                    }
                }
            }

            if (needsCorrection)
            {
                info.CurrentTrack = correctedTrack;
                info.CurrentArtist = correctedArtist;
                isStaleSMTC = true;
            }
        }

        if (isStaleSMTC)
        {
            mediaProperties = null;
        }
    }

    private bool InvalidateThumbnailStateIfTrackChanged(string currentTrackOnlyIdentityForThumb)
    {
        bool trackChangedForThisPass = !string.Equals(currentTrackOnlyIdentityForThumb, _lastThumbTrackIdentity, StringComparison.Ordinal);
        if (trackChangedForThisPass)
        {
            _cachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            _thumbCts?.Cancel();
            _thumbCts = null;
            _youTubeFetchIdentity = "";
            Interlocked.Increment(ref _youTubeFetchGeneration);
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            _soundCloudFetchIdentity = "";
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            Interlocked.Increment(ref _thumbnailFetchGeneration);
            RuntimeLog.Debug("MEDIA-THUMB-INVALIDATE", () =>
                $"Track changed: old='{_lastThumbTrackIdentity}' new='{currentTrackOnlyIdentityForThumb}' — cleared all thumbnail state");
        }
        return trackChangedForThisPass;
    }

    private void ApplyFinalPlaybackInfo(MediaInfo info, GlobalSystemMediaTransportControlsSession? session)
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

    private void ResolveBrowserMediaSource(
        MediaInfo info,
        string sessionSourceApp,
        Func<List<string>> windowTitleFactory,
        bool isCompletingAdTransition)
    {
        if (info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource))
        {

            bool isSameSession = !string.IsNullOrEmpty(info.SessionInstanceKey) &&
                                 string.Equals(info.SessionInstanceKey, _lastPublishedSessionInstanceKey, StringComparison.Ordinal);
            string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
            bool sameTrackAsStable = !string.IsNullOrEmpty(currentTrackIdentity) &&
                                     currentTrackIdentity == _stableSourceTrackIdentity;
            List<string>? hintedWindowTitles = null;
            string hintedSource = "";
            bool sourceFromBrowserOverride = false;
            bool sourceFromTrackCache = false;

            string browserPlatformHint = "";
            if (!string.IsNullOrEmpty(sessionSourceApp) && IsBrowserSourceApp(sessionSourceApp))
            {
                hintedWindowTitles ??= windowTitleFactory();
                bool hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "youtube");
                browserPlatformHint = hasReliableYouTubeWindowMatch
                    ? "YouTube"
                    : DetectPlatformHint(hintedWindowTitles);
            }

            if (BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
                isBrowserSession: IsBrowserSourceApp(sessionSourceApp),
                currentBrowserPlatformHint: browserPlatformHint,
                lastSource: _lastSource,
                stableSource: _stableSource,
                isCompletingAdTransition))
            {
                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                info.IsYouTubeRunning = true;
            }

            if (!string.IsNullOrEmpty(sessionSourceApp) &&
                IsBrowserSourceApp(sessionSourceApp) &&
                TryGetSessionSourceOverride(info, out var sessionOverride) &&
                MediaPlatformExtensions.ParsePlatform(sessionOverride) == MediaPlatform.SoundCloud &&
                !string.Equals(browserPlatformHint, "YouTube", StringComparison.OrdinalIgnoreCase))
            {
                hintedWindowTitles ??= windowTitleFactory();
                if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "soundcloud"))
                {
                    info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                    info.IsSoundCloudRunning = true;
                    sourceFromBrowserOverride = true;
                }
            }

            if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
                !string.IsNullOrEmpty(info.CurrentTrack))
            {
                bool hasCachedSource = _sourceCache.TryGet(currentTrackIdentity, out var cachedSource) ||
                                       _sourceCache.TryGet(trackOnlyIdentity, out cachedSource);

                if (hasCachedSource &&
                    string.Equals(cachedSource, "SoundCloud", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(browserPlatformHint, "YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    hintedWindowTitles ??= windowTitleFactory();
                    if (HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, "soundcloud"))
                    {
                        info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                        info.IsSoundCloudRunning = true;
                        sourceFromTrackCache = true;
                    }
                }
            }

            if (isSameSession && !string.IsNullOrEmpty(_stableSource) && MediaPlatformExtensions.ParsePlatform(_stableSource) != MediaPlatform.Browser && sameTrackAsStable)
            {
                hintedWindowTitles = windowTitleFactory();
                hintedSource = DetectPlatformHint(hintedWindowTitles);
            }

            if (isSameSession && !string.IsNullOrEmpty(_stableSource) && MediaPlatformExtensions.ParsePlatform(_stableSource) != MediaPlatform.Browser && sameTrackAsStable)
            {
                bool canKeepStable = string.IsNullOrEmpty(hintedSource) ||
                                     string.Equals(hintedSource, _stableSource, StringComparison.OrdinalIgnoreCase);
                if (canKeepStable)
                {
                    info.MediaSource = _stableSource;
                    if (MediaPlatformExtensions.ParsePlatform(_stableSource) == MediaPlatform.YouTube) info.IsYouTubeRunning = true;
                }
            }

            if (info.Platform == MediaPlatform.Browser ||
                string.IsNullOrEmpty(info.MediaSource) ||
                sourceFromBrowserOverride ||
                sourceFromTrackCache)
            {
                var windowTitles = hintedWindowTitles ?? windowTitleFactory();
                string trackTitleLower = info.CurrentTrack.ToLower();
                string trackTitleNormalized = NormalizeForLooseMatch(trackTitleLower);
                bool hasTrack = !string.IsNullOrEmpty(trackTitleLower) && trackTitleLower != "browser" && trackTitleLower != "now playing";
                bool hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(windowTitles, info.CurrentTrack, "youtube");
                string platformHint = hasReliableYouTubeWindowMatch
                    ? "YouTube"
                    : DetectPlatformHint(windowTitles);
                if ((!hasTrack && string.Equals(platformHint, "YouTube", StringComparison.OrdinalIgnoreCase)) ||
                    hasReliableYouTubeWindowMatch)
                {
                    info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                    info.IsYouTubeRunning = true;
                }

                MediaSourceClassifier.DetectFromWindowTitles(info, windowTitles, trackTitleLower, trackTitleNormalized, hasTrack);
            }
        }

        if ((info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource)) &&
            !string.IsNullOrEmpty(sessionSourceApp) &&
            IsBrowserSourceApp(sessionSourceApp) &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            !IsLikelyYouTube(info) &&
            !IsKnownNonSpotifyTrack(info) &&
            _windowTitleScanner.IsSpotifyWebPlayerOpen())
        {
            info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
            info.IsSpotifyPlaying = true;
            info.IsSpotifyRunning = true;
        }

        if (!string.IsNullOrEmpty(sessionSourceApp) &&
            IsBrowserSourceApp(sessionSourceApp) &&
            !string.IsNullOrEmpty(info.MediaSource) &&
            info.Platform != MediaPlatform.Browser)
        {
            if (info.Platform == MediaPlatform.YouTube)
            {
                SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
            }
            else if (!TryGetSessionSourceOverride(info, out var existingOverride) ||
                     MediaPlatformExtensions.ParsePlatform(existingOverride) != MediaPlatform.YouTube)
            {
                SetSessionSourceOverride(info, info.MediaSource);
            }
        }

        if (info.Platform != MediaPlatform.Browser && !string.IsNullOrEmpty(info.MediaSource))
        {
            _stableSource = info.MediaSource;
            _lastSourceConfirmedTime = DateTime.Now;
            _cachedSource = info.MediaSource;
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
                    if (info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.SoundCloud)
                    {
                        _sourceCache.Save();
                    }
                }
            }
        }
        else
        {
            _cachedSource = MediaPlatform.Browser.ToDisplayString();
        }
    }

    private async Task ApplySessionThumbnailAsync(MediaInfo info, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties, bool trackChangedForThisPass, bool forceRefresh)
    {
        bool isYouTubeLikeSource = info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
        bool browserSessionChanged =
            IsBrowserSourceApp(info.SourceAppId) &&
            !string.IsNullOrEmpty(_lastPublishedSessionInstanceKey) &&
            !string.IsNullOrEmpty(info.SessionInstanceKey) &&
            !string.Equals(
                _lastPublishedSessionInstanceKey,
                info.SessionInstanceKey,
                StringComparison.Ordinal);
        bool hasVerifiedYouTubeThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube;
        bool hasVerifiedSoundCloudThumbGlobal = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.SoundCloud;
        if (!trackChangedForThisPass && _cachedThumbnail != null &&
            (hasVerifiedYouTubeThumb || hasVerifiedSoundCloudThumbGlobal))
        {
            info.Thumbnail = _cachedThumbnail;
        }
        else if (!trackChangedForThisPass && _cachedThumbnail != null)
        {
            info.Thumbnail = _cachedThumbnail;
        }
        else
        {
            var thumbnail = mediaProperties?.Thumbnail;
            if (thumbnail != null)
            {
                try
                {
                    using var stream = await thumbnail.OpenReadAsync();
                    if (stream != null && stream.Size > 0)
                    {
                        var newBitmap = await ConvertToWpfBitmapAsync(stream);
                        if (newBitmap != null)
                        {
                            bool isSoundCloudSource = info.Platform == MediaPlatform.SoundCloud;
                            bool hasVerifiedSoundCloudThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.SoundCloud;
                            bool likelySoundCloudArtwork = IsLikelySoundCloudArtworkCandidate(newBitmap);

                            var thumbDecision = ThumbnailHeuristics.DecideSmtcThumbnail(new ThumbnailHeuristics.SmtcThumbnailInputs
                            {
                                IsYouTubeLikeSource = isYouTubeLikeSource,
                                IsSoundCloudSource = isSoundCloudSource,
                                IsBrowserOrYouTubePlatform = info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.Browser,
                                IsBrowserSession = info.Platform == MediaPlatform.Browser || IsBrowserSourceApp(info.SourceAppId),
                                TrackChanged = trackChangedForThisPass,
                                BrowserSessionChanged = browserSessionChanged,
                                HasVerifiedYouTubeThumb = hasVerifiedYouTubeThumb,
                                HasVerifiedSoundCloudThumb = hasVerifiedSoundCloudThumb,
                                LikelySoundCloudArtwork = likelySoundCloudArtwork,
                                RecentTrackChange = (DateTime.Now - _lastMetadataChangeTime).TotalSeconds < 4.0,
                                CachedThumbnailIsNull = _cachedThumbnail == null,
                                PixelWidth = newBitmap.PixelWidth,
                                PixelHeight = newBitmap.PixelHeight,
                            });

                            if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Reject)
                            {
                                RuntimeLog.Debug("MEDIA-THUMB-STALE", () =>
                                    $"Rejecting stale SMTC thumbnail on track change: " +
                                    $"track='{info.CurrentTrack}' source='{info.MediaSource}' " +
                                    $"thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2}");
                                info.Thumbnail = null;
                            }
                            else if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Accept)
                            {
                                bool isSmtcTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                          info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                                RuntimeLog.Debug("MEDIA-THUMB-CROP", () =>
                                    $"path=smtc-update track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' " +
                                    $"thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2} " +
                                    $"isSmtcTopicChannel={isSmtcTopicChannel}");
                                newBitmap = CropToSquare(newBitmap, info.MediaSource, forceCenterCrop: isSmtcTopicChannel) ?? newBitmap;
                                _cachedThumbnail = newBitmap;
                                if (info.Platform == MediaPlatform.SoundCloud)
                                {
                                    if (likelySoundCloudArtwork)
                                    {
                                        _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                                    }
                                }
                                else
                                {
                                    _cachedThumbnailSource = info.MediaSource ?? "";
                                }
                                info.Thumbnail = _cachedThumbnail;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-THUMB-PROCESS", ex.ToString());
                }
            }
        }
    }

    private async Task ApplyTimelinePropertiesAsync(MediaInfo info, GlobalSystemMediaTransportControlsSession? session, bool forceRefresh)
    {
        try
        {
            var timeline = session?.GetTimelineProperties();
            if (timeline != null)
            {

                bool isInitialOrBigChange = forceRefresh ||
                                           _lastTrackSignature == "" ||
                                           (DateTime.Now - _lastMetadataChangeTime).TotalSeconds < 4.0;

                if (isInitialOrBigChange)
                {

                    await Task.Delay(120);
                    timeline = session?.GetTimelineProperties() ?? timeline;
                }

                var duration = timeline.EndTime - timeline.StartTime;
                if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;

                bool isNewTrack = !string.IsNullOrEmpty(info.CurrentTrack) &&
                    !string.IsNullOrEmpty(_lastTrackName) &&
                    info.CurrentTrack != _lastTrackName;

                bool isBrowserTimelineTrack = IsBrowserSourceApp(info.SourceAppId) ||
                                              info.Platform == MediaPlatform.YouTube ||
                                              info.Platform == MediaPlatform.SoundCloud ||
                                              info.Platform == MediaPlatform.Browser;

                var solved = TimelinePositionSolver.Solve(new TimelineSolveInputs
                {
                    StartTime = timeline.StartTime,
                    EndTime = timeline.EndTime,
                    MaxSeekTime = timeline.MaxSeekTime,
                    Position = timeline.Position,
                    LastUpdatedUtc = timeline.LastUpdatedTime.ToUniversalTime(),
                    IsInitialOrBigChange = isInitialOrBigChange,
                    IsNewTrack = isNewTrack,
                    IsBrowserTimelineTrack = isBrowserTimelineTrack,
                    IsPlaying = info.IsPlaying,
                    PlaybackRate = info.PlaybackRate,
                    NowUtc = DateTimeOffset.UtcNow,
                });

                info.Position = solved.Position;
                info.Duration = solved.Duration;
                info.LastUpdated = solved.LastUpdatedUtc;
                if (solved.IsIndeterminate.HasValue)
                {
                    info.IsIndeterminate = solved.IsIndeterminate.Value;
                }
            }
            else
            {
                info.IsIndeterminate = true;
            }
        }
        catch { info.IsIndeterminate = true; }
    }

    private async Task<(GlobalSystemMediaTransportControlsSession? session, string? spotifyGroundTruth)> ResolveActiveSessionAsync(bool forceRefresh)
    {
        if (_sessionManager == null) return (null, null);
        GlobalSystemMediaTransportControlsSession? session = null;
        string? spotifyGroundTruth = null;
        string osCurrentId = _sessionManager.GetCurrentSession()?.SourceAppUserModelId ?? "";

        if (_activeDisplaySession != null && !forceRefresh && !IsSessionStillPresent(_activeDisplaySession))
        {
            RuntimeLog.Debug("MEDIA-SESSION", "Active display session no longer listed (app closed) — forcing rescan.");
            _activeDisplaySession = null;
        }

        if (_activeDisplaySession != null && !forceRefresh)
        {
            try
            {
                if (IsIgnoredSourceApp(_activeDisplaySession.SourceAppUserModelId ?? ""))
                {
                    _activeDisplaySession = null;
                }
                else if (string.IsNullOrEmpty(osCurrentId) || osCurrentId == _activeDisplaySession.SourceAppUserModelId)
                {
                    var playback = _activeDisplaySession.GetPlaybackInfo();
                    if (playback != null && playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        var props = await _activeDisplaySession.TryGetMediaPropertiesAsync();
                        if (props != null && !string.IsNullOrEmpty(props.Title))
                        {

                            if (_activeDisplaySession.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                spotifyGroundTruth = GetSpotifyWindowTitle();
                                if (string.IsNullOrEmpty(spotifyGroundTruth) || !SpotifyTitleContainsTrack(spotifyGroundTruth, props.Title))
                                {

                                    session = null;
                                }
                                else
                                {
                                    session = _activeDisplaySession;
                                }
                            }
                            else
                            {
                                session = _activeDisplaySession;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-DETECT-ACTIVE", ex.ToString());
            }
        }

        if (session == null)
        {
            spotifyGroundTruth ??= GetSpotifyWindowTitle();
            GlobalSystemMediaTransportControlsSession? bestSession = null;
            int bestScore = -1;
            bool hasAnyActiveSession = false;
            GlobalSystemMediaTransportControlsSession? fallbackActiveSession = null;

            try
            {
                var sessions = _sessionManager.GetSessions();

                foreach (var s in sessions)
                {
                    try
                    {
                        var sourceApp = s.SourceAppUserModelId ?? "";
                        if (IsIgnoredSourceApp(sourceApp)) continue;

                        var status = s.GetPlaybackInfo().PlaybackStatus;
                        bool isActive = IsSessionPlayingStatus(status);
                        if (!isActive) continue;

                        hasAnyActiveSession = true;
                        if (fallbackActiveSession == null)
                        {
                            fallbackActiveSession = s;
                        }

                        if (!string.IsNullOrEmpty(osCurrentId) &&
                            string.Equals(sourceApp, osCurrentId, StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackActiveSession = s;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-ACTIVE-SCAN", ex.ToString());
                    }
                }

                foreach (var s in sessions)
                {
                    try
                    {
                        var sourceApp = s.SourceAppUserModelId ?? "";
                        if (IsIgnoredSourceApp(sourceApp)) continue;

                        string sessionInstanceKey = BuildSessionInstanceKey(s);
                        var playbackInfo = s.GetPlaybackInfo();
                        var status = playbackInfo.PlaybackStatus;
                        var nowUtc = DateTime.UtcNow;

                        bool isActive = IsSessionPlayingStatus(status);
                        bool isPrevActive = ReferenceEquals(_activeDisplaySession, s);
                        bool wasPlaying = _sessionState.GetPlayingState(sessionInstanceKey);

                        if (isActive && !wasPlaying)
                        {
                            _sessionState.SetPlayStartTime(sessionInstanceKey, nowUtc);
                            _latestPlayingSessionKey = sessionInstanceKey;
                            _latestPlayingSessionStartUtc = nowUtc;

                            ClearSessionSourceOverride(sessionInstanceKey, sourceApp);

                            _lastTrackSignature = "";
                        }

                        _sessionState.SetPlayingState(sessionInstanceKey, isActive);

                        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                        for (int i = 0; i < 2; i++)
                        {
                            props = await s.TryGetMediaPropertiesAsync();
                            if (props != null && !string.IsNullOrEmpty(props.Title)) break;
                            if (i == 0) await Task.Delay(25);
                        }

                        bool hasTitle = props != null && !string.IsNullOrEmpty(props.Title);
                        bool hasArtist = props != null && !string.IsNullOrEmpty(props.Artist);

                        bool isSpotify = sourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
                        bool isMusic = sourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase);
                        bool isYouTube = sourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase);
                        bool isBrowser = sourceApp.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                        sourceApp.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                                        sourceApp.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                                        sourceApp.Contains("Firefox", StringComparison.OrdinalIgnoreCase);

                        bool isOsCurrent = osCurrentId == sourceApp;

                        bool isDedicatedMusicAppPlaying = false;
                        if (isOsCurrent && (isBrowser || isYouTube))
                        {
                            foreach (var otherSession in sessions)
                            {
                                try
                                {
                                    var otherId = otherSession.SourceAppUserModelId ?? "";
                                    if (otherId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                                        otherId.Contains("Music", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (IsSessionPlayingStatus(otherSession.GetPlaybackInfo().PlaybackStatus))
                                        {
                                            isDedicatedMusicAppPlaying = true;
                                            break;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        if (isActive)
                        {
                            _sessionState.SetLastPlayingTime(sourceApp, DateTime.Now);
                        }

                        double? playStartAge = null;
                        if (isActive && _sessionState.TryGetPlayStartTime(sessionInstanceKey, out var playStartUtc))
                        {
                            playStartAge = (nowUtc - playStartUtc).TotalSeconds;
                        }

                        double? latestPlayingAge = null;
                        if (!string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                            sessionInstanceKey == _latestPlayingSessionKey)
                        {
                            latestPlayingAge = (nowUtc - _latestPlayingSessionStartUtc).TotalSeconds;
                        }

                        double? lastPlayingIdle = null;
                        if (_sessionState.TryGetLastPlayingTime(sourceApp, out var lastPlaying))
                        {
                            lastPlayingIdle = (DateTime.Now - lastPlaying).TotalSeconds;
                        }

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

                        bool artistIsNonGeneric = hasArtist && props!.Artist != "YouTube" && props.Artist != "Browser";

                        int score = SessionScorer.Score(new SessionScoreInputs
                        {
                            HasTitle = hasTitle,
                            HasArtist = hasArtist,
                            HasThumbnail = props?.Thumbnail != null,
                            ArtistIsNonGeneric = artistIsNonGeneric,
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
                            : (isActive || isPrevActive || osCurrentId == sourceApp);

                        if (score > bestScore && eligible)
                        {
                            bestScore = score;
                            bestSession = s;
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
                    if (!IsSessionPlayingStatus(bestSession.GetPlaybackInfo().PlaybackStatus))
                    {
                        bestSession = fallbackActiveSession ?? bestSession;
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-BESTCHECK", ex.ToString());
                }
            }

            if (bestSession != null && _activeDisplaySession != null && !ReferenceEquals(bestSession, _activeDisplaySession))
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
                }
            }

            if (bestSession != null && _activeDisplaySession != null && !ReferenceEquals(bestSession, _activeDisplaySession))
            {
                string bestId = bestSession.SourceAppUserModelId ?? "";
                string bestSessionKey = BuildSessionInstanceKey(bestSession);
                if (bestId != _pendingSessionAppId)
                {
                    _pendingSessionAppId = bestId;
                    _pendingSessionStartTime = DateTime.Now;
                }

                string currentId = _activeDisplaySession.SourceAppUserModelId ?? "";
                bool currentIsPremium = currentId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                                      currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

                bool isOsCurrent = !string.IsNullOrEmpty(osCurrentId) && bestId == osCurrentId;
                bool isRecentLatestPlayback = !string.IsNullOrEmpty(_latestPlayingSessionKey) &&
                                              bestSessionKey == _latestPlayingSessionKey &&
                                              (DateTime.UtcNow - _latestPlayingSessionStartUtc).TotalSeconds < 8;
                bool currentStillPlaying = false;
                try
                {
                    currentStillPlaying = IsSessionPlayingStatus(_activeDisplaySession.GetPlaybackInfo().PlaybackStatus);
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
                        var timelineAge = (DateTimeOffset.UtcNow - bestTimeline.LastUpdatedTime).TotalSeconds;
                        hasFreshTimeline = timelineAge >= 0 && timelineAge < 2.0;

                        var currentTimeline = _activeDisplaySession.GetTimelineProperties();
                        if (currentTimeline != null && hasFreshTimeline)
                        {
                            var currentTimelineAge = (DateTimeOffset.UtcNow - currentTimeline.LastUpdatedTime).TotalSeconds;

                            if (currentTimelineAge - timelineAge > 3.0)
                            {
                                RuntimeLog.Debug("MEDIA-SESSION", () => $"Switching session: fresh timeline New={timelineAge:F1}s vs Current={currentTimelineAge:F1}s");
                                hasFreshTimeline = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-DETECT-FRESH-TIMELINE", ex.ToString());
                }

                if (SessionSwitchArbiter.ShouldHoldCurrentSession(new SessionSwitchInputs
                {
                    CurrentIsPremium = currentIsPremium,
                    BestIsOsCurrent = isOsCurrent,
                    BestIsRecentLatestPlayback = isRecentLatestPlayback,
                    CurrentStillPlaying = currentStillPlaying,
                    BestHasFreshTimeline = hasFreshTimeline,
                    PendingElapsedSeconds = (DateTime.Now - _pendingSessionStartTime).TotalSeconds,
                }))
                {
                    bestSession = _activeDisplaySession;
                }
            }
            else
            {
                _pendingSessionAppId = "";
            }

            if (bestSession != null)
            {
                if (!ReferenceEquals(_activeDisplaySession, bestSession))
                {
                    _lastSessionSwitchTime = DateTime.Now;
                }
                session = bestSession;
            }
            else
            {
                if (hasAnyActiveSession && fallbackActiveSession != null)
                {
                    session = fallbackActiveSession;
                }
                else
                {
                    bool currentStillPlaying = false;
                    try
                    {
                        if (_activeDisplaySession != null)
                        {
                            currentStillPlaying = IsSessionPlayingStatus(_activeDisplaySession.GetPlaybackInfo().PlaybackStatus);
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Error("MEDIA-DETECT-FALLBACK-STATUS", ex.ToString());
                    }

                    session = _sessionManager.GetCurrentSession();
                }
            }
        }

        return (session, spotifyGroundTruth);
    }

    private Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
        => _metadataLookup.TryGetYouTubeVideoIdWithInfoAsync(title, artist, ct);

    private async Task<string?> TryGetYouTubeVideoIdAsync(string title, CancellationToken ct = default)
    {
        var res = await TryGetYouTubeVideoIdWithInfoAsync(title, "", ct);
        return res?.Id;
    }

    private bool TryGetCachedMismatchVideoId(string videoId)
        => _videoIds.IsMismatch(videoId);

    private void CacheMismatchVideoId(string videoId)
        => _videoIds.MarkMismatch(videoId);

    private void ClearMismatchCache()
        => _videoIds.ClearMismatches();

    private Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkUrlAsync(title, artist, requireStrongMatch, ct);

    private Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
        => _metadataLookup.TryGetSoundCloudArtworkFromUrlAsync(soundCloudUrl, ct);

    private Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
        => _artworkService.DownloadImageAsync(url, ct);

    private BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false)
        => _artworkService.CropToSquare(source, mediaSource, forceCenterCrop);

    private Task<BitmapImage?> ConvertToWpfBitmapAsync(global::Windows.Storage.Streams.IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
        => _artworkService.ConvertToWpfBitmapAsync(stream, ct);

    private List<string> GetAllWindowTitles()
        => _windowTitleScanner.GetAllWindowTitles(_timelineSimulator.IsThrottled);

    private string? TryExtractVideoIdFromBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetBrowserUrl();
            if (string.IsNullOrEmpty(url)) return null;

            var match = System.Text.RegularExpressions.Regex.Match(url,
                @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");

            if (match.Success)
            {
                string videoId = match.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted video ID from browser URL: {videoId}");
                return videoId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    private string? TryExtractVideoIdFromAnyBrowserUrl()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(url)) return null;

            var match = System.Text.RegularExpressions.Regex.Match(url,
                @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");

            if (match.Success)
            {
                string videoId = match.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted video ID from background browser: {videoId}");
                return videoId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaDetection] Background browser URL extraction failed: {ex.Message}");
        }

        return null;
    }

    private string? TryExtractSoundCloudUrlFromAnyBrowser()
    {
        try
        {
            string? url = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(url)) return null;

            if (url.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(url,
                    @"soundcloud\.com/([^/\s?#]+)/([^/\s?#]+)");
                if (match.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaDetection] Extracted SoundCloud URL from browser: {url}");
                    return url;
                }
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

        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.SourceAppId))
        {
            if (TryGetSessionSourceOverride(info, out var sOver) && MediaPlatformExtensions.ParsePlatform(sOver) == MediaPlatform.YouTube)
            {
                if (string.IsNullOrEmpty(info.CurrentTrack))
                {
                    return true;
                }

                string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                bool hasYouTubeTrackCache = _sourceCache.HasSource(trackIdentity, trackOnlyIdentity, "YouTube");
                if (hasYouTubeTrackCache)
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(info.CurrentArtist) &&
            info.CurrentArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            return true;

        if (info.Platform == MediaPlatform.Browser && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            try
            {
                var titles = GetAllWindowTitles();
                if (titles != null && titles.Count > 0)
                {
                    if (PlatformDetector.HasReliableWindowMatch(titles, info.CurrentTrack, "youtube"))
                    {
                        return true;
                    }

                    if (MediaPlatformExtensions.ParsePlatform(DetectPlatformHint(titles)) == MediaPlatform.YouTube)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private bool IsKnownNonSpotifyTrack(MediaInfo info)
    {
        if (string.IsNullOrEmpty(info.CurrentTrack)) return false;

        string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
        string trackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");

        if (_sourceCache.TryGet(trackIdentity, out var cachedSource) ||
            _sourceCache.TryGet(trackOnlyIdentity, out cachedSource))
        {
            if (!string.IsNullOrEmpty(cachedSource) &&
                !string.Equals(cachedSource, "Spotify", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cachedSource, "Browser", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
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
        return MediaTimingDecisions.ShouldPreserveSoundCloud(
            info.MediaSource, info.CurrentTrack, info.CurrentArtist, info.SourceAppId, info.SessionInstanceKey,
            _lastSource, _lastPublishedSessionInstanceKey, _lastMetadataChangeTime, DateTime.Now,
            hasOverride, sessionOverride);
    }

    private void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        _timelineSimulator.ApplySimulatedTimeline(info, atEndStuck);
    }

    private void ParseSpotifyTitle(string title, MediaInfo info)
    {
        var (artist, track) = PlatformDetector.ParseSpotifyTitle(title);
        info.CurrentArtist = artist;
        info.CurrentTrack = track;
    }

    private static bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
        => MediaHeuristics.SpotifyTitleContainsTrack(spotifyWindowTitle, smtcTrack);

    private static string NormalizeTrackForComparison(string text)
        => MediaHeuristics.NormalizeTrackForComparison(text);

    private static string ExtractCoreTrackName(string track)
        => MediaHeuristics.ExtractCoreTrackName(track);

    private string ExtractVideoTitle(string windowTitle, string platform)
    {
        return PlatformDetector.ExtractTitleFromWindow(windowTitle, platform);
    }

    public bool TryGetCurrentSessionVolume(out float volume, out bool isMuted)
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TryGetVolume(sourceAppId, out volume, out isMuted);
    }

    public bool TrySetCurrentSessionVolume(float volume)
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TrySetVolume(sourceAppId, volume);
    }

    public bool TryToggleCurrentSessionMute()
    {
        string sourceAppId = GetActiveSourceAppId();
        return _volumeService.TryToggleMute(sourceAppId);
    }

    public void InvalidateVolumeSessionCache()
    {
        _volumeService.InvalidateVolumeSessionCache();
    }

    private string GetActiveSourceAppId()
    {
        try
        {
            return _activeDisplaySession?.SourceAppUserModelId
                ?? _currentSession?.SourceAppUserModelId
                ?? _sessionManager?.GetCurrentSession()?.SourceAppUserModelId
                ?? "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgCts?.Cancel();
        _thumbCts?.Cancel();
        _changeChannel.Writer.TryComplete();

        UnsubscribeFromSession();

        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionChanged;
            _sessionManager.SessionsChanged -= OnSessionsChanged;
        }
    }

    public Task PlayPauseAsync() => _transportService.PlayPauseAsync();

    public Task NextTrackAsync() => _transportService.NextTrackAsync();

    public Task PreviousTrackAsync() => _transportService.PreviousTrackAsync();

    public Task SeekAsync(TimeSpan position) => _transportService.SeekAsync(position);

    public Task SeekRelativeAsync(double seconds) => _transportService.SeekRelativeAsync(seconds);

    public Task SeekToAbsoluteAsync(TimeSpan position) => _transportService.SeekToAbsoluteAsync(position);
}

[Flags]
public enum ChangeType
{
    None = 0,
    Heartbeat = 1,
    Timeline = 2,
    Playback = 4,
    MediaProperties = 8,
    SessionChanged = 16,
    ForceRefresh = 32
}

public enum DetectionMode
{
    Idle,
    AwaitingMetadata,
    EventDriven,
    ThrottledMedia
}
