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

    private bool _isDesktopEdgePromoted;
    private bool _desktopPromotionPending;
    private bool _desktopDemotionPending;
    private int _desktopRevealAnimationVersion;
    private DispatcherTimer? _desktopDemotionDelayTimer;
    private EventHandler? _desktopTransparentFrameHandler;
    private int _desktopTransparentFramesObserved;
    private bool _desktopPointerInHoverZone;
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
            // Once the edge has armed the reveal, allow the pointer to travel down
            // into the notch. Cancelling as soon as it left the 3 px edge strip made
            // the transparent/promotion frames alternate during a normal gesture.
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
        bool pointerOverNotch = NotchWrapper?.IsMouseOver == true;
        bool inputCapturedWithin = IsMouseCaptureWithin
                                   || IsStylusCaptureWithin
                                   || AreAnyTouchesCapturedWithin;
        bool ownedWindowInteractionActive = HasActiveOwnedWindowInteraction();

        return ShouldKeepDesktopPromotion(
            _desktopPointerInHoverZone,
            pointerOverNotch,
            inputCapturedWithin,
            IsKeyboardFocusWithin,
            ownedWindowInteractionActive);
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

                // Interaction may begin again after demotion was scheduled. Check
                // the live WPF input state so an expanded panel, drag operation, or
                // text field cannot disappear behind the foreground application.
                if (IsDesktopNotchInteractionActive()) return;

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

    private bool IsNotchObscuredByAnyWindow()
    {
        if (_hwnd == IntPtr.Zero) return false;

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

            if (FullscreenDetector.TryGetWindowBounds(hwnd, out var winRect))
            {
                if (winRect.Left < notchRect.Right &&
                    winRect.Right > notchRect.Left &&
                    winRect.Top < notchRect.Bottom &&
                    winRect.Bottom > notchRect.Top)
                {
                    isObscured = true;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);

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

        // Render one fully transparent frame while the HWND is still behind the
        // foreground application. Promoting first exposes DWM's previous opaque
        // surface for a frame, which looks like a bright flash before the fade.
        _desktopPromotionPending = true;
        _desktopDemotionPending = false;
        SetDesktopRevealOpacityImmediate(0);
        _desktopTransparentFramesObserved = 0;

        // WPF property assignment is not proof that the layered HWND has presented
        // the transparent pixels. Wait for an actual composition frame and flush
        // DWM before changing z-order; otherwise the previous opaque surface can be
        // shown for one frame above the foreground app.
        _desktopTransparentFrameHandler = (_, _) =>
        {
            // CompositionTarget.Rendering is raised immediately before WPF renders
            // a frame, not after it has submitted that frame to DWM.  Therefore the
            // first callback only tells us that the zero-opacity frame is about to
            // be produced.  Waiting for the following callback guarantees that one
            // complete transparent frame has actually been submitted.  Promoting on
            // the first callback was timing-dependent and caused the remaining
            // occasional one-frame flash.
            if (++_desktopTransparentFramesObserved < 2)
                return;

            if (_desktopTransparentFrameHandler != null)
            {
                CompositionTarget.Rendering -= _desktopTransparentFrameHandler;
                _desktopTransparentFrameHandler = null;
            }

            if (!_desktopPromotionPending || _cleanedUp) return;

            DwmFlush();

            _desktopPromotionPending = false;
            _isDesktopEdgePromoted = true;

            // Change the HWND layer only after WPF/DWM has committed the fully
            // transparent frame. This preserves the correct WS_EX_TOPMOST state
            // without ever exposing a stale opaque layered-window surface.
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
            DwmFlush();
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

            // Make sure DWM has consumed the transparent surface at its new,
            // non-topmost z-order before restoring opacity.  Without this barrier,
            // an opaque frame could occasionally be composed in the old topmost
            // band while the pointer reversed direction quickly.
            DwmFlush();

            // It is now behind normal windows. Restore its visual state so it is
            // immediately ready when the desktop or hot zone is shown again.
            SetDesktopRevealOpacityImmediate(1);
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
        // Desktop promotion owns Window.Opacity rather than NotchContainer.Opacity.
        // Fullscreen/idle visibility animations also animate NotchContainer; using
        // the same dependency property let either animation replace the other and
        // was the main source of intermittent flashing during state changes.
        // Snapshot the currently rendered value before replacing an animation.
        // Calling BeginAnimation(..., null) directly resets to the base value and
        // was the second source of flashing when the pointer reversed direction.
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
