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
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnMediaPublishParams
    {
        public byte ForceRefresh;
        public byte MetadataChanged;
        public byte ThumbnailChanged;
        public byte PlaybackChanged;
        public byte TimelineChanged;
        public byte SourceChanged;
        public byte SeekCapabilityChanged;
        public byte PipChanged;
        public byte ThrottleChanged;
        public byte IsThrottled;
        public byte IsPlaying;
        public long PositionTicks;
        public long LastPositionTicks;
        public long DurationTicks;
        public long NowUtcTicks;
        public long StartupSyncUntilUtcTicks;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_media_detection_resolve_heartbeat_interval_ms(
        int detectionMode,
        out int outIntervalMs);

    [LibraryImport(NativeDll)]
    private static partial int vn_media_detection_should_skip_heartbeat(
        int detectionMode,
        long lastEventTicks,
        long nowUtcTicks,
        out byte outSkip);

    [LibraryImport(NativeDll)]
    private static partial int vn_media_detection_evaluate_should_publish(
        in VnMediaPublishParams @params,
        out byte outShouldPublish);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_build_session_instance_key(
        nint unkPtr,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_extract_process_name(
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_extract_youtube_video_id(
        ReadOnlySpan<char> url,
        int urlLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);



    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_get_spotify_window_title(
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_parse_youtube_window_title(
        ReadOnlySpan<char> extractedTitle,
        int extractedTitleLen,
        Span<char> outTrack,
        int trackCap,
        out int outTrackLen,
        Span<char> outArtist,
        int artistCap,
        out int outArtistLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_evaluate_spotify_secondary_correction(
        ReadOnlySpan<char> groundTruth,
        int groundTruthLen,
        ReadOnlySpan<char> currentTrack,
        int currentTrackLen,
        ReadOnlySpan<char> currentArtist,
        int currentArtistLen,
        Span<char> outTrack,
        int trackCap,
        out int outTrackLen,
        Span<char> outArtist,
        int artistCap,
        out int outArtistLen,
        out byte outCorrected);

    private enum ServiceLifecycleState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Disposed
    }

    private const string MediaSessionLogTag = "MEDIA-SESSION";
    private const string MediaThumbCropLogTag = "MEDIA-THUMB-CROP";
    private const string SpotifyPlatformName = "Spotify";
    private const string YouTubePlatformName = "YouTube";
    private const string SoundCloudPlatformName = "SoundCloud";
    private const string YouTubeLowerPlatformName = "youtube";
    private const string SoundCloudLowerPlatformName = "soundcloud";
    private const string MediaStopLogTag = "MEDIA-STOP";

    private readonly object _lifecycleLock = new();
    private ServiceLifecycleState _state = ServiceLifecycleState.Stopped;
    private Task _stopTask = Task.CompletedTask;
    private bool _restartRequested;
    private int _lifecycleGeneration;
    private Task? _initTask;
    private Task? _processingTask;
    private Task? _heartbeatTask;
    private Task? _stagedRefreshTask;
    private Task _workerDrainTask = Task.CompletedTask;
    private Task? _disposeTask;
    private readonly Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>>? _sessionManagerFactory;

    internal Task? InitTask => _initTask;
    internal Task? ProcessingTask => _processingTask;
    internal Task? HeartbeatTask => _heartbeatTask;
    internal Task? StagedRefreshTask => _stagedRefreshTask;
    internal bool IsRunning => _state == ServiceLifecycleState.Running;
    internal bool IsStarting => _state == ServiceLifecycleState.Starting;
    internal bool IsStopped => _state == ServiceLifecycleState.Stopped;
    internal bool IsDisposed => _state == ServiceLifecycleState.Disposed;
    internal BitmapSource? CachedThumbnail { get; set; }

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _disposed;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly IMediaMetadataLookupService _metadataLookup;
    private readonly IMediaArtworkService _artworkService;
    private readonly IWindowTitleScanner _windowTitleScanner;
    private readonly Func<string> _spotifyWindowTitle;
    private readonly MediaSessionVolumeService _volumeService;
    private readonly MediaTransportControlService _transportService;

    private readonly MediaChangeQueue _changes = new();
    private CancellationTokenSource? _bgCts;
    private DetectionMode _currentMode = DetectionMode.Idle;
    private long _lastEventTimeTicks;
    private int _awaitingMetadataPollGeneration;
    private DateTime _transientSessionGapStartedUtc = DateTime.MinValue;
    private DateTime _youtubeAdTransitionStartedUtc = DateTime.MinValue;
    private DateTime _startupProgressSyncUntilUtc = DateTime.MinValue;

    public event EventHandler<MediaInfo>? MediaChanged;

    private string _lastTrackSignature = "";
    private string _lastThumbTrackIdentity = "";
    private string _cachedThumbnailSource = "";

    private string _pendingSessionInstanceKey = "";
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
    private bool _isNewTrackThisUpdate;
    private string _throttledStaleSmtcTrack = "";
    private BitmapSource? _lastInitialCroppedThumbnail;
    private readonly MediaTimelineSimulator _timelineSimulator = new();
    private readonly MediaTimelineClassifier _timelineClassifier = new();
    private string _lastSoundCloudArtworkIdentity = "";
    private DateTime _lastSoundCloudArtworkAttemptTimeUtc = DateTime.MinValue;
    private int _soundCloudArtworkAttemptCount = 0;
    private const int MaxSoundCloudArtworkAttemptsPerTrack = 2;
    private string _youTubeFetchIdentity = "";
    private DateTime _lastYouTubeThumbnailAttemptTimeUtc = DateTime.MinValue;
    private int _youTubeFetchGeneration;
    private int _youTubeFetchInFlight;
    private string _soundCloudFetchIdentity = "";
    private int _soundCloudFetchGeneration = 0;
    private int _soundCloudFetchInFlight = 0;
    private int _thumbnailFetchGeneration = 0;
    private ArtworkFingerprint? _lastSessionArtworkFingerprint;
    private int _sessionArtworkGeneration = -1;
    private static readonly TimeSpan YouTubeThumbnailRetryInterval = TimeSpan.FromSeconds(1.1);
    private static readonly TimeSpan SoundCloudArtworkRetryInterval = TimeSpan.FromSeconds(1.1);

    private string _lastSource = "";
    private bool _lastIsPlaying = false;
    private bool _lastIsPictureInPicture = false;
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
        : this(metadataLookup, artworkService, windowTitleScanner, null)
    {
    }

    internal MediaDetectionService(
        IMediaMetadataLookupService metadataLookup,
        IMediaArtworkService artworkService,
        IWindowTitleScanner windowTitleScanner,
        Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>>? sessionManagerFactory,
        MediaSourceCache? sourceCache = null,
        Func<string>? spotifyWindowTitle = null)
    {
        _metadataLookup = metadataLookup;
        _artworkService = artworkService;
        _windowTitleScanner = windowTitleScanner;
        _pinReconnect = new MediaPinReconnectWindow(() => _changes.Enqueue(ChangeTypes.ForceRefresh));
        _spotifyWindowTitle = spotifyWindowTitle ?? GetSpotifyWindowTitle;
        _sessionManagerFactory = sessionManagerFactory;
        _volumeService = new MediaSessionVolumeService();
        _transportService = new MediaTransportControlService(GetActiveSession);

        _sourceCache = sourceCache ?? new MediaSourceCache();
        _sourceCache.Load();
    }
    public IMediaArtworkService ArtworkService => _artworkService;

    private sealed record PinnedSession(string Key, string Source, GlobalSystemMediaTransportControlsSession Session,
        string Track, string SourceAppId, HashSet<string> OtherSessionKeys, string PreviousKey = "");
    private volatile bool _keepPinnedOnTrackChange = true;

    public bool KeepPinnedOnTrackChange
    {
        get => _keepPinnedOnTrackChange;
        set => _keepPinnedOnTrackChange = value;
    }
    private PinnedSession? _pinnedSession;
    private readonly MediaPinReconnectWindow _pinReconnect;
    private MediaInfo? _lastPublishedInfo;
    private bool _pinClosedThisUpdate;

    public bool IsSessionPinned(string? sessionKey)
    {
        var pinned = Volatile.Read(ref _pinnedSession);
        return pinned != null && !string.IsNullOrEmpty(sessionKey)
            && (pinned.Key == sessionKey || pinned.PreviousKey == sessionKey);
    }

    public bool AcceptsMediaUpdate(MediaInfo info)
    {
        var pinned = Volatile.Read(ref _pinnedSession);
        return pinned == null || pinned.Key == info.SessionInstanceKey;
    }

    public bool ToggleSessionPin(MediaInfo info)
    {
        string sessionKey = info.SessionInstanceKey;
        if (string.IsNullOrEmpty(sessionKey)) return false;
        if (IsSessionPinned(sessionKey))
            Interlocked.Exchange(ref _pinnedSession, null);
        else
        {
            try
            {
                // The displayed snapshot may lag behind the detector. Resolve the
                // clicked session from the list rather than trusting active display.
                var sessions = _sessionManager?.GetSessions();
                var session = sessions?.FirstOrDefault(s => BuildSessionInstanceKey(s) == sessionKey);
                if (session == null || sessions == null) return false;
                var others = sessions.Select(BuildSessionInstanceKey).Where(key => key != sessionKey).ToHashSet(StringComparer.Ordinal);
                Interlocked.Exchange(ref _pinnedSession, new PinnedSession(sessionKey, info.MediaSource, session,
                    BuildTrackIdentity(info.CurrentTrack, ""), info.SourceAppId, others));
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("MEDIA-PIN", $"Unable to pin displayed session: {ex.Message}");
                return false;
            }
        }
        _pinReconnect.Reset();
        _changes.Enqueue(ChangeTypes.ForceRefresh);
        return true;
    }

    private GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        var pinned = Volatile.Read(ref _pinnedSession);
        if (pinned != null) return pinned.Session;

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

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _state == ServiceLifecycleState.Disposed) return;
            if (_state == ServiceLifecycleState.Starting || _state == ServiceLifecycleState.Running) return;
            if (_state == ServiceLifecycleState.Stopping)
            {
                if (_restartRequested) return;
                _restartRequested = true;
                RestartAfterStopAsync(_stopTask).SafeFireAndForget("MEDIA-RESTART");
                return;
            }

            _restartRequested = false;
            _state = ServiceLifecycleState.Starting;
            int generation = unchecked(++_lifecycleGeneration);

            _bgCts?.Cancel();
            _bgCts?.Dispose();
            _bgCts = new CancellationTokenSource();
            var ct = _bgCts.Token;

            _startupProgressSyncUntilUtc = DateTime.UtcNow.AddSeconds(5);
            _initTask = StartCoreAsync(generation, ct);
        }
    }

    private async Task StartCoreAsync(int generation, CancellationToken ct)
    {
        try
        {
            var sessionManager = await GetSessionManagerAsync(ct).ConfigureAwait(false);

            lock (_lifecycleLock)
            {
                if (ct.IsCancellationRequested || _disposed || generation != _lifecycleGeneration || _state != ServiceLifecycleState.Starting)
                {
                    return;
                }

                _sessionManager = sessionManager;
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += OnSessionChanged;
                    _sessionManager.SessionsChanged += OnSessionsChanged;
                    SubscribeToCurrentSession();
                }

                _state = ServiceLifecycleState.Running;

                _processingTask = Task.Run(() => ProcessingLoopAsync(ct), ct);
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), ct);
                _stagedRefreshTask = Task.Run(() => RunStagedRefreshAsync(ct), ct);
            }

            _changes.Enqueue(ChangeTypes.ForceRefresh);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation during init
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MEDIA-INIT", $"Failed to init SMTC: {ex.Message}");
            lock (_lifecycleLock)
            {
                if (generation == _lifecycleGeneration && _state == ServiceLifecycleState.Starting)
                {
                    _state = ServiceLifecycleState.Stopped;
                }
            }
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetSessionManagerAsync(CancellationToken ct)
    {
        if (_sessionManagerFactory != null)
        {
            return await _sessionManagerFactory(ct).ConfigureAwait(false);
        }

        return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
    }

    private async Task RunStagedRefreshAsync(CancellationToken ct)
    {
        int[] stagedDelaysMs = { 120, 350, 800 };
        foreach (int delayMs in stagedDelaysMs)
        {
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || ct.IsCancellationRequested)
            {
                return;
            }

            _changes.Enqueue(ChangeTypes.ForceRefresh);
        }
    }

    public void Stop()
    {
        StopAsync().SafeFireAndForget("MEDIA-STOP");
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            _restartRequested = false;
            if (_state == ServiceLifecycleState.Stopping) return _stopTask;
            return _stopTask = StopCoreAsync(isDisposing: false);
        }
    }

    private async Task RestartAfterStopAsync(Task stopping)
    {
        await stopping.ConfigureAwait(false);
        lock (_lifecycleLock)
        {
            if (!_restartRequested || _disposed) return;
            _restartRequested = false;
            Start();
        }
    }

    private void SubscribeToCurrentSession()
    {
        if (_currentSession != null)
        {
            if (!AreSameSession(_currentSession, _activeDisplaySession))
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
            }
            _currentSession = null;
        }

        var sessionManager = _sessionManager;
        if (sessionManager == null) return;

        try
        {
            _currentSession = sessionManager.GetCurrentSession();
            if (_currentSession != null && !AreSameSession(_currentSession, _activeDisplaySession))
            {
                _currentSession.TimelinePropertiesChanged += OnTimelineChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackChanged;
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                RuntimeLog.Debug(MediaSessionLogTag, $"Subscribed to session: {_currentSession.SourceAppUserModelId}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(MediaSessionLogTag, $"Failed to subscribe to session: {ex.Message}");
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
        }

        DetachActiveDisplaySession();
        _currentSession = null;
    }

    private void DetachActiveDisplaySession()
    {
        if (_activeDisplaySession != null && !AreSameSession(_activeDisplaySession, _currentSession))
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

        _activeDisplaySession = null;
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changes.Enqueue(ChangeTypes.Timeline);
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changes.Enqueue(ChangeTypes.Playback);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
        _changes.Enqueue(ChangeTypes.MediaProperties);
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Log("Session Changed", "System-wide session focus shifted");
        _changes.Enqueue(ChangeTypes.SessionChanged);
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Log("Sessions Changed", "Session list changed (app opened/closed)");
        _changes.Enqueue(ChangeTypes.SessionChanged);
    }

    private static void Log(string tag, string message)
    {
        RuntimeLog.Log($"MEDIA-{tag}", message);
    }

    #region Background Processing

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        await foreach (var types in _changes.ReadAllAsync(ct))
        {
            try
            {
                const ChangeTypes ForceRefreshMask = ChangeTypes.MediaProperties
                    | ChangeTypes.Playback
                    | ChangeTypes.SessionChanged
                    | ChangeTypes.ForceRefresh;
                bool forceRefresh = (types & ForceRefreshMask) != 0;

                if ((types & ChangeTypes.SessionChanged) != 0)
                {
                    SubscribeToCurrentSession();
                }

                await UpdateMediaInfoAsync(forceRefresh, ct);
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
            int intervalMs = vn_media_detection_resolve_heartbeat_interval_ms((int)_currentMode, out int outMs) == 0 ? outMs : 3000;
            var interval = TimeSpan.FromMilliseconds(intervalMs);

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) { break; }

            var lastEvtTicks = Interlocked.Read(ref _lastEventTimeTicks);
            if (vn_media_detection_should_skip_heartbeat((int)_currentMode, lastEvtTicks, DateTime.UtcNow.Ticks, out byte skip) == 0 && skip != 0)
            {
                continue;
            }

            _changes.Enqueue(ChangeTypes.Heartbeat);
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
            RuntimeLog.Debug("MEDIA-MODE", $"Detection mode: {oldMode} -> {_currentMode} (Playing={info.IsAnyMediaPlaying}, Track='{info.CurrentTrack}', Throttled={info.IsThrottled})");
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

                    _changes.Enqueue(ChangeTypes.Heartbeat);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on token cancellation
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-METADATA-POLL", ex.ToString());
            }
        }, token);
    }

    #endregion

    private async Task UpdateMediaInfoAsync(bool forceRefresh, CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            _pinClosedThisUpdate = false;
            var info = new MediaInfo();
            List<string>? windowTitles = null;

            bool shouldQuarantineBrowserAd = await TryGetMediaSessionInfoAsync(info, forceRefresh, () =>
            {
                windowTitles ??= GetAllWindowTitles();
                return windowTitles;
            });
            ct.ThrowIfCancellationRequested();

            if (_pinClosedThisUpdate)
            {
                _timelineSimulator.Reset();
                _throttledStaleSmtcTrack = "";
                _transientSessionGapStartedUtc = DateTime.MinValue;
                _emptyMetadataStartTime = DateTime.MinValue;
                UpdateDetectionMode(info);
                CommitPublishedState(info, info.GetSignature());
                await FireMediaChangedAsync(info);
                _changes.Enqueue(ChangeTypes.ForceRefresh);
                return;
            }

            var waitingPin = Volatile.Read(ref _pinnedSession);
            if (waitingPin != null && _pinReconnect.IsWaiting)
            {
                // Stop UI extrapolation immediately while allowing a short
                // reconnection window for players that rebuild sessions per song.
                if (_lastPublishedInfo is { } last && last.SessionInstanceKey == waitingPin.Key)
                {
                    var paused = last.Clone();
                    paused.IsPlaying = false;
                    paused.IsSeekEnabled = false;
                    paused.IsThrottled = false;
                    paused.LastUpdated = DateTimeOffset.Now;
                    _timelineSimulator.Reset();
                    _throttledStaleSmtcTrack = "";
                    CommitPublishedState(paused, paused.GetSignature());
                    await FireMediaChangedAsync(paused);
                }
                return;
            }

            if (UpdateTransientSessionGapHold(info))
                return;

            if (shouldQuarantineBrowserAd)
            {
                UpdateDetectionMode(info, isSessionGapRecovery: true);
                return;
            }

            if (!AcceptsMediaUpdate(info)) return;
            if (Volatile.Read(ref _pinnedSession) == null)
            {
                ApplyWindowTitleFallback(info, ref windowTitles);
                TrackNameChangeBookkeeping(info);
                ApplyVideoTimelineRecovery(info, ref windowTitles);
            }
            else
            {
                TrackNameChangeBookkeeping(info);
            }

            ct.ThrowIfCancellationRequested();
            DetectPictureInPictureState(info);

            TrackNameChangeBookkeeping(info, resetThrottle: false);
            if (Volatile.Read(ref _pinnedSession) == null)
                PreserveSoundCloudSourceIfNeeded(info, ref windowTitles);

            info.IsThrottled = _timelineSimulator.IsThrottled;
            UpdateDetectionMode(info);
            var currentSignature = info.GetSignature();

            if (UpdateEmptyMetadataHold(info, currentSignature))
                return;

            bool isNewTrackForThumbnail = ComputeIsNewTrackForThumbnail(info);
            // Pre-publish thumbnail fallback & initial SMTC crop moved into TryPublishMediaChangeAsync.

            ct.ThrowIfCancellationRequested();
            if (!await TryPublishMediaChangeAsync(info, currentSignature, isNewTrackForThumbnail, forceRefresh))
                return;

            StartThumbnailFetchIfNeeded(info.Clone(), isNewTrackForThumbnail);
        }
        finally
        {
            _updateLock.Release();
        }
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
        ApplyPrePublishThumbnailFallbackAndCrop(info, isNewTrackForThumbnail);
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

#pragma warning disable S3776
    private void ApplyWindowTitleFallback(MediaInfo info, ref List<string>? windowTitles)
    {
        bool needsFallback = !info.IsAnyMediaPlaying || (string.IsNullOrEmpty(info.CurrentTrack) && info.Platform == MediaPlatform.Browser) || info.Platform == MediaPlatform.Browser || string.IsNullOrEmpty(info.MediaSource);

        bool recentlyHadMedia = !info.IsAnyMediaPlaying &&
                                (MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.YouTube || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.Browser || MediaPlatformExtensions.ParsePlatform(_lastSource) == MediaPlatform.SoundCloud) &&
                                _emptyMetadataStartTime != DateTime.MinValue &&
                                (DateTime.UtcNow - _emptyMetadataStartTime).TotalSeconds < 6.0;

        if (needsFallback && (info.IsAnyMediaPlaying || recentlyHadMedia))
        {
            windowTitles ??= GetAllWindowTitles();

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
                    if (!string.IsNullOrEmpty(info.CurrentArtist)) _stableArtist = info.CurrentArtist;
                }
            }

            if (string.IsNullOrEmpty(info.CurrentTrack))
            {
                foreach (var title in windowTitles)
                {
                    if (title.Contains(YouTubePlatformName, StringComparison.OrdinalIgnoreCase) &&
                        !title.StartsWith(YouTubePlatformName + " -", StringComparison.OrdinalIgnoreCase) &&
                        !title.Equals(YouTubePlatformName, StringComparison.OrdinalIgnoreCase) &&
                        MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(info.IsSpotifyPlaying, info.MediaSource))
                    {
                        info.IsAnyMediaPlaying = true;
                        info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                        info.YouTubeTitle = ExtractVideoTitle(title, YouTubePlatformName);
                        info.CurrentTrack = info.YouTubeTitle;

                        info.CurrentArtist = !string.IsNullOrEmpty(_stableArtist) && (DateTime.UtcNow - _lastSourceConfirmedTime).TotalSeconds < 15.0 ? _stableArtist : YouTubePlatformName;
                        break;
                    }

                    if (title.Contains(SoundCloudPlatformName, StringComparison.OrdinalIgnoreCase) &&
                        !title.StartsWith(SoundCloudPlatformName + " -", StringComparison.OrdinalIgnoreCase) &&
                        !title.Equals(SoundCloudPlatformName, StringComparison.OrdinalIgnoreCase) &&
                        MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(info.IsSpotifyPlaying, info.MediaSource))
                    {
                        info.IsAnyMediaPlaying = true;
                        MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);

                        string extractedTitle = ExtractVideoTitle(title, SoundCloudPlatformName);
                        var (parsedArtist, parsedTrack) = PlatformDetector.ParseSoundCloudTitle(extractedTitle);
                        info.CurrentTrack = parsedTrack;
                        info.CurrentArtist = parsedArtist;

                        SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                        break;
                    }
                }
            }
        }
    }

#pragma warning disable S3776
    private void ApplyVideoTimelineRecovery(MediaInfo info, ref List<string>? windowTitles)
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
        if (isVideoSource && info.IsPlaying)
        {

            _timelineSimulator.UpdateObservedPosition(info.Position);

            double progress = info.Duration.TotalSeconds > 0 ? info.Position.TotalSeconds / info.Duration.TotalSeconds : 0;
            bool positionStuck = _timelineSimulator.IsPositionStuck(TimeSpan.FromSeconds(1.5));
            bool atEndStuck = MediaTimelineSimulator.IsAtEndStuck(progress, _lastMetadataChangeTime, TimeSpan.FromSeconds(1.2));

            if (progress >= 1.0 && info.Duration.TotalSeconds > 0 && _timelineSimulator.IsThrottled)
            {
                _timelineSimulator.Reset();
                _throttledStaleSmtcTrack = "";
                _changes.Enqueue(ChangeTypes.ForceRefresh);
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
                    Span<char> trackBuf = stackalloc char[512];
                    Span<char> artistBuf = stackalloc char[512];

                    foreach (var title in windowTitles)
                    {
                        if (title.Contains(YouTubePlatformName, StringComparison.OrdinalIgnoreCase) &&
                            !title.StartsWith(YouTubePlatformName + " -", StringComparison.OrdinalIgnoreCase) &&
                            !title.Equals(YouTubePlatformName, StringComparison.OrdinalIgnoreCase))
                        {
                            var extractedTitle = ExtractVideoTitle(title, YouTubePlatformName);
                            string trackName = extractedTitle;
                            string artistName = YouTubePlatformName;
                            if (vn_media_detection_parse_youtube_window_title(
                                    extractedTitle.AsSpan(), extractedTitle.Length,
                                    trackBuf, trackBuf.Length, out int tLen,
                                    artistBuf, artistBuf.Length, out int aLen) == 0)
                            {
                                trackName = new string(trackBuf[..tLen]);
                                artistName = new string(artistBuf[..aLen]);
                            }

                            if (string.IsNullOrWhiteSpace(trackName) ||
                                trackName.Equals(YouTubePlatformName, StringComparison.OrdinalIgnoreCase) ||
                                trackName.StartsWith("- " + YouTubePlatformName, StringComparison.OrdinalIgnoreCase))
                                continue;

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
                                ApplyTimelineAvailability(info, true, true, false, TimeSpan.Zero, DateTime.UtcNow);
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

                                var timeOnTrack = DateTime.UtcNow - _lastMetadataChangeTime;
                                info.Position = timeOnTrack;
                                info.LastUpdated = DateTimeOffset.Now;
                                ApplyTimelineAvailability(info, true, false, false, info.Duration, DateTime.UtcNow);
                                foundRecovery = true;
                                break;
                            }
                        }
                    }
                }

                if (!foundRecovery && _timelineSimulator.IsThrottled)
                {
                    _timelineSimulator.TryExitThrottleIfStalled(TimeSpan.FromSeconds(3.5));
                    if (!_timelineSimulator.IsThrottled) _throttledStaleSmtcTrack = "";
                }
            }
            else if (_timelineSimulator.IsThrottled)
            {
                _timelineSimulator.TryExitThrottleIfPositionResumed(TimeSpan.FromMilliseconds(500));
                if (!_timelineSimulator.IsThrottled) _throttledStaleSmtcTrack = "";
            }
        }
        else
        {
            _timelineSimulator.Reset();
            _throttledStaleSmtcTrack = "";
        }
    }

    private void ApplyPrePublishThumbnailFallbackAndCrop(MediaInfo info, bool isNewTrackForThumbnail)
    {
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
            (info.Thumbnail.PixelWidth >= 200 || IsLikelySoundCloudArtworkCandidate(info.Thumbnail)))
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

#pragma warning disable S3776
    private void StartThumbnailFetchIfNeeded(MediaInfo info, bool isNewTrackForThumbnail)
    {
        bool forceFetchForTrackChange = isNewTrackForThumbnail;
        if (forceFetchForTrackChange)
        {
            _windowTitleScanner.InvalidateUrlCaches();
            ClearMismatchCache();
            ForgetVideoIdCacheExceptForTrack(BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
            _soundCloudArtworkAttemptCount = 0;
        }

        bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                            TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                            MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;

        bool browserHasYouTubeTabOpen = false;
        if (info.Platform == MediaPlatform.Browser && !hasSoundCloudSessionOverride)
        {
            try
            {
                var browserUrl = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
                if (!string.IsNullOrEmpty(browserUrl))
                {
                    if (browserUrl.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                        browserUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
                    {
                        browserHasYouTubeTabOpen = true;
                    }
                    else if (!string.IsNullOrEmpty(SoundCloudMatching.ExtractTrackUrl(browserUrl)))
                    {
                        hasSoundCloudSessionOverride = true;
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Debug("MEDIA-URL", $"URL scan ignored: {ex.Message}");
            }
        }

        bool thumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail);
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
            ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
        });
        bool isPotentialYouTube = sourcePlan.IsPotentialYouTube;
        bool isPotentialSoundCloud = sourcePlan.IsPotentialSoundCloud;

        string youTubeTrackIdentity = isPotentialYouTube
            ? $"{BuildTrackIdentity(info.CurrentTrack, "")}|{info.SourceAppId}|{info.SessionInstanceKey}"
            : "";
        string soundCloudTrackIdentity = isPotentialSoundCloud
            ? BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist)
            : "";

        bool needsFetch = forceFetchForTrackChange || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
        if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !forceFetchForTrackChange) needsFetch = false;

        if (needsFetch && !forceFetchForTrackChange && CachedThumbnail != null && CachedThumbnail.PixelWidth >= 200)
        {
            needsFetch = false;
            info.Thumbnail = CachedThumbnail;
            _timelineSimulator.RecoveredThumbnail = CachedThumbnail;
        }

        if (needsFetch && info.Thumbnail != null &&
            (info.Thumbnail.PixelWidth >= 200 || IsLikelySoundCloudArtworkCandidate(info.Thumbnail)))
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
            bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                  info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

            if (isNearSquare || isTopicChannel)
            {
                needsFetch = false;
                string thumbnailSource = isPotentialSoundCloud && !isPotentialYouTube
                    ? SoundCloudPlatformName
                    : info.MediaSource ?? YouTubePlatformName;
                if (!ReferenceEquals(info.Thumbnail, _lastInitialCroppedThumbnail))
                {
                    RuntimeLog.Debug(MediaThumbCropLogTag,
                        $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
                    var cropped = CropToSquare(info.Thumbnail, thumbnailSource, forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
                    info.Thumbnail = cropped;
                    CachedThumbnail = cropped;
                    _timelineSimulator.RecoveredThumbnail = cropped;
                }
                _cachedThumbnailSource = isPotentialYouTube ? YouTubePlatformName : thumbnailSource;
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
            var cts = CreateAndReplaceThumbnailCts();
            var token = cts.Token;
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
                fetchGeneration, needsBetterThumb, cts), token);
        }
        else if (isPotentialSoundCloud && !isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            bool isNewSoundCloudTrack = !string.IsNullOrEmpty(soundCloudTrackIdentity) &&
                                        !string.Equals(soundCloudTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
            if (isNewSoundCloudTrack)
            {
                _soundCloudArtworkAttemptCount = 0;
            }

            bool sameTrackFetchRunning =
                Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                string.Equals(_soundCloudFetchIdentity, soundCloudTrackIdentity, StringComparison.Ordinal);
            bool hasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != MediaPlatform.SoundCloud;

            bool shouldStartSoundCloudFetch =
                (thumbnailIsNullOrPlaceholder || hasMismatchedThumbSource) &&
                _soundCloudArtworkAttemptCount < MaxSoundCloudArtworkAttemptsPerTrack &&
                ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
                {
                    IsNewSoundCloudTrack = isNewSoundCloudTrack,
                    SameTrackFetchRunning = sameTrackFetchRunning,
                    ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
                    HasMismatchedThumbSource = hasMismatchedThumbSource,
                    RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
                });

            if (shouldStartSoundCloudFetch)
            {
                _soundCloudArtworkAttemptCount++;
                _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
                _lastSoundCloudArtworkIdentity = soundCloudTrackIdentity;
                var cts = CreateAndReplaceThumbnailCts();
                var token = cts.Token;
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

                bool requireStrongMatch = info.Platform != MediaPlatform.SoundCloud;

                _ = Task.Run(() => FetchSoundCloudThumbnailAsync(info, token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, requireStrongMatch, cts), token);
            }
        }
        else
        {
            CancelAndClearThumbnailCts();
            Interlocked.Increment(ref _youTubeFetchGeneration);
            _youTubeFetchIdentity = "";
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            _soundCloudFetchIdentity = "";
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
        }
    }

    private CancellationTokenSource CreateAndReplaceThumbnailCts()
    {
        CancellationTokenSource cts;
        var bgCts = _bgCts;
        if (bgCts != null)
        {
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(bgCts.Token);
            }
            catch (ObjectDisposedException)
            {
                cts = new CancellationTokenSource();
            }
        }
        else
        {
            cts = new CancellationTokenSource();
        }

        var oldCts = Interlocked.Exchange(ref _thumbCts, cts);
        CancelAndDisposeThumbnailCts(oldCts);
        return cts;
    }

    private void CancelAndClearThumbnailCts()
    {
        var oldCts = Interlocked.Exchange(ref _thumbCts, null);
        CancelAndDisposeThumbnailCts(oldCts);
    }

    private void ReleaseThumbnailCts(CancellationTokenSource? ownedCts)
    {
        if (ownedCts == null) return;
        Interlocked.CompareExchange(ref _thumbCts, null, ownedCts);
        try
        {
            ownedCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CancelAndDisposeThumbnailCts(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

#pragma warning disable S107, S3776
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
        bool needsBetterThumb,
        CancellationTokenSource? ownedCts = null)
    {
        try
        {
            // Accept the artist this lookup resolved as well as the original SMTC
            // artist, while retaining the track, session and generation checks.
            int lifecycleGenAtStart = Volatile.Read(ref _lifecycleGeneration);
            bool IsCurrentFetch() =>
                !_disposed &&
                _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
                lifecycleGenAtStart == Volatile.Read(ref _lifecycleGeneration) &&
                !token.IsCancellationRequested &&
                info.Platform != MediaPlatform.SoundCloud &&
                _lastPublishedInfo?.Platform != MediaPlatform.SoundCloud &&
                (_lastPublishedInfo == null || _lastPublishedInfo.IsIndeterminate == info.IsIndeterminate) &&
                generationAtStart == Volatile.Read(ref _thumbnailFetchGeneration) &&
                fetchGeneration == Volatile.Read(ref _youTubeFetchGeneration) &&
                (IsStillSamePublishedTrack(
                    trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch) ||
                 IsStillSamePublishedTrack(
                    trackDuringFetch, info.CurrentArtist, sourceAppDuringFetch, sessionKeyDuringFetch));

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
            bool timelineRecovered = false;

            void RecoverDuration(TimeSpan duration)
            {
                if (info.IsIndeterminate || !MediaTimelinePolicy.HasFiniteDuration(duration))
                    return;

                _timelineSimulator.RecoveredDuration = duration;
                info.Duration = duration;
                info.IsTimelinePending = false;
                timelineRecovered = true;
            }

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

            if (!string.IsNullOrEmpty(videoId) && TryGetCachedMismatchVideoId(videoId))
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
                    // Multiple tabs can contain different videos by the same artist.
                    // An author match alone does not identify the playing video.
                    bool videoMatchesTrack = urlResult.TitleMatches(trackDuringFetch);

                    if (!videoMatchesTrack)
                    {
                        RuntimeLog.Debug("META-YOUTUBE-API",
                            $"video-mismatch: api-title='{urlResult.Title}' smtc-track='{trackDuringFetch}' videoId={videoId} -> discarding stale videoId");
                        if (!await ApplyIfCurrentAsync(() =>
                            {
                                CacheMismatchVideoId(videoId);
                                EvictVideoIdCacheEntry(trackDuringFetch, videoId);
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
                            CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId);
                            CacheVideoIdForTrack(trackDuringFetch, videoId);

                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.IsYouTubeRunning = true;
                            SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.Save();

                            if (!string.IsNullOrEmpty(urlResult.Author) && urlResult.Author != YouTubePlatformName)
                            {
                                info.CurrentArtist = urlResult.Author;
                                _stableArtist = urlResult.Author;
                                _lastSourceConfirmedTime = DateTime.UtcNow;
                            }

                            RecoverDuration(urlResult.Duration);

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
                else
                {
                    RuntimeLog.Debug("META-YOUTUBE-API",
                        $"validation-failed: oEmbed returned null for videoId={videoId} track='{trackDuringFetch}' -> discarding unvalidated videoId");
                    videoId = null;
                }
            }

            while (retryCount < 3 && !token.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(videoId))
                {
                    var result = await TryGetYouTubeVideoIdWithInfoAsync(trackDuringFetch, artistDuringFetch, token);
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

                            if (!string.IsNullOrEmpty(result.Author) && result.Author != YouTubePlatformName)
                            {
                                info.CurrentArtist = result.Author;
                                _stableArtist = result.Author;
                                _lastSourceConfirmedTime = DateTime.UtcNow;
                            }

                            RecoverDuration(result.Duration);

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

                    if (searchResult != null && !string.IsNullOrEmpty(searchResult.Id))
                    {
                        videoId = searchResult.Id;

                        if (!await ApplyIfCurrentAsync(() =>
                        {
                            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                            info.IsYouTubeRunning = true;
                            SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                            _sourceCache.Save();

                            CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId);
                            CacheVideoIdForTrack(trackDuringFetch, videoId);

                            if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != YouTubePlatformName)
                            {
                                info.CurrentArtist = searchResult.Author;
                                _stableArtist = searchResult.Author;
                                _lastSourceConfirmedTime = DateTime.UtcNow;
                            }

                            RecoverDuration(searchResult.Duration);

                            if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                            {
                                preferredThumbnailUrl = searchResult.ThumbnailUrl;
                            }
                        }))
                        {
                            return;
                        }

                        RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH",
                            $"title-search-resolved track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                    }
                }

                if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                {
                    MediaInfo? metadataSnapshot = null;
                    if (!await ApplyIfCurrentAsync(() =>
                    {
                        info.YouTubeVideoId = videoId;
                        bool shouldPublishMetadata = timelineRecovered || (!string.IsNullOrEmpty(_stableArtist) &&
                            _stableArtist != YouTubePlatformName &&
                            (IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch) ||
                             IsStillSamePublishedTrack(trackDuringFetch, info.CurrentArtist, sourceAppDuringFetch, sessionKeyDuringFetch)));

                        if (_lastPublishedInfo != null)
                        {
                            _lastPublishedInfo.YouTubeVideoId = videoId;
                            if (info.Platform == MediaPlatform.YouTube)
                            {
                                MediaSourceClassifier.SetPlatformRunning(_lastPublishedInfo, MediaPlatform.YouTube);
                            }
                            if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != YouTubePlatformName)
                            {
                                _lastPublishedInfo.CurrentArtist = info.CurrentArtist;
                            }
                            if (timelineRecovered && MediaTimelinePolicy.HasFiniteDuration(info.Duration) && !_lastPublishedInfo.IsIndeterminate)
                            {
                                _lastPublishedInfo.Duration = info.Duration;
                                _lastPublishedInfo.IsTimelinePending = false;
                            }
                        }

                        if (shouldPublishMetadata)
                        {
                            var target = _lastPublishedInfo ?? info;
                            _lastPublishedInfo = target.Clone();
                            string sig = _lastPublishedInfo.GetSignature();
                            _lastPublishedSignature = sig;
                            _lastTrackSignature = sig;
                            _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                            _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");
                            metadataSnapshot = _lastPublishedInfo.Clone();
                        }
                    }))
                        return;

                    if (metadataSnapshot != null)
                    {
                        await FireMediaChangedAsync(metadataSnapshot);
                    }

                    if (needsBetterThumb || info.Platform == MediaPlatform.YouTube)
                    {
                        BitmapSource? frameBitmap = null;
                        string thumbnailUrl;

                        if (!string.IsNullOrWhiteSpace(preferredThumbnailUrl))
                        {
                            thumbnailUrl = preferredThumbnailUrl;
                            frameBitmap = await DownloadImageAsync(thumbnailUrl, token);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 200)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl, token);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/sddefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl, token);
                            if (!IsCurrentFetch())
                                return;

                            if (frameBitmap != null && frameBitmap.PixelWidth < 400)
                                frameBitmap = null;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl, token);
                            if (!IsCurrentFetch())
                                return;
                        }

                        if (frameBitmap == null)
                        {
                            thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
                            frameBitmap = await DownloadImageAsync(thumbnailUrl, token);
                            if (!IsCurrentFetch())
                                return;
                        }

                        if (frameBitmap != null && IsCurrentFetch())
                        {
                            bool isYtFetchTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                         info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                            RuntimeLog.Debug(MediaThumbCropLogTag,
                                $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
                            frameBitmap = CropToSquare(frameBitmap, YouTubePlatformName, forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
                            MediaInfo? publishedSnapshot = null;
                            if (!await ApplyIfCurrentAsync(() =>
                            {
                                _timelineSimulator.RecoveredThumbnail = frameBitmap;
                                CachedThumbnail = frameBitmap;
                                _cachedThumbnailSource = MediaPlatform.YouTube.ToDisplayString();
                                info.Thumbnail = frameBitmap;

                                var target = _lastPublishedInfo ?? info;
                                target.Thumbnail = frameBitmap;
                                target.YouTubeVideoId = videoId;
                                if (info.Platform == MediaPlatform.YouTube)
                                {
                                    MediaSourceClassifier.SetPlatformRunning(target, MediaPlatform.YouTube);
                                }
                                if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != YouTubePlatformName)
                                {
                                    target.CurrentArtist = info.CurrentArtist;
                                }
                                if (timelineRecovered && MediaTimelinePolicy.HasFiniteDuration(info.Duration) && !target.IsIndeterminate)
                                {
                                    target.Duration = info.Duration;
                                    target.IsTimelinePending = false;
                                }

                                _lastPublishedInfo = target.Clone();
                                string sig = _lastPublishedInfo.GetSignature();
                                _lastPublishedSignature = sig;
                                _lastTrackSignature = sig;
                                _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                                _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");

                                publishedSnapshot = _lastPublishedInfo.Clone();
                                publishedSnapshot.IsThumbnailOnlyUpdate = true;
                            }))
                            {
                                return;
                            }

                            if (publishedSnapshot != null)
                            {
                                await FireMediaChangedAsync(publishedSnapshot);
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

                            bool pollMatches = pollResult.TitleMatches(trackDuringFetch);

                            if (!pollMatches)
                            {
                                CacheMismatchVideoId(polledId);
                                continue;
                            }

                            videoId = polledId;
                            if (!await ApplyIfCurrentAsync(() =>
                            {
                                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                                info.IsYouTubeRunning = true;
                                SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                _sourceCache.Save();

                                if (!string.IsNullOrEmpty(pollResult.Author) && pollResult.Author != YouTubePlatformName)
                                {
                                    info.CurrentArtist = pollResult.Author;
                                    _stableArtist = pollResult.Author;
                                    _lastSourceConfirmedTime = DateTime.UtcNow;
                                }
                                RecoverDuration(pollResult.Duration);
                            }))
                            {
                                return;
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

                                if (searchResult != null && !string.IsNullOrEmpty(searchResult.Id))
                                {
                                    videoId = searchResult.Id;
                                    if (!await ApplyIfCurrentAsync(() =>
                                    {
                                        MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.YouTube);
                                        SetSessionSourceOverride(info, MediaPlatform.YouTube.ToDisplayString());
                                        _sourceCache.SetBoth(trackDuringFetch, BuildTrackIdentity(trackDuringFetch, artistDuringFetch), MediaPlatform.YouTube.ToDisplayString());
                                        _sourceCache.Save();
                                        CacheVideoIdForTrack(BuildTrackIdentity(trackDuringFetch, artistDuringFetch), videoId);
                                        CacheVideoIdForTrack(trackDuringFetch, videoId);

                                        if (!string.IsNullOrEmpty(searchResult.Author) && searchResult.Author != YouTubePlatformName)
                                        {
                                            info.CurrentArtist = searchResult.Author;
                                            _stableArtist = searchResult.Author;
                                            _lastSourceConfirmedTime = DateTime.UtcNow;
                                        }
                                        RecoverDuration(searchResult.Duration);
                                    }))
                                    {
                                        return;
                                    }
                                    if (!string.IsNullOrWhiteSpace(searchResult.ThumbnailUrl))
                                        preferredThumbnailUrl = searchResult.ThumbnailUrl;

                                    RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH",
                                        $"title-search-after-poll track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                                }
                            }

                            if (string.IsNullOrEmpty(videoId) &&
                                info.Platform == MediaPlatform.Browser &&
                                IsCurrentFetch())
                            {
                                int scGen = Interlocked.Increment(ref _soundCloudFetchGeneration);
                                Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);
                                await FetchSoundCloudThumbnailAsync(
                                    info, token, trackDuringFetch, artistDuringFetch,
                                    sourceAppDuringFetch, sessionKeyDuringFetch,
                                    generationAtStart, scGen, requireStrongMatch: true);
                                return;
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

                        // The next iteration retries a track lookup. A raw browser URL
                        // here would bypass validation and could belong to another tab.
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* Cancelled */ }
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
            ReleaseThumbnailCts(ownedCts);
        }
    }

#pragma warning disable S107, S3776
    private async Task FetchSoundCloudThumbnailAsync(MediaInfo info, CancellationToken token, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int thumbGenAtStart, int fetchGeneration, bool requireStrongMatch, CancellationTokenSource? ownedCts = null)
    {
        int lifecycleGenAtStart = Volatile.Read(ref _lifecycleGeneration);
        bool IsCurrentSoundCloudFetch() =>
            !_disposed &&
            _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
            lifecycleGenAtStart == Volatile.Read(ref _lifecycleGeneration) &&
            !token.IsCancellationRequested &&
            fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration) &&
            Volatile.Read(ref _thumbnailFetchGeneration) == thumbGenAtStart &&
            IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch);

        try
        {
            if (!IsCurrentSoundCloudFetch())
                return;

            string? artworkUrl = null;
            string? browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
            if (!string.IsNullOrEmpty(browserSoundCloudUrl))
            {
                artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, trackDuringFetch, artistDuringFetch, token);
            }

            if ((string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl)) &&
                IsCurrentSoundCloudFetch())
            {
                artworkUrl = await TryGetSoundCloudArtworkUrlAsync(trackDuringFetch, artistDuringFetch, requireStrongMatch, token);
            }

            if ((string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl)) &&
                string.IsNullOrEmpty(browserSoundCloudUrl) &&
                IsCurrentSoundCloudFetch())
            {
                _windowTitleScanner.InvalidateUrlCaches();
                browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
                if (!string.IsNullOrEmpty(browserSoundCloudUrl))
                {
                    artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, trackDuringFetch, artistDuringFetch, token);
                }
            }

            if (string.IsNullOrEmpty(artworkUrl) ||
                IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl) ||
                !IsCurrentSoundCloudFetch())
            {
                return;
            }

            BitmapSource? frameBitmap = await DownloadImageAsync(artworkUrl, token);
            if (frameBitmap == null ||
                IsLikelySoundCloudPlaceholderThumbnail(frameBitmap) ||
                !IsCurrentSoundCloudFetch())
            {
                return;
            }

            if (info.Platform == MediaPlatform.YouTube || IsLikelyYouTube(info))
            {
                RuntimeLog.Debug(MediaThumbCropLogTag,
                    $"soundcloud-fetch suppressed: track resolved as YouTube track='{trackDuringFetch}' cachedSource='{_cachedThumbnailSource}'");
                return;
            }

            frameBitmap = CropToSquare(frameBitmap, SoundCloudPlatformName) ?? frameBitmap;

            MediaInfo? publishedSnapshot = null;
            await _updateLock.WaitAsync(token);
            try
            {
                if (!IsCurrentSoundCloudFetch() ||
                    info.Platform == MediaPlatform.YouTube ||
                    _lastPublishedInfo?.Platform == MediaPlatform.YouTube ||
                    IsLikelyYouTube(info))
                {
                    return;
                }

                _timelineSimulator.RecoveredThumbnail = frameBitmap;
                CachedThumbnail = frameBitmap;
                _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                if (info.Platform == MediaPlatform.Browser)
                {
                    MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
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

                var target = _lastPublishedInfo ?? info;
                target.Thumbnail = frameBitmap;
                if (info.Platform == MediaPlatform.SoundCloud && target.Platform == MediaPlatform.Browser)
                {
                    MediaSourceClassifier.SetPlatformRunning(target, MediaPlatform.SoundCloud);
                }

                _lastPublishedInfo = target.Clone();
                string sig = _lastPublishedInfo.GetSignature();
                _lastPublishedSignature = sig;
                _lastTrackSignature = sig;
                _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");

                publishedSnapshot = _lastPublishedInfo.Clone();
                publishedSnapshot.IsThumbnailOnlyUpdate = true;
            }
            finally
            {
                _updateLock.Release();
            }

            if (publishedSnapshot != null)
            {
                await FireMediaChangedAsync(publishedSnapshot);
            }
        }
        catch (OperationCanceledException) { /* Cancelled */ }
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
            ReleaseThumbnailCts(ownedCts);
        }
    }

    private static string GetSpotifyWindowTitle()
    {
        Span<char> buf = stackalloc char[512];
        return vn_media_detection_get_spotify_window_title(buf, buf.Length, out int len) == 0 && len > 0
            ? new string(buf[..len])
            : string.Empty;
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
        var native = ((WinRT.IWinRTObject)session).NativeObject;
        Span<char> buf = stackalloc char[512];
        try
        {
            int rc = vn_media_detection_build_session_instance_key(
                native.ThisPtr, sourceApp.AsSpan(), sourceApp.Length, buf, buf.Length, out int len);
            if (rc == -2 && len > buf.Length)
            {
                char[] heapBuf = new char[len];
                rc = vn_media_detection_build_session_instance_key(
                    native.ThisPtr, sourceApp.AsSpan(), sourceApp.Length, heapBuf, heapBuf.Length, out len);
                return rc == 0 && len > 0 ? new string(heapBuf, 0, len) : sourceApp;
            }
            return rc == 0 && len > 0 ? new string(buf[..len]) : sourceApp;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    }

    private static bool AreSameSession(
        GlobalSystemMediaTransportControlsSession? a,
        GlobalSystemMediaTransportControlsSession? b)
    {
        return ReferenceEquals(a, b) ||
               (a != null && b != null && BuildSessionInstanceKey(a) == BuildSessionInstanceKey(b));
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

            return sessions.Any(s => AreSameSession(s, session));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SESSION-PRESENT", ex.ToString());
            return true;
        }
    }

    private static bool IsBrowserSourceApp(string sourceAppId)
    {
        return PlatformDetector.IsBrowserApp(sourceAppId);
    }

    private static string? ExtractProcessNameFromSourceApp(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;
        Span<char> buf = sourceAppId.Length <= 256 ? stackalloc char[256] : new char[sourceAppId.Length];
        return vn_media_detection_extract_process_name(
            sourceAppId.AsSpan(), sourceAppId.Length, buf, buf.Length, out int len) == 0 && len > 0
            ? new string(buf[..len])
            : null;
    }

    private void DetectPictureInPictureState(MediaInfo info)
    {
        if (!info.IsAnyMediaPlaying && string.IsNullOrEmpty(info.CurrentTrack))
        {
            info.IsPictureInPicture = false;
            return;
        }

        string? procName = ExtractProcessNameFromSourceApp(info.SourceAppId);
        bool isBrowser = info.Platform == MediaPlatform.Browser || IsBrowserSourceApp(info.SourceAppId);

        bool isPip = _windowTitleScanner.IsPipActive(procName);
        if (!isPip && isBrowser)
        {
            isPip = _windowTitleScanner.IsPipActive(null);
        }

        info.IsPictureInPicture = isPip;
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

    private static bool IsLikelySoundCloudPlaceholderThumbnail(BitmapSource? thumbnail)
        => ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(thumbnail);

    private static bool IsLikelySoundCloudArtworkCandidate(BitmapSource? thumbnail)
        => ThumbnailHeuristics.IsLikelyArtworkCandidate(thumbnail);

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
        => MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url);

    private async Task<bool> TryGetMediaSessionInfoAsync(MediaInfo info, bool forceRefresh, Func<List<string>> windowTitleFactory)
    {
        if (_sessionManager == null) return false;
        _isNewTrackThisUpdate = false;

        try
        {
            var (session, spotifyGroundTruth, mediaProperties) = await ResolveActiveSessionAsync(forceRefresh);
            if (session == null) return false;

            SwitchActiveDisplaySessionIfNeeded(session);

            var sessionSourceApp = session.SourceAppUserModelId ?? "";
            info.IsAnyMediaPlaying = true;
            info.SourceAppId = sessionSourceApp;
            info.SessionInstanceKey = BuildSessionInstanceKey(session);

            ApplyMediaSourceFromAppId(info, sessionSourceApp);

            mediaProperties ??= await session.TryGetMediaPropertiesAsync();
            string sessionTitle = mediaProperties?.Title ?? "";
            string sessionArtist = mediaProperties?.Artist ?? "";
            string lowerAlbum = (mediaProperties?.AlbumTitle ?? "").ToLowerInvariant();

            RefineMediaSourceFromMetadata(info, sessionTitle.ToLowerInvariant(), sessionArtist.ToLowerInvariant(), lowerAlbum);

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

            await ApplySessionThumbnailAsync(info, mediaProperties, trackChangedForThisPass);

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
            bool hasReliableYouTubeWindowMatch = false;
            if (!string.IsNullOrEmpty(sessionSourceApp) && IsBrowserSourceApp(sessionSourceApp))
            {
                hintedWindowTitles ??= windowTitleFactory();
                hasReliableYouTubeWindowMatch = HasReliablePlatformWindowMatch(hintedWindowTitles, info.CurrentTrack, YouTubeLowerPlatformName);
                browserPlatformHint = hasReliableYouTubeWindowMatch
                    ? YouTubePlatformName
                    : DetectPlatformHint(hintedWindowTitles);
            }

            if (BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
                isBrowserSession: IsBrowserSourceApp(sessionSourceApp),
                currentBrowserPlatformHint: browserPlatformHint,
                lastSource: _lastSource,
                stableSource: _stableSource,
                isCompletingAdTransition))
            {
                MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.YouTube);
            }

            if (!string.IsNullOrEmpty(sessionSourceApp) &&
                IsBrowserSourceApp(sessionSourceApp) &&
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
                                       _sourceCache.TryGet(trackOnlyIdentity, out cachedSource);

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
                hintedSource = DetectPlatformHint(hintedWindowTitles);

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
                    if (info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.SoundCloud)
                    {
                        _sourceCache.Save();
                    }
                }
            }
        }
    }

#pragma warning disable S3776
    private Task ApplySessionThumbnailAsync(MediaInfo info, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties, bool trackChangedForThisPass)
        => ApplySessionArtworkAsync(info, trackChangedForThisPass, async () =>
        {
            var thumbnail = mediaProperties?.Thumbnail;
            if (thumbnail == null) return null;
            using var stream = await thumbnail.OpenReadAsync();
            return stream != null && stream.Size > 0
                ? await ConvertToWpfBitmapAsync(stream, _bgCts?.Token ?? CancellationToken.None)
                : null;
        });

    private async Task ApplySessionArtworkAsync(MediaInfo info, bool trackChangedForThisPass, Func<Task<BitmapSource?>> readArtwork)
    {
        int generation = Volatile.Read(ref _thumbnailFetchGeneration);
        if (info.Platform == MediaPlatform.SoundCloud &&
            MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube)
        {
            // Source resolution can arrive after a speculative browser lookup.
            // The previous provider's cache must not hide the real session artwork.
            CachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            info.Thumbnail = null;
        }

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
        bool refreshSessionArtwork = !isYouTubeLikeSource &&
                                     info.Platform is MediaPlatform.SoundCloud or MediaPlatform.Browser;
        info.Thumbnail = CachedThumbnail;
        if (!trackChangedForThisPass && CachedThumbnail != null && !refreshSessionArtwork)
        {
            info.Thumbnail = CachedThumbnail;
        }
        else
        {
            try
            {
                BitmapSource? newBitmap = await readArtwork();
                if (newBitmap == null || generation != Volatile.Read(ref _thumbnailFetchGeneration)) return;
                var fingerprint = ArtworkFingerprint.Create(newBitmap);
                if ((refreshSessionArtwork || (isYouTubeLikeSource && trackChangedForThisPass)) &&
                    _lastSessionArtworkFingerprint is { } previous && previous.Equals(fingerprint) &&
                    (_sessionArtworkGeneration != generation || CachedThumbnail != null))
                {
                    // Browsers can publish the new title before replacing the old artwork.
                    // Keep reading subsequent snapshots; never attach that old image to this track.
                    // A verified network lookup can still supply a legitimately shared album cover.
                    return;
                }
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
                    RecentTrackChange = (DateTime.UtcNow - _lastMetadataChangeTime).TotalSeconds < 4.0,
                    CachedThumbnailIsNull = CachedThumbnail == null,
                    PixelWidth = newBitmap.PixelWidth,
                    PixelHeight = newBitmap.PixelHeight,
                });

                if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Reject)
                {
                    RuntimeLog.Debug("MEDIA-THUMB-STALE",
                        $"Rejecting stale SMTC thumbnail on track change: track='{info.CurrentTrack}' source='{info.MediaSource}' thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2}");
                    info.Thumbnail = null;
                }
                else if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Accept)
                {
                    _lastSessionArtworkFingerprint = fingerprint;
                    _sessionArtworkGeneration = generation;
                    if (refreshSessionArtwork)
                    {
                        // A pending fallback must not overwrite newer artwork from the player.
                        Interlocked.Increment(ref _soundCloudFetchGeneration);
                        Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
                        _timelineSimulator.RecoveredThumbnail = null;
                    }
                    bool isSmtcTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                              info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                    RuntimeLog.Debug(MediaThumbCropLogTag,
                        $"path=smtc-update track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2} isSmtcTopicChannel={isSmtcTopicChannel}");
                    newBitmap = CropToSquare(newBitmap, info.MediaSource, forceCenterCrop: isSmtcTopicChannel) ?? newBitmap;
                    CachedThumbnail = newBitmap;
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
                    info.Thumbnail = CachedThumbnail;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-THUMB-PROCESS", ex.ToString());
            }
        }
    }

    private async Task ApplyTimelinePropertiesAsync(MediaInfo info, GlobalSystemMediaTransportControlsSession? session, bool forceRefresh)
    {
        bool isNewTrack = _isNewTrackThisUpdate ||
            (!string.IsNullOrEmpty(info.CurrentTrack) &&
             !string.IsNullOrEmpty(_lastTrackName) && info.CurrentTrack != _lastTrackName);
        bool isBrowserTimelineTrack = IsBrowserSourceApp(info.SourceAppId) ||
            info.Platform is MediaPlatform.YouTube or MediaPlatform.SoundCloud or MediaPlatform.Browser;

        try
        {
            var timeline = session?.GetTimelineProperties();
            if (timeline != null)
            {

                bool isInitialOrBigChange = forceRefresh ||
                                           _lastTrackSignature == "" ||
                                           (DateTime.UtcNow - _lastMetadataChangeTime).TotalSeconds < 4.0;

                if (isInitialOrBigChange)
                {

                    await Task.Delay(120, _bgCts?.Token ?? CancellationToken.None);
                    timeline = session?.GetTimelineProperties() ?? timeline;
                }

                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;
                bool isLiveTimeline = false;
                if (isBrowserTimelineTrack && MediaTimelinePolicy.HasFiniteDuration(duration))
                {
                    string identity = $"{info.SourceAppId}|{info.SessionInstanceKey}|{info.CurrentTrack}";
                    TimeSpan endpoint = timeline.EndTime > timeline.StartTime ? timeline.EndTime : timeline.MaxSeekTime;
                    isLiveTimeline = _timelineClassifier.Observe(identity, endpoint,
                        timeline.MinSeekTime - timeline.StartTime, DateTimeOffset.UtcNow, info.IsPlaying);
                }
                else
                {
                    _timelineClassifier.Reset();
                }

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
                    IsLiveTimeline = isLiveTimeline,
                    IsPlaying = info.IsPlaying,
                    PlaybackRate = info.PlaybackRate,
                    NowUtc = DateTimeOffset.UtcNow,
                });

                info.Position = solved.Position;
                info.Duration = solved.Duration;
                info.LastUpdated = solved.LastUpdatedUtc;
                info.IsIndeterminate = solved.IsIndeterminate;
                ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, isLiveTimeline, duration, DateTime.UtcNow);
            }
            else
            {
                _timelineClassifier.Reset();
                info.IsIndeterminate = true;
                ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, false, TimeSpan.Zero, DateTime.UtcNow);
            }
        }
        catch
        {
            _timelineClassifier.Reset();
            info.IsIndeterminate = true;
            ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, false, TimeSpan.Zero, DateTime.UtcNow);
        }
    }

    internal void ApplyTimelineAvailability(MediaInfo info, bool isBrowserTimelineTrack, bool isNewTrack,
        bool isLiveTimeline, TimeSpan reportedDuration, DateTime nowUtc)
    {
        info.IsTimelinePending = false;
        if (!isBrowserTimelineTrack || reportedDuration > TimeSpan.Zero || isLiveTimeline)
            return;

        // Browsers clear their timeline while loading another video. A zero
        // duration supplies no evidence that the new video is a live broadcast.
        info.IsIndeterminate = false;
        if (_lastPublishedInfo is { } previous &&
            (info.SourceAppId != previous.SourceAppId || info.SessionInstanceKey != previous.SessionInstanceKey))
        {
            _lastMetadataChangeTime = nowUtc;
        }

        if (_lastPublishedInfo is { } last &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            info.CurrentTrack == last.CurrentTrack &&
            info.SourceAppId == last.SourceAppId &&
            info.SessionInstanceKey == last.SessionInstanceKey)
        {
            if (last.IsIndeterminate)
            {
                info.IsIndeterminate = true;
                return;
            }

            if (last.HasTimeline)
            {
                info.Duration = last.Duration;
                return;
            }
        }

        info.IsTimelinePending = isNewTrack || _lastTrackSignature == "" ||
            (nowUtc - _lastMetadataChangeTime).TotalSeconds < 4.0;
    }

#pragma warning disable S3776
    private async Task<(GlobalSystemMediaTransportControlsSession? session, string? spotifyGroundTruth, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties)> ResolveActiveSessionAsync(bool forceRefresh)
    {
        // Stop clears the shared reference while this scan can be awaiting
        // metadata. Retain the manager for the lifetime of this scan.
        var sessionManager = _sessionManager;
        if (sessionManager == null) return (null, null, null);
        var pinned = Volatile.Read(ref _pinnedSession);
        if (pinned != null)
        {
            var sessions = sessionManager.GetSessions();
            var match = sessions.FirstOrDefault(s => BuildSessionInstanceKey(s) == pinned.Key);
            if (match != null)
            {
                // Remember competing sessions so a later rebuild cannot bind the
                // pin to a different tab that was already playing alongside it.
                var others = new HashSet<string>(pinned.OtherSessionKeys, StringComparer.Ordinal);
                foreach (var other in sessions)
                {
                    string key = BuildSessionInstanceKey(other);
                    if (key != pinned.Key) others.Add(key);
                }
                if (others.Count != pinned.OtherSessionKeys.Count)
                    Interlocked.CompareExchange(ref _pinnedSession, pinned with { OtherSessionKeys = others }, pinned);
                _pinReconnect.Reset();
                return (match, null, null);
            }
            if (_keepPinnedOnTrackChange)
            {
                // Some players replace their SMTC session on every track. Retain
                // the source preference and reconnect only to one unambiguous new
                // session from that application, never to a known competing tab.
                var replacements = sessions.Where(candidate =>
                    string.Equals(candidate.SourceAppUserModelId, pinned.SourceAppId, StringComparison.OrdinalIgnoreCase)
                    && !pinned.OtherSessionKeys.Contains(BuildSessionInstanceKey(candidate))).Take(2).ToArray();
                if (replacements.Length == 1)
                {
                    var replacement = replacements[0];
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
                        return (replacement, null, null);
                    }
                }
            }
            // Session lists can briefly be empty during track changes. Keep the
            // pinned snapshot until absence is confirmed, never select a rival.
            lock (_lifecycleLock)
            {
                if (_bgCts?.IsCancellationRequested != false || _disposed ||
                    !ReferenceEquals(Volatile.Read(ref _pinnedSession), pinned)) return (null, null, null);
                _pinReconnect.Start();
            }
            if (!_pinReconnect.HasExpired) return (null, null, null);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pinnedSession, null, pinned), pinned))
            {
                _pinReconnect.Reset();
                UnsubscribeFromSession();
                _pinClosedThisUpdate = true;
                RuntimeLog.Info("MEDIA-PIN", "Pinned session closed; restored automatic selection.");
                return (null, null, null);
            }
        }
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
        {
            try
            {
                if (IsIgnoredSourceApp(_activeDisplaySession.SourceAppUserModelId ?? ""))
                {
                    DetachActiveDisplaySession();
                }
                // Browser sessions must be rescored: another tab can start playing
                // while the previously selected tab still reports Playing.
                else if (!IsBrowserSourceApp(_activeDisplaySession.SourceAppUserModelId ?? "") &&
                         (osCurrentSession == null || AreSameSession(osCurrentSession, _activeDisplaySession)))
                {
                    var playback = _activeDisplaySession.GetPlaybackInfo();
                    if (playback != null && playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        var props = await _activeDisplaySession.TryGetMediaPropertiesAsync();
                        if (props != null && !string.IsNullOrEmpty(props.Title))
                        {

                            if (_activeDisplaySession.SourceAppUserModelId?.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                spotifyGroundTruth = _spotifyWindowTitle();
                                if (string.IsNullOrEmpty(spotifyGroundTruth) || !SpotifyTitleContainsTrack(spotifyGroundTruth, props.Title))
                                {

                                    session = null;
                                }
                                else
                                {
                                    session = _activeDisplaySession;
                                    resolvedMediaProps = props;
                                }
                            }
                            else
                            {
                                session = _activeDisplaySession;
                                resolvedMediaProps = props;
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
                if (spotifyGroundTruth == null && sessions.Any(s => s.SourceAppUserModelId?.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) == true))
                {
                    spotifyGroundTruth = _spotifyWindowTitle();
                }
                var newlyPlayingSessions = new List<GlobalSystemMediaTransportControlsSession>();
                var scanTimeUtc = DateTime.UtcNow;

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
                        if (!_sessionState.GetPlayingState(BuildSessionInstanceKey(s)))
                        {
                            newlyPlayingSessions.Add(s);
                        }
                        if (fallbackActiveSession == null)
                        {
                            fallbackActiveSession = s;
                        }

                        if (AreSameSession(s, osCurrentSession))
                        {
                            fallbackActiveSession = s;
                        }
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
                        {
                            activeDisplayProps = props;
                        }
                        if (fallbackActiveSession != null && AreSameSession(s, fallbackActiveSession))
                        {
                            fallbackActiveProps = props;
                        }

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

                        bool isDedicatedMusicAppPlaying = false;
                        if (isOsCurrent && (isBrowser || isYouTube))
                        {
                            foreach (var otherSession in sessions)
                            {
                                try
                                {
                                    var otherId = otherSession.SourceAppUserModelId ?? "";
                                    if ((otherId.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) ||
                                         otherId.Contains("Music", StringComparison.OrdinalIgnoreCase)) &&
                                        IsSessionPlayingStatus(otherSession.GetPlaybackInfo().PlaybackStatus))
                                    {
                                        isDedicatedMusicAppPlaying = true;
                                        break;
                                    }
                                }
                                catch
                                {
                                    // Ignore individual session inspection error
                                }
                            }
                        }

                        if (isActive)
                        {
                            _sessionState.SetLastPlayingTime(sessionInstanceKey, nowUtc);
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
                        if (_sessionState.TryGetLastPlayingTime(sessionInstanceKey, out var lastPlaying))
                        {
                            lastPlayingIdle = (DateTime.UtcNow - lastPlaying).TotalSeconds;
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

                        bool artistIsNonGeneric = hasArtist && props!.Artist != YouTubePlatformName && props.Artist != "Browser";

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
                string bestSessionKey = BuildSessionInstanceKey(bestSession);
                if (bestSessionKey != _pendingSessionInstanceKey)
                {
                    _pendingSessionInstanceKey = bestSessionKey;
                    _pendingSessionStartTime = DateTime.UtcNow;
                }

                string currentId = _activeDisplaySession.SourceAppUserModelId ?? "";
                bool currentIsPremium = currentId.Contains(SpotifyPlatformName, StringComparison.OrdinalIgnoreCase) ||
                                      currentId.Contains("Music", StringComparison.OrdinalIgnoreCase);

                bool isOsCurrent = AreSameSession(bestSession, osCurrentSession);
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
                                RuntimeLog.Debug(MediaSessionLogTag, $"Switching session: fresh timeline New={timelineAge:F1}s vs Current={currentTimelineAge:F1}s");
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
                    PendingElapsedSeconds = (DateTime.UtcNow - _pendingSessionStartTime).TotalSeconds,
                }))
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
            else
            {
                if (hasAnyActiveSession && fallbackActiveSession != null)
                {
                    session = fallbackActiveSession;
                    resolvedMediaProps = fallbackActiveProps;
                }
                else
                {
                    session = sessionManager.GetCurrentSession();
                }
            }
        }

        return (session, spotifyGroundTruth, resolvedMediaProps);
    }

    public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
        => _metadataLookup.TryGetYouTubeVideoIdWithInfoAsync(title, artist, ct);

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
                var titles = GetAllWindowTitles();
                if (titles != null && titles.Count > 0 &&
                    PlatformDetector.HasReliableWindowMatch(titles, info.CurrentTrack, YouTubeLowerPlatformName))
                {
                    return true;
                }
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

    private static string ExtractVideoTitle(string windowTitle, string platform)
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
            var pinned = Volatile.Read(ref _pinnedSession);
            if (pinned != null) return pinned.Session.SourceAppUserModelId ?? "";
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
        // Mark the service disposed immediately; let the dispatcher keep running
        // while workers finish cancellation and release their resources.
        _ = ObserveShutdownAsync(GetDisposalTask());
    }

    public ValueTask DisposeAsync() => new(GetDisposalTask());
    private Task _workerResourceReleaseTask = Task.CompletedTask;

    internal async Task DrainShutdownAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        await _workerResourceReleaseTask.ConfigureAwait(false);
    }

    private Task GetDisposalTask()
    {
        lock (_lifecycleLock)
            return _disposeTask ??= StopCoreAsync(isDisposing: true);
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Warn(MediaStopLogTag, $"Shutdown failed: {ex}"); }
    }

    private async Task StopCoreAsync(bool isDisposing)
    {
        CancellationTokenSource? ctsToCancel;
        Task allTasks;
        GlobalSystemMediaTransportControlsSessionManager? sessionManagerToUnsub;

        lock (_lifecycleLock)
        {
            if (_state == ServiceLifecycleState.Disposed || (!isDisposing && _state == ServiceLifecycleState.Stopped))
                return;

            _state = isDisposing ? ServiceLifecycleState.Disposed : ServiceLifecycleState.Stopping;
            if (isDisposing)
                _disposed = true;

            unchecked { _lifecycleGeneration++; }

            ctsToCancel = _bgCts;
            _bgCts = null;
            if (isDisposing) _pinReconnect.Dispose();
            else _pinReconnect.Reset();

            var tasksToWait = new List<Task>();
            if (!_workerDrainTask.IsCompleted) tasksToWait.Add(_workerDrainTask);
            if (_initTask != null) tasksToWait.Add(_initTask);
            if (_processingTask != null) tasksToWait.Add(_processingTask);
            if (_heartbeatTask != null) tasksToWait.Add(_heartbeatTask);
            if (_stagedRefreshTask != null) tasksToWait.Add(_stagedRefreshTask);
            allTasks = _workerDrainTask = Task.WhenAll(tasksToWait);

            _initTask = null;
            _processingTask = null;
            _heartbeatTask = null;
            _stagedRefreshTask = null;

            sessionManagerToUnsub = _sessionManager;
            _sessionManager = null;
        }

        // 1. Cancel background token
        try
        {
            if (ctsToCancel != null) await ctsToCancel.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(MediaStopLogTag, $"CTS cancel failed: {ex.Message}");
        }

        // 2. Wait for running tasks to complete
        if (!allTasks.IsCompletedSuccessfully)
        {
            try
            {
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                var completed = await Task.WhenAny(allTasks, timeoutTask).ConfigureAwait(false);
                if (completed != allTasks)
                {
                    RuntimeLog.Warn(MediaStopLogTag, "Timed out waiting for background tasks to exit.");
                }
                else
                {
                    await allTasks.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(MediaStopLogTag, $"Error waiting for tasks: {ex.Message}");
            }
        }

        // 3. Unsubscribe from session & session manager
        Interlocked.Exchange(ref _pinnedSession, null);
        _pinReconnect.Reset();
        _volumeService.InvalidateVolumeSessionCache();
        UnsubscribeFromSession();
        if (sessionManagerToUnsub != null)
        {
            try
            {
                sessionManagerToUnsub.CurrentSessionChanged -= OnSessionChanged;
                sessionManagerToUnsub.SessionsChanged -= OnSessionsChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(MediaStopLogTag, $"Failed unsubscribing session manager: {ex.Message}");
            }
        }

        // 4. Dispose CTS
        // A timed-out worker may still use its token and release the update lock.
        // Release those objects only after the captured workers actually exit.
        _workerResourceReleaseTask = ReleaseWorkerResourcesAsync(allTasks, ctsToCancel, isDisposing);

        // 5. Complete the channel or drain it for a later restart.
        _sessionState.Clear();
        CancelAndClearThumbnailCts();
        if (isDisposing)
        {
            _changes.Complete();
        }
        else
        {
            // Drain stale items from channel so restart is clean
            _changes.Clear();

            lock (_lifecycleLock)
            {
                if (_state == ServiceLifecycleState.Stopping)
                {
                    _state = ServiceLifecycleState.Stopped;
                }
            }
        }
    }

    private async Task ReleaseWorkerResourcesAsync(Task workers, CancellationTokenSource? cts, bool isDisposing)
    {
        try { await workers.ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Debug(MediaStopLogTag, $"Worker exited: {ex.Message}"); }
        finally
        {
            cts?.Dispose();
            if (isDisposing)
            {
                _volumeService.Dispose();
                _updateLock.Dispose();
            }
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
public enum ChangeTypes
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
