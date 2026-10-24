using System;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed class FullscreenAutoHideController : IDisposable
{
    private static readonly TimeSpan FullscreenStateCooldown = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(50);
    private const int PollIntervalMs = 300;

    private readonly Func<IntPtr> _getNotchHwnd;
    private readonly Func<bool>? _isAutoHideSuppressed;
    private NotchSettings _settings;
    private readonly UiScheduledTimer _pollTimer;

    private DateTime _lastCheckUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.MinValue;
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

        _pollTimer = new UiScheduledTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        SyncTimerState();
    }

    private void SyncTimerState()
    {
        if (_disposed) return;
        if (_settings.HideOnExclusiveFullscreen || _settings.HideOnWindowedFullscreen)
        {
            _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
        }
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Evaluate(default, force: false);
    }

    public void UpdateSettings(NotchSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
                _lastStateChangeUtc = DateTime.UtcNow;
                HideStateChanged?.Invoke(false);
                return true;
            }
            return false;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastCheckUtc) < ThrottleInterval)
            return false;

        _lastCheckUtc = now;

        var targetHwnd = foregroundHwnd == IntPtr.Zero ? GetForegroundWindow() : foregroundHwnd;
        bool shouldHide = ShouldHideForFullscreen(targetHwnd, notchHwnd);

        if (shouldHide == _isHiddenByFullscreen)
            return false;

        if (!force && shouldHide && (now - _lastStateChangeUtc) < FullscreenStateCooldown)
        {
            RecheckNeeded?.Invoke();
            return false;
        }

        _isHiddenByFullscreen = shouldHide;
        _lastStateChangeUtc = now;
        HideStateChanged?.Invoke(shouldHide);
        return true;
    }

    public void Reset()
    {
        if (_isHiddenByFullscreen)
        {
            _isHiddenByFullscreen = false;
            _lastStateChangeUtc = DateTime.UtcNow;
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

        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _pollTimer.Dispose();
    }
}
