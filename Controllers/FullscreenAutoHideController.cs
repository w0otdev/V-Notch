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
    private NotchSettings _settings;
    private readonly DispatcherTimer _pollTimer;

    private DateTime _lastCheckUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.MinValue;
    private bool _isHiddenByFullscreen;
    private bool _disposed;

    public bool IsHiddenByFullscreen => _isHiddenByFullscreen;

    public event Action<bool>? HideStateChanged;

    public event Action? RecheckNeeded;

    public FullscreenAutoHideController(Func<IntPtr> getNotchHwnd, NotchSettings settings)
    {
        _getNotchHwnd = getNotchHwnd ?? throw new ArgumentNullException(nameof(getNotchHwnd));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Evaluate(default, force: false);
    }

    public void UpdateSettings(NotchSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Evaluate(default, force: true);
    }

    public bool Evaluate(IntPtr foregroundHwnd = default, bool force = false)
    {
        IntPtr notchHwnd = _getNotchHwnd();
        if (notchHwnd == IntPtr.Zero) return false;

        if (!_settings.HideOnExclusiveFullscreen && !_settings.HideOnWindowedFullscreen)
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

    private bool ShouldHideForFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        IntPtr notchMonitor = FullscreenDetector.GetWindowMonitor(notchHwnd);

        if (hwnd == notchHwnd)
            return CheckFullscreenBehind(notchHwnd, notchMonitor, notchHwnd);

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint fgPid);
            GetWindowThreadProcessId(notchHwnd, out uint myPid);
            if (fgPid == myPid)
                return CheckFullscreenBehind(hwnd, notchMonitor, notchHwnd);
        }

        return IsForegroundWindowFullscreen(hwnd, notchMonitor, notchHwnd);
    }

    private bool CheckFullscreenBehind(IntPtr startHwnd, IntPtr notchMonitor, IntPtr notchHwnd)
    {
        var next = Win32Interop.GetWindow(startHwnd, GW_HWNDNEXT);
        const int maxWalk = 24;
        GetWindowThreadProcessId(notchHwnd, out uint myPid);

        for (int i = 0; i < maxWalk && next != IntPtr.Zero; i++)
        {
            if (IsWindowVisible(next) && !IsIconic(next))
            {
                GetWindowThreadProcessId(next, out uint nextPid);
                if (nextPid != myPid)
                {
                    var type = FullscreenDetector.DetectFullscreenType(next, notchHwnd, notchMonitor);
                    if (type != FullscreenType.None)
                        return ShouldHideForType(type);
                }
            }
            next = Win32Interop.GetWindow(next, GW_HWNDNEXT);
        }
        return false;
    }

    private bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchMonitor, IntPtr notchHwnd)
    {
        var type = FullscreenDetector.DetectFullscreenType(hwnd, notchHwnd, notchMonitor);
        return ShouldHideForType(type);
    }

    private bool ShouldHideForType(FullscreenType type) => type switch
    {
        FullscreenType.ExclusiveFullscreen => _settings.HideOnExclusiveFullscreen,
        FullscreenType.WindowedFullscreen => _settings.HideOnWindowedFullscreen,
        _ => false
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
    }
}
