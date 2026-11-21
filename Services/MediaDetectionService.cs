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
    internal SemaphoreSlim UpdateLock => _updateLock;
    internal MediaTimelineSimulator Timeline => _timelineSimulator;
    internal MediaPinReconnectWindow PinReconnect => _pinReconnect;
    internal CancellationTokenSource? ThumbnailCancellation => _thumbCts;
    internal void SetSessionManagerForTest(GlobalSystemMediaTransportControlsSessionManager manager) => _sessionManager = manager;
    internal GlobalSystemMediaTransportControlsSession? ActiveDisplaySession => _activeDisplaySession;

    internal void SetRunningWorkerForTest(Task? worker, CancellationTokenSource? lifetime)
    {
        _bgCts = lifetime;
        _processingTask = worker;
        _state = ServiceLifecycleState.Running;
    }

    internal void SetRecentBrowserMediaForTest(DateTime observedAt)
    {
        _lastSource = "Browser";
        _emptyMetadataStartTime = observedAt;
    }

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
    private int _deferredThumbRefreshGeneration = -1;
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
    private const int MaxSpotifyArtistCacheEntries = 512;
    private readonly ConcurrentDictionary<string, string> _spotifyArtistCache = new(StringComparer.OrdinalIgnoreCase);

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
                SyncTitleMonitorTarget();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ProcessError", ex.Message);
            }
        }
    }

    private NativeWindowTitleMonitor? _titleMonitor;
    private int _lastTitleNotificationsState = -1;

    private void SyncTitleMonitorTarget()
    {
        if (_windowTitleScanner is not WindowTitleScanner scanner) return;
        var monitor = Volatile.Read(ref _titleMonitor);
        if (monitor == null) return;
        bool active = monitor.UpdateSource(GetActiveSourceAppId(), out bool changed);
        int nextState = active ? 1 : 0;
        if (changed || Interlocked.Exchange(ref _lastTitleNotificationsState, nextState) != nextState)
        {
            Volatile.Write(ref _lastTitleNotificationsState, nextState);
            scanner.SetTitleNotificationsActive(active);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var scanner = _windowTitleScanner as WindowTitleScanner;
        using var titleChanges = scanner == null ? null : new NativeWindowTitleMonitor(() =>
        {
            if (ct.IsCancellationRequested) return;
            scanner?.InvalidateWindowTitles();
            _windowTitleScanner.InvalidateUrlCaches();
            Interlocked.Exchange(ref _lastEventTimeTicks, DateTime.UtcNow.Ticks);
            _changes.Enqueue(ChangeTypes.MediaProperties);
        });
        Volatile.Write(ref _lastTitleNotificationsState, -1);
        Volatile.Write(ref _titleMonitor, titleChanges);
        SyncTitleMonitorTarget();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SyncTitleMonitorTarget();
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
        finally
        {
            Volatile.Write(ref _titleMonitor, null);
            Volatile.Write(ref _lastTitleNotificationsState, -1);
            scanner?.SetTitleNotificationsActive(false);
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

    internal async Task UpdateMediaInfoAsync(bool forceRefresh, CancellationToken ct)
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

            await PublishMediaSnapshotAsync(info, forceRefresh, ct);
        }
        finally
        {
            _updateLock.Release();
        }
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
