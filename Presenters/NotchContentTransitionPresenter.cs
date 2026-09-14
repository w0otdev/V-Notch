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
    public FrameworkElement? TimerContent { get; init; }
    public FrameworkElement? AudioContent { get; init; }
    public FrameworkElement? AudioScrollViewer { get; init; }
    public FrameworkElement? SecondaryContent { get; init; }
}

public sealed class NotchContentTransitionPresenter : IDisposable
{
    private const string LogTag = "NOTCH-CONTENT-PRESENTER";

    private readonly NotchContentViewRefs _refs;
    private long _activeSessionId;
    private bool _disposed;

    public NotchContentTransitionPresenter(NotchContentViewRefs refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
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

        var targetElement = GetElementForView(plan.TargetView);
        var allElements = GetAllElements();

        // Nếu collapse: ẩn tất cả
        if (plan.TargetView == NotchView.Compact || targetElement == null)
        {
            FadeOutAll(sessionId, plan.Motion.Duration, plan.Motion.Easing, plan.Motion.TargetFps, plan.Motion.ReduceMotion, () =>
            {
                if (sessionId == _activeSessionId)
                {
                    onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
                }
            });
            return;
        }

        // Đảm bảo control con AudioScrollViewer bên trong AudioContent luôn hiển thị khi target là AudioMixer
        if (plan.TargetView == NotchView.AudioMixer && _refs.AudioScrollViewer != null)
        {
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Visibility = Visibility.Visible;
            _refs.AudioScrollViewer.Opacity = 1.0;
            ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
        }

        // Chuyển tới một expanded content view
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in allElements)
            {
                if (ReferenceEquals(el, targetElement))
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Visibility = Visibility.Visible;
                    el.Opacity = 1.0;
                    ClearTemporaryAnimationTransforms(el);
                }
                else
                {
                    ResetElementVisualState(el);
                }
            }
            if (sessionId == _activeSessionId && plan.TargetView != NotchView.AudioMixer && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetChildVisualState(_refs.AudioScrollViewer);
            }
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        // Cross-fade animation
        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;

        // Chuẩn bị target element theo Rule D3: Lấy opacity hiện tại TRƯỚC KHI dừng animation clock
        double targetStartOpacity = targetElement.Visibility == Visibility.Visible ? targetElement.Opacity : 0.0;
        targetElement.BeginAnimation(UIElement.OpacityProperty, null);
        targetElement.Visibility = Visibility.Visible;
        targetElement.Opacity = targetStartOpacity;
        ClearTemporaryAnimationTransforms(targetElement);

        var fadeIn = new DoubleAnimation(targetStartOpacity, 1.0, plan.Motion.Duration)
        {
            EasingFunction = plan.Motion.Easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(fadeIn, fps);

        fadeIn.Completed += (_, _) =>
        {
            if (sessionId != _activeSessionId) return;

            targetElement.BeginAnimation(UIElement.OpacityProperty, null);
            targetElement.Opacity = 1.0;
            targetElement.Visibility = Visibility.Visible;

            // Ẩn các element khác và dọn dẹp transform/effect
            foreach (var el in allElements)
            {
                if (!ReferenceEquals(el, targetElement))
                {
                    ResetElementVisualState(el);
                }
            }

            if (plan.TargetView == NotchView.AudioMixer && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
                _refs.AudioScrollViewer.Visibility = Visibility.Visible;
                _refs.AudioScrollViewer.Opacity = 1.0;
                ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
            }
            else if (plan.TargetView != NotchView.AudioMixer && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetChildVisualState(_refs.AudioScrollViewer);
            }

            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        };

        // Fade out các element còn lại theo Rule D3: Lấy opacity hiện tại TRƯỚC KHI dừng animation clock
        foreach (var el in allElements)
        {
            if (ReferenceEquals(el, targetElement)) continue;

            if (el.Visibility == Visibility.Visible && el.Opacity > 0.01)
            {
                double currentElOpacity = el.Opacity;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = currentElOpacity;

                var fadeOut = new DoubleAnimation(currentElOpacity, 0.0, plan.Motion.Duration)
                {
                    EasingFunction = plan.Motion.Easing
                };
                Timeline.SetDesiredFrameRate(fadeOut, fps);
                fadeOut.Completed += (_, _) =>
                {
                    if (sessionId != _activeSessionId) return;
                    ResetElementVisualState(el);
                    if (ReferenceEquals(el, _refs.AudioContent) && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
                    {
                        ResetChildVisualState(_refs.AudioScrollViewer);
                    }
                };
                el.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                ResetElementVisualState(el);
            }
        }

        targetElement.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public long InvalidateSession()
    {
        return ++_activeSessionId;
    }

    public void CancelActiveTransition()
    {
        _activeSessionId++;
        foreach (var el in GetAllElements())
        {
            double currentOpacity = el.Visibility == Visibility.Visible ? el.Opacity : 0.0;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = currentOpacity;
            ClearTemporaryAnimationTransforms(el);
        }
        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
        {
            double currentScrollOpacity = _refs.AudioScrollViewer.Visibility == Visibility.Visible ? _refs.AudioScrollViewer.Opacity : 0.0;
            _refs.AudioScrollViewer.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.AudioScrollViewer.Opacity = currentScrollOpacity;
            ClearTemporaryAnimationTransforms(_refs.AudioScrollViewer);
        }
    }

    public void SnapToView(NotchView view)
    {
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
            ResetChildVisualState(_refs.AudioScrollViewer);
        }

        foreach (var el in GetAllElements())
        {
            if (target != null && ReferenceEquals(el, target))
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Visibility = Visibility.Visible;
                el.Opacity = 1.0;
                ClearTemporaryAnimationTransforms(el);
            }
            else
            {
                ResetElementVisualState(el);
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

    private static void ResetChildVisualState(FrameworkElement el)
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
        if (el.Name == "ExpandedContent" || string.Equals(el.Name, "ExpandedContent", StringComparison.Ordinal))
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

    private void FadeOutAll(long sessionId, Duration duration, IEasingFunction easing, int fps, bool reduceMotion, Action onCompleted)
    {
        var elements = GetAllElements();
        if (reduceMotion || duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in elements)
            {
                ResetElementVisualState(el);
            }
            if (sessionId == _activeSessionId && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetChildVisualState(_refs.AudioScrollViewer);
            }
            onCompleted();
            return;
        }

        int pendingCount = 0;
        foreach (var el in elements)
        {
            if (el.Visibility == Visibility.Visible && el.Opacity > 0.01)
            {
                double currentElOpacity = el.Opacity;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = currentElOpacity;

                pendingCount++;
                var fade = new DoubleAnimation(currentElOpacity, 0.0, duration) { EasingFunction = easing };
                Timeline.SetDesiredFrameRate(fade, fps);
                fade.Completed += (_, _) =>
                {
                    if (sessionId != _activeSessionId) return;
                    ResetElementVisualState(el);
                    pendingCount--;
                    if (pendingCount <= 0)
                    {
                        if (_refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
                        {
                            ResetChildVisualState(_refs.AudioScrollViewer);
                        }
                        onCompleted();
                    }
                };
                el.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                ResetElementVisualState(el);
            }
        }

        if (pendingCount == 0)
        {
            if (sessionId == _activeSessionId && _refs.AudioScrollViewer != null && !ReferenceEquals(_refs.AudioScrollViewer, _refs.AudioContent))
            {
                ResetChildVisualState(_refs.AudioScrollViewer);
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
            NotchView.Secondary => _refs.SecondaryContent,
            _ => null
        };
    }

    private List<FrameworkElement> GetAllElements()
    {
        var list = new List<FrameworkElement> { _refs.ExpandedContent };
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
        _activeSessionId++;
    }
}
