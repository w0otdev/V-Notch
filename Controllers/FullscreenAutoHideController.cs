using System;
using System.Threading;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed class FullscreenAutoHideController : IDisposable
{
    private const long FullscreenStateCooldownMs = 180;
    private const long ThrottleIntervalMs = 50;
    private const int PollIntervalMs = 300;

    private readonly Func<IntPtr> _getNotchHwnd;
    private readonly Func<bool>? _isAutoHideSuppressed;
    private NotchSettings _settings;
    // Window queries run on a thread-pool timer so a slow Win32 call never stalls the UI thread.
    // The dispatcher is only woken when the sampled state differs from the applied one.
    private readonly Timer _pollTimer;
    private readonly Dispatcher _dispatcher;
    private readonly Action _applyPolledState;
    private int _pollRunning;
    private int _wakePending;
    private bool _pollActive;

    private long _lastCheckTick;
    private long _lastStateChangeTick;
    private bool _isHiddenByFullscreen;
    private bool _disposed;

    public bool IsHiddenByFullscreen => _isHiddenByFullscreen;

    public event Action<bool>? HideStateChanged;

    public event Action? RecheckNeeded;

    public FullscreenAutoHideController(Func<IntPtr> getNotchHwnd, NotchSettings settings,
        Func<bool>? isAutoHideSuppressed = null)
    {
        _getNotchHwnd = getNotchHwnd ?? throw new ArgumentNullException(nameof(getNotchHwnd));
        _isAutoHideSuppressed = isAutoHideSuppressed;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _dispatcher = Dispatcher.CurrentDispatcher;
        _applyPolledState = ApplyPolledState;
        _pollTimer = new Timer(PollTimer_Tick, null, Timeout.Infinite, Timeout.Infinite);
        SyncTimerState();
    }

    private void SyncTimerState()
    {
        if (_disposed) return;
        bool enabled = _settings.HideOnExclusiveFullscreen || _settings.HideOnWindowedFullscreen;
        if (enabled == _pollActive) return;
        _pollActive = enabled;
        _pollTimer.Change(enabled ? PollIntervalMs : Timeout.Infinite, enabled ? PollIntervalMs : Timeout.Infinite);
    }

    // Thread-pool thread: no UI state is touched except lock-free reads of values the UI thread owns.
    private void PollTimer_Tick(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _pollRunning, 1) != 0) return;
        try
        {
            var settings = Volatile.Read(ref _settings);
            IntPtr notchHwnd = _getNotchHwnd();
            if (notchHwnd == IntPtr.Zero) return;
            bool hidden = Volatile.Read(ref _isHiddenByFullscreen);
            // While hidden and an auto-hide suppressor exists, the UI re-evaluates (the suppressor lives on the UI thread).
            bool wake = hidden && _isAutoHideSuppressed != null;
            if (!wake)
            {
                IntPtr foreground = GetForegroundWindow();
                wake = FullscreenDetector.ShouldHideForFullscreen(
                    foreground, notchHwnd, settings.HideOnExclusiveFullscreen, settings.HideOnWindowedFullscreen) != hidden;
            }
            if (!wake || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _wakePending, 1) != 0) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, _applyPolledState);
        }
        catch (Exception)
        {
            // Sampling is best effort; the next tick retries.
        }
        finally
        {
            Volatile.Write(ref _pollRunning, 0);
        }
    }

    private void ApplyPolledState()
    {
        Volatile.Write(ref _wakePending, 0);
        if (_disposed) return;
        Evaluate(default, force: false);
    }

    public void UpdateSettings(NotchSettings settings)
    {
        Volatile.Write(ref _settings, settings ?? throw new ArgumentNullException(nameof(settings)));
        SyncTimerState();
        Evaluate(default, force: true);
    }

    public bool Evaluate(IntPtr foregroundHwnd = default, bool force = false)
    {
        IntPtr notchHwnd = _getNotchHwnd();
        if (notchHwnd == IntPtr.Zero) return false;

        if (_isAutoHideSuppressed?.Invoke() == true ||
            (!_settings.HideOnExclusiveFullscreen && !_settings.HideOnWindowedFullscreen))
        {
            if (_isHiddenByFullscreen)
            {
                _isHiddenByFullscreen = false;
                _lastStateChangeTick = Environment.TickCount64;
                HideStateChanged?.Invoke(false);
                return true;
            }
            return false;
        }

        long nowTick = Environment.TickCount64;
        if (!force && _lastCheckTick != 0 && (nowTick - _lastCheckTick) < ThrottleIntervalMs)
            return false;

        _lastCheckTick = nowTick;

        var targetHwnd = foregroundHwnd == IntPtr.Zero ? GetForegroundWindow() : foregroundHwnd;
        bool shouldHide = ShouldHideForFullscreen(targetHwnd, notchHwnd);

        if (shouldHide == _isHiddenByFullscreen)
            return false;

        if (!force && shouldHide && _lastStateChangeTick != 0 && (nowTick - _lastStateChangeTick) < FullscreenStateCooldownMs)
        {
            RecheckNeeded?.Invoke();
            return false;
        }

        _isHiddenByFullscreen = shouldHide;
        _lastStateChangeTick = nowTick;
        HideStateChanged?.Invoke(shouldHide);
        return true;
    }

    public void Reset()
    {
        if (_isHiddenByFullscreen)
        {
            _isHiddenByFullscreen = false;
            _lastStateChangeTick = Environment.TickCount64;
            HideStateChanged?.Invoke(false);
        }
    }

    private bool ShouldHideForFullscreen(IntPtr hwnd, IntPtr notchHwnd) =>
        FullscreenDetector.ShouldHideForFullscreen(
            hwnd, notchHwnd,
            _settings.HideOnExclusiveFullscreen,
            _settings.HideOnWindowedFullscreen);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollActive = false;
        _pollTimer.Dispose();
    }
}
