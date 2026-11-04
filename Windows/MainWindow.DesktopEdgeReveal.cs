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
    private UiScheduledTimer? _desktopDemotionDelayTimer;
    private EventHandler? _desktopTransparentFrameHandler;
    private bool _desktopPointerInHoverZone;
    private DateTime _startupHoldUntilUtc = DateTime.MinValue;
    private UiScheduledTimer? _startupHoldTimer;
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
        // Keep the file tray above other windows until the user dismisses it.
        if (_isSecondaryView) return true;

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
        _startupHoldTimer?.Dispose();
        _startupHoldTimer = new UiScheduledTimer(duration, DispatcherPriority.Background, (_, _) =>
        {
            _startupHoldTimer?.Stop();
            _startupHoldTimer?.Dispose();
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

        _desktopDemotionDelayTimer ??= new UiScheduledTimer(
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

    private void ScheduleDesktopLayerDemotionIfIdle()
    {
        // Focus and settings can change while the cursor stays still. The hover
        // service skips unchanged positions, so these transitions must recheck too.
        if (_settings.StayBehindWindows && _isDesktopEdgePromoted && !IsDesktopNotchInteractionActive())
            ScheduleDesktopLayerDemotion();
    }

    private void CancelScheduledDesktopDemotion()
    {
        _desktopDemotionDelayTimer?.Stop();
    }

    private Task<bool> IsNotchObscuredByAnyWindowAsync()
    {
        IntPtr notchHwnd = _hwnd;
        // Keep the current frame visible while probing. Assuming a stale result
        // means "obscured" made a notch on the desktop blink off before revealing.
        return notchHwnd == IntPtr.Zero
            ? Task.FromResult(false)
            : Task.Run(() => ProbeNotchObscuredByAnyWindow(notchHwnd));
    }

    private static bool ProbeNotchObscuredByAnyWindow(IntPtr notchHwnd)
    {
        if (!GetWindowRect(notchHwnd, out var notchRect))
            return false;

        if (notchRect.Right <= notchRect.Left || notchRect.Bottom <= notchRect.Top)
            return false;

        GetWindowThreadProcessId(notchHwnd, out uint myPid);

        bool isObscured = false;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == notchHwnd) return true;

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

        CancelScheduledDesktopDemotion();
        _desktopPromotionPending = true;
        int version = ++_desktopRevealAnimationVersion;
        PromoteFromDesktopLayerAsync(version).SafeFireAndForget("DESKTOP-REVEAL");
    }

    private async Task PromoteFromDesktopLayerAsync(int version)
    {
        bool obscured = await IsNotchObscuredByAnyWindowAsync();
        if (!IsDesktopRevealCurrent(version) || !_desktopPromotionPending) return;

        if (!obscured || !IsEffectivelyNotchVisible)
        {
            _desktopPromotionPending = false;
            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = true;
            ConfigureOverlayWindow();
            SetDesktopRevealOpacityImmediate(1);
            return;
        }

        _desktopDemotionPending = false;
        SetDesktopRevealOpacityImmediate(0);
        DesktopRevealTranslate.Y = -NotchVisibilitySlideDistance;

        QueueAfterDesktopTransparentFrame(() =>
        {
            _desktopPromotionPending = false;
            _isDesktopEdgePromoted = true;
            ConfigureOverlayWindow();
            CompleteDesktopLayerChangeAsync(_desktopRevealAnimationVersion, revealing: true)
                .SafeFireAndForget("DESKTOP-REVEAL");
        });
    }

    private void DemoteToDesktopLayerWithFade()
    {
        if (_desktopDemotionPending) return;
        CancelScheduledDesktopDemotion();

        _desktopDemotionPending = true;
        int version = ++_desktopRevealAnimationVersion;
        DemoteToDesktopLayerAsync(version).SafeFireAndForget("DESKTOP-REVEAL");
    }

    private async Task DemoteToDesktopLayerAsync(int version)
    {
        bool obscured = await IsNotchObscuredByAnyWindowAsync();
        if (!IsDesktopRevealCurrent(version) || !_desktopDemotionPending) return;

        if (!obscured || !IsEffectivelyNotchVisible)
        {
            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = false;
            ConfigureOverlayWindow();
            SetDesktopRevealOpacityImmediate(1);
            return;
        }

        AnimateDesktopReveal(revealing: false, () => QueueAfterDesktopTransparentFrame(() =>
        {
            _desktopDemotionPending = false;
            _isDesktopEdgePromoted = false;
            ConfigureOverlayWindow();
            CompleteDesktopLayerChangeAsync(_desktopRevealAnimationVersion, revealing: false)
                .SafeFireAndForget("DESKTOP-REVEAL");
        }));
    }

    private bool IsDesktopRevealCurrent(int version) =>
        !_cleanedUp && version == _desktopRevealAnimationVersion;

    private void QueueAfterDesktopTransparentFrame(Action completed)
    {
        DetachDesktopTransparentFrameHandler();
        int version = _desktopRevealAnimationVersion;
        TimeSpan? firstFrame = null;
        _desktopTransparentFrameHandler = (_, args) =>
        {
            // Rendering can run more than once for one frame. Only a distinct
            // frame guarantees the previous transparent surface was submitted.
            if (args is not RenderingEventArgs rendering) return;
            if (firstFrame == null)
            {
                firstFrame = rendering.RenderingTime;
                return;
            }
            if (firstFrame == rendering.RenderingTime) return;

            DetachDesktopTransparentFrameHandler();
            if (IsDesktopRevealCurrent(version)) completed();
        };
        CompositionTarget.Rendering += _desktopTransparentFrameHandler;
    }

    private async Task CompleteDesktopLayerChangeAsync(int version, bool revealing)
    {
        // Wait off the UI thread until the new z-order reaches the compositor.
        if (_hwnd != IntPtr.Zero) await Task.Run(() => DwmFlush());
        if (!IsDesktopRevealCurrent(version)
            || _isDesktopEdgePromoted != revealing
            || _desktopPromotionPending || _desktopDemotionPending) return;

        if (revealing) AnimateDesktopReveal(revealing: true);
        else SetDesktopRevealOpacityImmediate(1);
    }

    private void CancelDesktopLayerDemotion()
    {
        _desktopDemotionPending = false;
        DetachDesktopTransparentFrameHandler();
        AnimateDesktopReveal(revealing: true);
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
        // Keep the layered HWND's alpha stable while changing z-order. A separate
        // visual also lets idle and desktop transitions animate independently.
        double from = Math.Clamp(DesktopRevealRoot.Opacity, 0, 1);
        int version = ++_desktopRevealAnimationVersion;
        DesktopRevealRoot.BeginAnimation(UIElement.OpacityProperty, null);
        DesktopRevealRoot.Opacity = from;

        var animation = new DoubleAnimation(from, target, DesktopRevealDuration(durationMs))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = target > from ? EasingMode.EaseOut : EasingMode.EaseIn
            },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        animation.Completed += (_, _) =>
        {
            if (version != _desktopRevealAnimationVersion) return;

            DesktopRevealRoot.Opacity = target;
            DesktopRevealRoot.BeginAnimation(UIElement.OpacityProperty, null);
            completed?.Invoke();
        };
        DesktopRevealRoot.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void SetDesktopRevealOpacityImmediate(double opacity)
    {
        _desktopRevealAnimationVersion++;
        DesktopRevealRoot.BeginAnimation(UIElement.OpacityProperty, null);
        DesktopRevealRoot.Opacity = opacity;
        DesktopRevealTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        DesktopRevealTranslate.Y = 0;
    }

    private static TimeSpan DesktopRevealDuration(int milliseconds) =>
        AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation
            ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);

    private void AnimateDesktopReveal(bool revealing, Action? completed = null)
    {
        double fromY = DesktopRevealTranslate.Y;
        double targetY = revealing ? 0 : -NotchVisibilitySlideDistance;
        AnimateDesktopRevealOpacity(revealing ? 1 : 0,
            revealing ? NotchRevealFadeDurationMs : NotchHideFadeDurationMs, null);
        int version = _desktopRevealAnimationVersion;
        DesktopRevealTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        DesktopRevealTranslate.Y = fromY;
        var animation = new DoubleAnimation(fromY, targetY,
            DesktopRevealDuration(revealing ? NotchRevealSlideDurationMs : NotchHideSlideDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = revealing ? EasingMode.EaseOut : EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        animation.Completed += (_, _) =>
        {
            if (!IsDesktopRevealCurrent(version)) return;
            DesktopRevealTranslate.Y = targetY;
            DesktopRevealTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            completed?.Invoke();
        };
        DesktopRevealTranslate.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
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
