using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class NotchContentViewRefs
{
    public required FrameworkElement ExpandedContent { get; init; }
    public FrameworkElement? CameraContent { get; init; }
    public FrameworkElement? AiUsageContent { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public FrameworkElement? AudioContent { get; init; }
    public FrameworkElement? AudioScrollViewer { get; init; }
    public FrameworkElement? SecondaryContent { get; init; }
    public Func<long, bool>? IsSessionCurrent { get; init; }
}

public sealed class NotchContentTransitionPresenter : IDisposable
{


    private readonly NotchContentViewRefs _refs;
    private long _activeSessionId;
    private long _animationVersion;
    private bool _disposed;
    private sealed record PageOffset(Transform Original, TransformGroup Wrapper, TranslateTransform Offset);
    private readonly Dictionary<FrameworkElement, PageOffset> _pageOffsets = new();

    public NotchContentTransitionPresenter(NotchContentViewRefs refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
    }

    private static readonly CubicEase ExpandedSwitchEnterEase = CreateFrozenCubicInOut();

    private static CubicEase CreateFrozenCubicInOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        return ease;
    }

    public void TransitionContent(TransitionPlan plan, Action<TransitionExecutionResult> onCompleted)
    {
        if (_disposed)
        {
            onCompleted(new TransitionExecutionResult(plan.SessionId, TransitionExecutionStatus.Canceled, "Presenter disposed"));
            return;
        }

        long sessionId = plan.SessionId;
        _activeSessionId = sessionId;
        long version = ++_animationVersion;
        if (!IsCurrent(sessionId, version)) return;

        var targetElement = GetElementForView(plan.TargetView);
        var allElements = GetAllElements();

        // Nếu collapse: ẩn tất cả
        if (plan.TargetView == NotchView.Compact || targetElement == null)
        {
            FadeOutAll(sessionId, version, plan.Motion.Duration, plan.Motion.Easing, plan.Motion.TargetFps, plan.Motion.ReduceMotion, () =>
            {
                if (IsCurrent(sessionId, version))
                {
                    onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
                }
            });
            return;
        }

        // Đảm bảo control con AudioScrollViewer bên trong AudioContent luôn hiển thị khi target là AudioMixer
        if (plan.TargetView == NotchView.AudioMixer)
        {
            ShowAudioScrollViewer(requireDetachedFromAudioContent: false);
        }

        // Chuyển tới một expanded content view
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            ApplyImmediateExpandedTransition(plan, targetElement, allElements, sessionId, version);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        // Cross-fade animation
        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;
        bool switchingExpandedView = plan.FromView != NotchView.Compact;
        var enterDuration = switchingExpandedView
            ? new Duration(TimeSpan.FromMilliseconds(Math.Min(320, plan.Motion.Duration.TimeSpan.TotalMilliseconds)))
            : plan.Motion.Duration;
        var exitDuration = switchingExpandedView
            ? new Duration(TimeSpan.FromMilliseconds(Math.Min(190, plan.Motion.Duration.TimeSpan.TotalMilliseconds)))
            : plan.Motion.Duration;

        // Chuẩn bị target element theo Rule D3: Lấy opacity hiện tại TRƯỚC KHI dừng animation clock
        bool targetWasVisible = targetElement.Visibility == Visibility.Visible && targetElement.Opacity > 0.01;
        double targetStartOpacity = targetElement.Visibility == Visibility.Visible ? targetElement.Opacity : 0.0;
        targetElement.BeginAnimation(UIElement.OpacityProperty, null);
        targetElement.Visibility = Visibility.Visible;
        targetElement.Opacity = targetStartOpacity;
        if (switchingExpandedView)
        {
            targetElement.IsHitTestVisible = false;
            AnimatePageOffset(targetElement, targetWasVisible ? 0 : 22 * plan.NavigationDirection,
                0, enterDuration, fps);
        }
        else
        {
            ReleasePageOffset(targetElement);
            ClearTemporaryAnimationTransforms(targetElement);
        }

        var fadeIn = new DoubleAnimation(targetStartOpacity, 1.0, enterDuration)
        {
            EasingFunction = switchingExpandedView ? ExpandedSwitchEnterEase : plan.Motion.Easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(fadeIn, fps);

        fadeIn.Completed += (_, _) =>
        {
            if (!IsCurrent(sessionId, version)) return;

            targetElement.BeginAnimation(UIElement.OpacityProperty, null);
            targetElement.Opacity = 1.0;
            targetElement.Visibility = Visibility.Visible;
            ReleasePageOffset(targetElement);
            targetElement.IsHitTestVisible = true;

            foreach (var el in allElements)
            {
                if (!ReferenceEquals(el, targetElement))
                {
                    ResetContentVisualState(el);
                }
            }

            if (plan.TargetView == NotchView.AudioMixer)
            {
                ShowAudioScrollViewer(requireDetachedFromAudioContent: true);
            }
            else
            {
                ResetDetachedAudioScrollViewer();
            }

            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        };

        BeginOutgoingElementTransitions(
            plan, targetElement, allElements, sessionId, version, switchingExpandedView, exitDuration, fps);

        targetElement.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void ApplyImmediateExpandedTransition(
        TransitionPlan plan,
        FrameworkElement targetElement,
        List<FrameworkElement> allElements,
        long sessionId,
        long version)
    {
        foreach (var el in allElements)
        {
            if (ReferenceEquals(el, targetElement))
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Visibility = Visibility.Visible;
                el.Opacity = 1.0;
                ReleasePageOffset(el);
                ClearTemporaryAnimationTransforms(el);
                el.IsHitTestVisible = true;
            }
            else
            {
                ResetContentVisualState(el);
            }
        }
        if (IsCurrent(sessionId, version) && plan.TargetView != NotchView.AudioMixer)
        {
            ResetDetachedAudioScrollViewer();
        }
    }

    private void BeginOutgoingElementTransitions(
        TransitionPlan plan,
        FrameworkElement targetElement,
        List<FrameworkElement> allElements,
        long sessionId,
        long version,
        bool switchingExpandedView,
        Duration exitDuration,
        int fps)
    {
        foreach (var el in allElements)
        {
            if (ReferenceEquals(el, targetElement)) continue;

            if (el.Visibility != Visibility.Visible || el.Opacity <= 0.01)
            {
                ResetContentVisualState(el);
                continue;
            }

            double currentElOpacity = el.Opacity;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = currentElOpacity;
            if (switchingExpandedView)
            {
                el.IsHitTestVisible = false;
                AnimatePageOffset(el, 0, -14 * plan.NavigationDirection, exitDuration, fps);
            }

            var fadeOut = new DoubleAnimation(currentElOpacity, 0.0, exitDuration)
            {
                EasingFunction = switchingExpandedView ? AnimationPrimitives._easeQuadInOut : plan.Motion.Easing
            };
            Timeline.SetDesiredFrameRate(fadeOut, fps);
            fadeOut.Completed += (_, _) =>
            {
                if (!IsCurrent(sessionId, version)) return;
                ResetContentVisualState(el);
                if (ReferenceEquals(el, _refs.AudioContent))
                {
                    ResetDetachedAudioScrollViewer();
                }
            };
            el.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private void ShowAudioScrollViewer(bool requireDetachedFromAudioContent)
    {
        if (_refs.AudioScrollViewer == null) return;
        if (requireDetachedFromAudioContent && ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent)) return;
        _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.AudioScrollViewer.Visibility = Visibility.Visible;
        _refs.AudioScrollViewer.Opacity = 1.0;
        ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
    }

    private void ResetDetachedAudioScrollViewer()
    {
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            ResetElementVisualState(_refs.AudioScrollViewer);
        }
    }

    private void AnimatePageOffset(FrameworkElement element, double initialX, double targetX, Duration duration, int fps)
    {
        if (!_pageOffsets.TryGetValue(element, out var layer) || !ReferenceEquals(element.RenderTransform, layer.Wrapper))
        {
            var original = element.RenderTransform ?? Transform.Identity;
            var offset = new TranslateTransform(initialX, 0);
            var wrapper = new TransformGroup();
            wrapper.Children.Add(original);
            wrapper.Children.Add(offset);
            layer = new PageOffset(original, wrapper, offset);
            _pageOffsets[element] = layer;
            element.RenderTransform = wrapper;
        }
        double from = layer.Offset.X;
        layer.Offset.BeginAnimation(TranslateTransform.XProperty, null);
        layer.Offset.X = from;
        var slide = AnimationPrimitives.MakeAnim(from, targetX, duration, AnimationPrimitives._easeAppleOut, fps);
        layer.Offset.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void ReleasePageOffset(FrameworkElement element)
    {
        if (!_pageOffsets.Remove(element, out var layer)) return;
        layer.Offset.BeginAnimation(TranslateTransform.XProperty, null);
        if (ReferenceEquals(element.RenderTransform, layer.Wrapper)) element.RenderTransform = layer.Original;
    }

    private void ResetContentVisualState(FrameworkElement element)
    {
        ReleasePageOffset(element);
        ResetElementVisualState(element);
        element.IsHitTestVisible = true;
    }

    public long InvalidateSession()
    {
        ++_animationVersion;
        return ++_activeSessionId;
    }

    public void CancelActiveTransition()
    {
        ++_animationVersion;
        _activeSessionId++;
        foreach (var el in GetAllElements())
        {
            double currentOpacity = el.Visibility == Visibility.Visible ? el.Opacity : 0.0;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = currentOpacity;
            HoldCurrentTransformAndEffect(el);
        }
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            double currentScrollOpacity = _refs.AudioScrollViewer.Visibility == Visibility.Visible ? _refs.AudioScrollViewer.Opacity : 0.0;
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Opacity = currentScrollOpacity;
            HoldCurrentTransformAndEffect(_refs.AudioScrollViewer);
        }
    }

    private static void HoldCurrentTransformAndEffect(FrameworkElement element)
    {
        // Keep named transform/effect instances: other entrance animations can
        // still reference them. Only detach clocks, retaining the frame on screen.
        HoldTransform(element.RenderTransform);
        if (element.Effect is BlurEffect { IsFrozen: false } blur)
            HoldAnimatedValue(blur, BlurEffect.RadiusProperty);
    }

    private static void HoldTransform(Transform? transform)
    {
        if (transform == null || transform.IsFrozen) return;
        if (transform is TransformGroup group)
        {
            foreach (var child in group.Children) HoldTransform(child);
            return;
        }
        if (!transform.HasAnimatedProperties) return;
        switch (transform)
        {
            case TranslateTransform:
                HoldAnimatedValue(transform, TranslateTransform.XProperty);
                HoldAnimatedValue(transform, TranslateTransform.YProperty);
                break;
            case ScaleTransform:
                HoldAnimatedValue(transform, ScaleTransform.ScaleXProperty);
                HoldAnimatedValue(transform, ScaleTransform.ScaleYProperty);
                HoldAnimatedValue(transform, ScaleTransform.CenterXProperty);
                HoldAnimatedValue(transform, ScaleTransform.CenterYProperty);
                break;
            case RotateTransform:
                HoldAnimatedValue(transform, RotateTransform.AngleProperty);
                HoldAnimatedValue(transform, RotateTransform.CenterXProperty);
                HoldAnimatedValue(transform, RotateTransform.CenterYProperty);
                break;
            case SkewTransform:
                HoldAnimatedValue(transform, SkewTransform.AngleXProperty);
                HoldAnimatedValue(transform, SkewTransform.AngleYProperty);
                HoldAnimatedValue(transform, SkewTransform.CenterXProperty);
                HoldAnimatedValue(transform, SkewTransform.CenterYProperty);
                break;
            case MatrixTransform:
                HoldAnimatedValue(transform, MatrixTransform.MatrixProperty);
                break;
        }
    }

    private static void HoldAnimatedValue(Animatable target, DependencyProperty property)
    {
        if (!DependencyPropertyHelper.GetValueSource(target, property).IsAnimated) return;
        object value = target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetCurrentValue(property, value);
    }

    public void SnapToView(NotchView view)
    {
        if (_disposed) return;
        ++_animationVersion;
        _activeSessionId++;
        var target = GetElementForView(view);
        if (view == NotchView.AudioMixer && _refs.AudioScrollViewer != null)
        {
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Visibility = Visibility.Visible;
            _refs.AudioScrollViewer.Opacity = 1.0;
            ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
        }
        else if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            ResetElementVisualState(_refs.AudioScrollViewer);
        }

        foreach (var el in GetAllElements())
        {
            if (target != null && ReferenceEquals(el, target))
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Visibility = Visibility.Visible;
                el.Opacity = 1.0;
                ReleasePageOffset(el);
                el.IsHitTestVisible = true;
                ClearTemporaryAnimationTransforms(el);
            }
            else
            {
                ResetContentVisualState(el);
            }
        }
    }

    public static void ResetElementVisualState(FrameworkElement el)
    {
        if (el == null) return;
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 0.0;
        el.Visibility = Visibility.Collapsed;
        ClearTemporaryAnimationTransforms(el);
    }

    public static void ClearTransformAndEffects(FrameworkElement el) => ClearTemporaryAnimationTransforms(el);

    public static void ClearTemporaryAnimationTransforms(FrameworkElement el)
    {
        if (el == null) return;

        // Giữ transform bố cục cho ExpandedContent (Media view), chỉ dọn animation tạm và hiệu ứng
        if (el.Name == "ExpandedContent")
        {
            if (el.RenderTransform is TransformGroup tg)
            {
                foreach (var child in tg.Children)
                {
                    if (child is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                    }
                    else if (child is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        st.ScaleX = 1.0;
                        st.ScaleY = 1.0;
                    }
                }
            }
            else if (el.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.BeginAnimation(TranslateTransform.XProperty, null);
            }

            if (el.Effect is BlurEffect blur)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
                blur.Radius = 0;
            }
            if (Controls.ActiveBlur.GetBlur(el) == null)
            {
                el.Effect = null;
            }
            return;
        }

        // Đối với các view khác (Timer, Audio, Secondary): không có transform bố cục cố định,
        // dọn dẹp transform và effect tạm thời của animation.
        el.RenderTransform = null;
        if (el.Effect is BlurEffect b)
        {
            b.BeginAnimation(BlurEffect.RadiusProperty, null);
            b.Radius = 0;
        }
        el.Effect = null;
    }

    private void FadeOutAll(long sessionId, long version, Duration duration, IEasingFunction easing, int fps, bool reduceMotion, Action onCompleted)
    {
        var elements = GetAllElements();
        if (reduceMotion || duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in elements)
            {
                ResetContentVisualState(el);
            }
            if (IsCurrent(sessionId, version))
            {
                ResetDetachedAudioScrollViewer();
            }
            onCompleted();
            return;
        }

        int pendingCount = 0;
        foreach (var el in elements)
        {
            if (el.Visibility != Visibility.Visible || el.Opacity <= 0.01)
            {
                ResetContentVisualState(el);
                continue;
            }

            double currentElOpacity = el.Opacity;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = currentElOpacity;

            pendingCount++;
            var fade = new DoubleAnimation(currentElOpacity, 0.0, duration) { EasingFunction = easing };
            Timeline.SetDesiredFrameRate(fade, fps);
            fade.Completed += (_, _) =>
            {
                if (!IsCurrent(sessionId, version)) return;
                ResetContentVisualState(el);
                pendingCount--;
                if (pendingCount <= 0)
                {
                    ResetDetachedAudioScrollViewer();
                    onCompleted();
                }
            };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        if (pendingCount == 0)
        {
            if (IsCurrent(sessionId, version))
            {
                ResetDetachedAudioScrollViewer();
            }
            onCompleted();
        }
    }

    private FrameworkElement? GetElementForView(NotchView view)
    {
        return view switch
        {
            NotchView.Media => _refs.ExpandedContent,
            NotchView.Timer => _refs.TimerContent,
            NotchView.AudioMixer => _refs.AudioContent ?? _refs.AudioScrollViewer,
            NotchView.Camera => _refs.CameraContent,
            NotchView.AiUsage => _refs.AiUsageContent,
            NotchView.Secondary => _refs.SecondaryContent,
            _ => null
        };
    }

    private List<FrameworkElement> GetAllElements()
    {
        var list = new List<FrameworkElement> { _refs.ExpandedContent };
        if (_refs.CameraContent != null) list.Add(_refs.CameraContent);
        if (_refs.AiUsageContent != null) list.Add(_refs.AiUsageContent);
        if (_refs.TimerContent != null) list.Add(_refs.TimerContent);
        if (_refs.AudioContent != null) list.Add(_refs.AudioContent);
        else if (_refs.AudioScrollViewer != null) list.Add(_refs.AudioScrollViewer);
        if (_refs.SecondaryContent != null) list.Add(_refs.SecondaryContent);
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActiveTransition();
        foreach (var element in GetAllElements()) ReleasePageOffset(element);
    }

    private bool IsCurrent(long sessionId, long version) =>
        !_disposed && version == _animationVersion && sessionId == _activeSessionId &&
        (_refs.IsSessionCurrent?.Invoke(sessionId) ?? true);
}
