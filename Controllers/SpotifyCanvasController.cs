using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class SpotifyCanvasController : IDisposable
{
    private const string LogTag = "SPOTIFY-CANVAS-CONTROLLER";

    private readonly SpotifyCanvasService _service;
    private readonly ISpotifyCanvasPresenter _presenter;
    private readonly Action<Action> _runOnUi;

    private readonly ConcurrentDictionary<Task, byte> _activeTasks = new();

    private string _currentTrackKey = "";
    private string _currentTrack = "";
    private string _currentArtist = "";
    private TimeSpan _currentDuration = TimeSpan.Zero;
    private bool _isSpotifyPlatform;

    private Uri? _currentUri;
    private CancellationTokenSource? _fetchCts;
    private Task? _inFlightTask;
    private long _activeRequestId;
    private bool _lookupCompleted;
    private bool _isSurfaceVisible;
    private bool _shouldPlay;
    private bool _disposed;

    private bool _enabled = true;
    private string _spDc = "";
    private double _brightness = 0.7;
    private bool _localOnlyMode;
    private bool _blurFallbackEnabled = true;
    private bool _canFadeIn;
    private bool _isLyricsActive;

    public string CurrentTrackKey => _currentTrackKey;
    public Uri? CurrentUri => _currentUri;
    public long CurrentRequestId => _activeRequestId;
    public bool IsSurfaceVisible => _isSurfaceVisible;
    public bool IsLookupCompleted => _lookupCompleted;
    public bool HasPendingFetch => _fetchCts != null;

    public SpotifyCanvasController(
        SpotifyCanvasService service,
        ISpotifyCanvasPresenter presenter,
        Action<Action> runOnUi)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _runOnUi = runOnUi ?? throw new ArgumentNullException(nameof(runOnUi));

        _presenter.MediaFailed += (_, _) =>
        {
            _currentUri = null;
            _lookupCompleted = true;
        };
    }

    public void UpdateSettings(bool enabled, string? spDc, double brightness, bool localOnlyMode)
    {
        if (_disposed) return;
        bool wasDisabled = !_enabled || _localOnlyMode;
        _enabled = enabled;
        _spDc = spDc ?? "";
        _brightness = brightness;
        _localOnlyMode = localOnlyMode;

        NotifyPresentationChanged();

        if (!_enabled || _localOnlyMode)
        {
            Reset();
        }
        else if (wasDisabled && _isSpotifyPlatform && !string.IsNullOrEmpty(_currentTrackKey))
        {
            RefreshForCurrentTrack();
        }
    }

    public void UpdatePresentationContext(bool canFadeIn, bool blurFallbackEnabled, bool isLyricsActive)
    {
        if (_disposed) return;
        _canFadeIn = canFadeIn;
        _blurFallbackEnabled = blurFallbackEnabled;
        _isLyricsActive = isLyricsActive;
        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        var options = new SpotifyCanvasPresentationOptions(
            _brightness,
            _canFadeIn,
            _blurFallbackEnabled,
            _isLyricsActive);
        _runOnUi(() => _presenter.ApplyPresentation(options));
    }

    public void UpdateTrack(MediaInfo? info)
    {
        if (info == null)
        {
            _isSpotifyPlatform = false;
            Reset();
            return;
        }

        UpdateTrack(info.CurrentTrack, info.CurrentArtist, info.Duration, info.Platform, info.IsPlaying);
    }

    public void UpdateTrack(string? track, string? artist, TimeSpan duration, MediaPlatform platform, bool isPlaying)
    {
        if (_disposed) return;
        _shouldPlay = platform == MediaPlatform.Spotify && isPlaying;
        _isSpotifyPlatform = platform == MediaPlatform.Spotify;

        if (!_isSpotifyPlatform || string.IsNullOrWhiteSpace(track) || !_enabled || _localOnlyMode)
        {
            Reset();
            return;
        }

        string trackKey = $"{track}|{artist}";
        if (trackKey == _currentTrackKey)
        {
            // Same track: update playback state on presenter without resetting video source or fade
            _runOnUi(() =>
            {
                _presenter.SetPlaybackState(_shouldPlay);
                if (_currentUri != null && _isSurfaceVisible)
                {
                    _presenter.SetSource(_currentUri, _activeRequestId, _shouldPlay);
                }
            });
            return;
        }

        _currentTrackKey = trackKey;
        _currentTrack = track;
        _currentArtist = artist ?? "";
        _currentDuration = duration;
        _currentUri = null;
        _lookupCompleted = false;

        CancelFetch(retryWhenVisible: false);
        _runOnUi(() => _presenter.Hide(clearSource: true));

        TryStartFetch();
    }

    public void UpdatePlaybackState(bool isPlaying)
    {
        if (_disposed) return;
        _shouldPlay = _isSpotifyPlatform && isPlaying;
        _runOnUi(() => _presenter.SetPlaybackState(_shouldPlay));
    }

    public void SetSurfaceVisibility(bool isVisible)
    {
        if (_disposed) return;
        _isSurfaceVisible = isVisible;
        if (isVisible)
        {
            if (_currentUri != null)
            {
                _runOnUi(() => _presenter.SetSource(_currentUri, _activeRequestId, _shouldPlay));
            }
            else if (!_lookupCompleted && _fetchCts == null && !string.IsNullOrEmpty(_currentTrackKey))
            {
                TryStartFetch();
            }
            else
            {
                _runOnUi(() => _presenter.FadeInBackgroundIfReady());
            }
        }
        else
        {
            CancelFetch(retryWhenVisible: true);
            _runOnUi(() => _presenter.Hide(clearSource: true, restoreFallback: false));
        }
    }

    public void RefreshForCurrentTrack()
    {
        if (_disposed) return;
        if (!_isSpotifyPlatform || string.IsNullOrWhiteSpace(_currentTrack))
        {
            Reset();
            return;
        }

        CancelFetch(retryWhenVisible: false);
        _currentUri = null;
        _lookupCompleted = false;
        _runOnUi(() => _presenter.Hide(clearSource: true));

        TryStartFetch();
    }

    public void Reset()
    {
        CancelFetch(retryWhenVisible: false);
        _currentTrackKey = "";
        _currentTrack = "";
        _currentArtist = "";
        _currentDuration = TimeSpan.Zero;
        _currentUri = null;
        _lookupCompleted = false;

        _runOnUi(() => _presenter.Hide(clearSource: true));
    }

    public void ClearCache()
    {
        _service.ClearCache();
    }

    private void TryStartFetch()
    {
        if (_disposed ||
            !_enabled ||
            _localOnlyMode ||
            !_isSpotifyPlatform ||
            !_isSurfaceVisible ||
            _lookupCompleted ||
            _fetchCts != null ||
            string.IsNullOrEmpty(_currentTrackKey))
        {
            return;
        }

        long requestId = ++_activeRequestId;
        var requestCts = new CancellationTokenSource();
        _fetchCts = requestCts;
        CancellationToken token = requestCts.Token;

        string trackKey = _currentTrackKey;
        string track = _currentTrack;
        string artist = _currentArtist;
        TimeSpan duration = _currentDuration;
        string spDc = _spDc;

        var task = ExecuteFetchAsync(requestId, trackKey, track, artist, duration, spDc, requestCts, token);
        _inFlightTask = task;
        _activeTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => _activeTasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task ExecuteFetchAsync(
        long requestId,
        string trackKey,
        string track,
        string artist,
        TimeSpan duration,
        string spDc,
        CancellationTokenSource requestCts,
        CancellationToken token)
    {
        try
        {
            Uri? canvasUri = await _service.FetchCanvasAsync(
                track,
                artist,
                duration,
                spDc,
                token).ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            _runOnUi(() =>
            {
                if (_disposed || token.IsCancellationRequested || requestId != _activeRequestId || trackKey != _currentTrackKey)
                {
                    return;
                }

                _currentUri = canvasUri;
                _lookupCompleted = true;

                if (canvasUri != null)
                {
                    RuntimeLog.Debug(LogTag, $"Canvas ready for current lyrics view (request #{requestId})");
                    if (_isSurfaceVisible)
                    {
                        _presenter.SetSource(canvasUri, requestId, _shouldPlay);
                    }
                }
                else
                {
                    RuntimeLog.Debug(LogTag, "No Canvas available; keeping normal lyrics background");
                    _presenter.RestoreBlurFallback();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation on track change / hide
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"Canvas fetch failed: {ex.Message}");
            _runOnUi(() =>
            {
                if (!_disposed && !token.IsCancellationRequested && requestId == _activeRequestId && trackKey == _currentTrackKey)
                {
                    _currentUri = null;
                    _lookupCompleted = true;
                    _presenter.RestoreBlurFallback();
                }
            });
        }
        finally
        {
            _runOnUi(() =>
            {
                if (ReferenceEquals(_fetchCts, requestCts))
                {
                    _fetchCts = null;
                }
                requestCts.Dispose();
            });
        }
    }

    private void CancelFetch(bool retryWhenVisible)
    {
        var pending = _fetchCts;
        _fetchCts = null;
        if (pending == null) return;

        if (retryWhenVisible)
        {
            _lookupCompleted = false;
        }

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelFetch(retryWhenVisible: false);
        _runOnUi(() => _presenter.Dispose());

        _inFlightTask = null;

        var pendingTasks = _activeTasks.Keys.Where(t => !t.IsCompleted).ToArray();
        if (pendingTasks.Length > 0)
        {
            _ = Task.WhenAll(pendingTasks).ContinueWith(_ =>
            {
                try
                {
                    _service.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Debug(LogTag, () => $"Service disposal error: {ex.Message}");
                }
            }, TaskScheduler.Default);
        }
        else
        {
            _service.Dispose();
        }
    }
}
