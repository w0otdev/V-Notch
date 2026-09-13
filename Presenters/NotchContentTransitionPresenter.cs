using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class NotchContentViewRefs
{
    public required FrameworkElement ExpandedContent { get; init; }
    public FrameworkElement? TimerContent { get; init; }
    public FrameworkElement? AudioScrollViewer { get; init; }
    public FrameworkElement? SecondaryContent { get; init; }
}

/// <summary>
/// Quản lý việc cross-fade, opacity và chuyển đổi giữa các bề mặt nội dung của notch.
/// Độc quyền sở hữu Opacity & Visibility của các Content Containers (Workstream D - implement.md).
/// </summary>
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
            FadeOutAll(plan.Motion.Duration, plan.Motion.Easing, plan.Motion.TargetFps, plan.Motion.ReduceMotion, () =>
            {
                if (sessionId == _activeSessionId)
                {
                    onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
                }
            });
            return;
        }

        // Chuyển tới một expanded content view
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in allElements)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                if (ReferenceEquals(el, targetElement))
                {
                    el.Visibility = Visibility.Visible;
                    el.Opacity = 1.0;
                }
                else
                {
                    el.Visibility = Visibility.Collapsed;
                    el.Opacity = 0.0;
                }
            }
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        // Cross-fade animation
        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;

        // Chuẩn bị target element
        targetElement.BeginAnimation(UIElement.OpacityProperty, null);
        double targetStartOpacity = targetElement.Visibility == Visibility.Visible ? targetElement.Opacity : 0.0;
        targetElement.Visibility = Visibility.Visible;
        targetElement.Opacity = targetStartOpacity;

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

            // Ẩn các element khác
            foreach (var el in allElements)
            {
                if (!ReferenceEquals(el, targetElement))
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Opacity = 0.0;
                    el.Visibility = Visibility.Collapsed;
                }
            }

            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        };

        // Fade out các element còn lại
        foreach (var el in allElements)
        {
            if (ReferenceEquals(el, targetElement)) continue;

            if (el.Visibility == Visibility.Visible && el.Opacity > 0.01)
            {
                var fadeOut = new DoubleAnimation(el.Opacity, 0.0, plan.Motion.Duration)
                {
                    EasingFunction = plan.Motion.Easing
                };
                Timeline.SetDesiredFrameRate(fadeOut, fps);
                fadeOut.Completed += (_, _) =>
                {
                    if (sessionId != _activeSessionId) return;
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Opacity = 0.0;
                    el.Visibility = Visibility.Collapsed;
                };
                el.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                el.Visibility = Visibility.Collapsed;
                el.Opacity = 0.0;
            }
        }

        targetElement.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public void SnapToView(NotchView view)
    {
        _activeSessionId++;
        var target = GetElementForView(view);
        foreach (var el in GetAllElements())
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            if (target != null && ReferenceEquals(el, target))
            {
                el.Visibility = Visibility.Visible;
                el.Opacity = 1.0;
            }
            else
            {
                el.Visibility = Visibility.Collapsed;
                el.Opacity = 0.0;
            }
        }
    }

    private void FadeOutAll(Duration duration, IEasingFunction easing, int fps, bool reduceMotion, Action onCompleted)
    {
        var elements = GetAllElements();
        if (reduceMotion || duration.TimeSpan <= TimeSpan.Zero)
        {
            foreach (var el in elements)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 0.0;
                el.Visibility = Visibility.Collapsed;
            }
            onCompleted();
            return;
        }

        int pendingCount = 0;
        foreach (var el in elements)
        {
            if (el.Visibility == Visibility.Visible && el.Opacity > 0.01)
            {
                pendingCount++;
                var fade = new DoubleAnimation(el.Opacity, 0.0, duration) { EasingFunction = easing };
                Timeline.SetDesiredFrameRate(fade, fps);
                fade.Completed += (_, _) =>
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Opacity = 0.0;
                    el.Visibility = Visibility.Collapsed;
                    pendingCount--;
                    if (pendingCount <= 0) onCompleted();
                };
                el.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                el.Visibility = Visibility.Collapsed;
                el.Opacity = 0.0;
            }
        }

        if (pendingCount == 0)
        {
            onCompleted();
        }
    }

    private FrameworkElement? GetElementForView(NotchView view)
    {
        return view switch
        {
            NotchView.Media => _refs.ExpandedContent,
            NotchView.Timer => _refs.TimerContent,
            NotchView.AudioMixer => _refs.AudioScrollViewer,
            NotchView.Secondary => _refs.SecondaryContent,
            _ => null
        };
    }

    private List<FrameworkElement> GetAllElements()
    {
        var list = new List<FrameworkElement> { _refs.ExpandedContent };
        if (_refs.TimerContent != null) list.Add(_refs.TimerContent);
        if (_refs.AudioScrollViewer != null) list.Add(_refs.AudioScrollViewer);
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
