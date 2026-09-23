using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    private static readonly TimeSpan DesktopDemotionDelay = TimeSpan.FromMilliseconds(220);

    private bool _isDesktopEdgePromoted = true;
    private bool _desktopPromotionPending;
    private bool _desktopDemotionPending;
    private int _desktopRevealAnimationVersion;
    private DispatcherTimer? _desktopDemotionDelayTimer;
    private EventHandler? _desktopTransparentFrameHandler;
    private int _desktopTransparentFramesObserved;
    private bool _desktopPointerInHoverZone;
    private DateTime _startupHoldUntilUtc = DateTime.MinValue;
    private DispatcherTimer? _startupHoldTimer;
    private bool ShouldStayOnDesktopLayer =>
        _settings.StayBehindWindows && !_isDesktopEdgePromoted;

    private void HoverService_MousePositionChangedForDesktopReveal(object? sender, Point point)
    {
        if (!_settings.StayBehindWindows || !_isNotchVisible || _isHiddenByFullscreen)
            return;

        bool atRevealEdge = _notchManager.HoverService.IsPointInTopEdgeRevealZone(point);
        bool inNotchHoverZone = _notchManager.HoverService.IsPointInHoverZone(point);
        bool ownedWindowInteractionActive = HasActiveOwnedWindowInteraction();
        _desktopPointerInHoverZone = inNotchHoverZone;
        bool interactionActive = IsDesktopNotchInteractionActive();

        if (atRevealEdge || ownedWindowInteractionActive)
        {
            CancelScheduledDesktopDemotion();
            PromoteFromDesktopLayer();
        }
        else if (_desktopPromotionPending && !interactionActive)
        {
            // Allow pointer to move into notch once armed, avoiding frame
            // alternation from immediately cancelling outside the 3px edge strip.
            CancelPendingDesktopPromotion();
        }
        else if (_isDesktopEdgePromoted && !interactionActive)
        {
            ScheduleDesktopLayerDemotion();
        }
        else if (_isDesktopEdgePromoted && interactionActive)
        {
            CancelScheduledDesktopDemotion();
            if (_desktopDemotionPending) CancelDesktopLayerDemotion();
        }
    }

    private bool IsDesktopNotchInteractionActive()
    {
        if (IsStartupHoldActive(_isGreetingActive, _startupHoldUntilUtc, DateTime.UtcNow))
            return true;

        bool pointerOverNotch = NotchWrapper?.IsMouseOver == true || IsCursorInsideNotchVisual();
        bool inputCapturedWithin = IsMouseCaptureWithin
                                   || IsStylusCaptureWithin
                                   || AreAnyTouchesCapturedWithin;
        bool ownedWindowInteractionActive = HasActiveOwnedWindowInteraction();
        bool keyboardFocusActive = HasActiveKeyboardFocusInteraction();

        return ShouldKeepDesktopPromotion(
            _desktopPointerInHoverZone,
            pointerOverNotch,
            inputCapturedWithin,
            keyboardFocusActive,
            ownedWindowInteractionActive);
    }

    private bool HasActiveKeyboardFocusInteraction() =>
        DetermineActiveKeyboardFocusInteraction(
            _isExpanded || _isMusicExpanded,
            IsWindowOrOwnedWindowForeground(),
            _overlayWindow.IsKeyboardInputEnabled,
            System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or
            System.Windows.Controls.PasswordBox);

    internal static bool DetermineActiveKeyboardFocusInteraction(
        bool isExpandedOrMusicExpanded,
        bool isForeground,
        bool isKeyboardInputEnabled,
        bool isTextBoxFocused)
    {
        if (!isExpandedOrMusicExpanded)
            return false;

        if (!isForeground)
            return false;

        return isKeyboardInputEnabled || isTextBoxFocused;
    }

    private bool IsWindowOrOwnedWindowForeground()
    {
        IntPtr fg = Win32Interop.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (_hwnd != IntPtr.Zero && fg == _hwnd) return true;

        foreach (Window owned in OwnedWindows)
        {
            if (owned.IsVisible)
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(owned);
                if (helper.Handle == fg) return true;
            }
        }
        return false;
    }

    private bool HasActiveOwnedWindowInteraction()
    {
        foreach (Window ownedWindow in OwnedWindows)
        {
            if (IsWindowInteractionActive(ownedWindow))
                return true;
        }

        return false;
    }

    private static bool IsWindowInteractionActive(Window window)
    {
        if (!window.IsVisible)
            return false;

        if (window.IsActive
            || window.IsMouseOver
            || window.IsMouseCaptureWithin
            || window.IsStylusCaptureWithin
            || window.AreAnyTouchesCapturedWithin
            || window.IsKeyboardFocusWithin)
        {
            return true;
        }

        foreach (Window ownedWindow in window.OwnedWindows)
        {
            if (IsWindowInteractionActive(ownedWindow))
                return true;
        }

        return false;
    }

    internal static bool ShouldKeepDesktopPromotion(
        bool pointerInHoverZone,
        bool pointerOverNotch,
        bool inputCapturedWithin,
        bool keyboardFocusWithin,
        bool ownedWindowInteractionActive)
    {
        return pointerInHoverZone
               || pointerOverNotch
               || inputCapturedWithin
               || keyboardFocusWithin
               || ownedWindowInteractionActive;
    }

    internal static bool IsStartupHoldActive(bool isGreetingActive, DateTime startupHoldUntilUtc, DateTime nowUtc)
    {
        return isGreetingActive || nowUtc < startupHoldUntilUtc;
    }

    internal void StartStartupHold(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;

        _startupHoldUntilUtc = DateTime.UtcNow + duration;
        _startupHoldTimer?.Stop();
        _startupHoldTimer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) =>
        {
            _startupHoldTimer?.Stop();
            _startupHoldTimer = null;
            if (_settings.StayBehindWindows && _isDesktopEdgePromoted && !IsDesktopNotchInteractionActive())
            {
                ScheduleDesktopLayerDemotion();
            }
        }, Dispatcher);
        _startupHoldTimer.Start();
    }

    private void ScheduleDesktopLayerDemotion()
    {
        if (_desktopDemotionPending) return;

        _desktopDemotionDelayTimer ??= new DispatcherTimer(
            DesktopDemotionDelay,
            DispatcherPriority.Background,
            (_, _) =>
            {
                _desktopDemotionDelayTimer?.Stop();
                if (_cleanedUp || !_isDesktopEdgePromoted) return;

                // Check live WPF input state before demotion so active panels, drags,
                // or fields do not disappear behind foreground applications.
                if (IsDesktopNotchInteractionActive() || _isAnimating || _isExpanded || _isMusicExpanded) return;

                DemoteToDesktopLayerWithFade();
            },
            Dispatcher);

        if (!_desktopDemotionDelayTimer.IsEnabled)
            _desktopDemotionDelayTimer.Start();
    }

    private void CancelScheduledDesktopDemotion()
    {
        _desktopDemotionDelayTimer?.Stop();
    }

    private long _lastNotchObscuredCheckTicks;
    private bool _lastNotchObscuredResult;

    private bool IsNotchObscuredByAnyWindow()
    {
        if (_hwnd == IntPtr.Zero) return false;

        long now = Environment.TickCount64;
        if (now - _lastNotchObscuredCheckTicks < 250)
            return _lastNotchObscuredResult;

        _lastNotchObscuredCheckTicks = now;

        if (!GetWindowRect(_hwnd, out var notchRect))
            return false;

        if (notchRect.Right <= notchRect.Left || notchRect.Bottom <= notchRect.Top)
            return false;

        GetWindowThreadProcessId(_hwnd, out uint myPid);

        bool isObscured = false;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == _hwnd) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == myPid) return true;

            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || FullscreenDetector.IsWindowCloaked(hwnd))
                return true;

            if (FullscreenDetector.IsBlockedClass(hwnd))
                return true;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                return true;

            if ((exStyle & WS_EX_TRANSPARENT) != 0)
                return true;

            if (FullscreenDetector.TryGetWindowBounds(hwnd, out var winRect) &&
                winRect.Left < notchRect.Right &&
                winRect.Right > notchRect.Left &&
                winRect.Top < notchRect.Bottom &&
                winRect.Bottom > notchRect.Top)
            {
                isObscured = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        _lastNotchObscuredResult = isObscured;
        return isObscured;
    }

    private void PromoteFromDesktopLayer()
    {
        if (_isDesktopEdgePromoted)
        {
            if (_desktopDemotionPending) CancelDesktopLayerDemotion();
            return;
        }

        if (_desktopPromotionPending) return;

        // If the notch is not obscured by any window (e.g. on Desktop), promote
        // immediately without dropping opacity to 0 or playing a fade animation.
        if (!IsNotchObscuredByAnyWindow())
        {
            _desktopPromotionPending = false;
            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = true;
            ConfigureOverlayWindow();
            SetDesktopRevealOpacityImmediate(1);
            return;
        }

        // Render a transparent frame before promotion to prevent DWM from
        // exposing the previous opaque surface as a bright flash.
        _desktopPromotionPending = true;
        _desktopDemotionPending = false;
        SetDesktopRevealOpacityImmediate(0);
        _desktopTransparentFramesObserved = 0;

        // Wait for composition frame and commit transparent frame before altering z-order
        _desktopTransparentFrameHandler = (_, _) =>
        {
            // Wait for subsequent composition callback to guarantee transparent frame
            // submission to DWM before promoting, preventing one-frame flashes.
            if (++_desktopTransparentFramesObserved < 2)
                return;

            if (_desktopTransparentFrameHandler != null)
            {
                CompositionTarget.Rendering -= _desktopTransparentFrameHandler;
                _desktopTransparentFrameHandler = null;
            }

            if (!_desktopPromotionPending || _cleanedUp) return;

            _desktopPromotionPending = false;
            _isDesktopEdgePromoted = true;

            // Update HWND layer after transparent frame commit, maintaining
            // WS_EX_TOPMOST without exposing stale opaque surfaces.
            ConfigureOverlayWindow();
            AnimateDesktopRevealOpacity(1, 320, null);
        };
        CompositionTarget.Rendering += _desktopTransparentFrameHandler;
    }

    private void DemoteToDesktopLayerWithFade()
    {
        if (_desktopDemotionPending) return;
        CancelScheduledDesktopDemotion();

        // If the notch is not obscured by any window (e.g. on Desktop), demote
        // directly without fading opacity out to 0 and back to 1.
        if (!IsNotchObscuredByAnyWindow())
        {
            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = false;
            ConfigureOverlayWindow();
            SetDesktopRevealOpacityImmediate(1);
            return;
        }

        _desktopDemotionPending = true;

        AnimateDesktopRevealOpacity(0, 240, () =>
        {
            if (!_desktopDemotionPending) return;

            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = false;
            // The fade has completely finished, so changing the HWND layer cannot
            // expose a partially rendered frame.
            ConfigureOverlayWindow();

            // Offload DWM flush to a background task so UI thread is never blocked.
            _ = Task.Run(() =>
            {
                DwmFlush();
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (!_isDesktopEdgePromoted)
                    {
                        SetDesktopRevealOpacityImmediate(1);
                    }
                }));
            });
        });
    }

    private void CancelDesktopLayerDemotion()
    {
        _desktopDemotionPending = false;
        AnimateDesktopRevealOpacity(1, 220, null);
        EnsureTopmost(force: true);
    }

    private void CancelPendingDesktopPromotion()
    {
        if (!_desktopPromotionPending) return;

        _desktopPromotionPending = false;
        DetachDesktopTransparentFrameHandler();
        _desktopRevealAnimationVersion++;
        SetDesktopRevealOpacityImmediate(1);
    }

    private void DetachDesktopTransparentFrameHandler()
    {
        if (_desktopTransparentFrameHandler == null) return;

        CompositionTarget.Rendering -= _desktopTransparentFrameHandler;
        _desktopTransparentFrameHandler = null;
    }

    private void AnimateDesktopRevealOpacity(double target, int durationMs, Action? completed)
    {
        // Animate Window.Opacity and snapshot current value before replacement to
        // avoid animation collisions with NotchContainer and base-value resets.
        double from = Math.Clamp(Opacity, 0, 1);
        int version = ++_desktopRevealAnimationVersion;
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = from;

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = target > from ? EasingMode.EaseOut : EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        animation.Completed += (_, _) =>
        {
            if (version != _desktopRevealAnimationVersion) return;

            BeginAnimation(Window.OpacityProperty, null);
            Opacity = target;
            completed?.Invoke();
        };
        BeginAnimation(
            Window.OpacityProperty,
            animation,
            System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void SetDesktopRevealOpacityImmediate(double opacity)
    {
        _desktopRevealAnimationVersion++;
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = opacity;
    }

    private void ResetDesktopEdgePromotionIfDisabled()
    {
        if (_settings.StayBehindWindows) return;

        _startupHoldTimer?.Stop();
        _startupHoldTimer = null;
        CancelScheduledDesktopDemotion();
        DetachDesktopTransparentFrameHandler();
        _desktopPromotionPending = false;
        _desktopDemotionPending = false;
        _isDesktopEdgePromoted = false;
        _desktopPointerInHoverZone = false;
        if (NotchContainer != null)
        {
            SetDesktopRevealOpacityImmediate(1);
        }
    }
}
