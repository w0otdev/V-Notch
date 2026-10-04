using System;
using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controllers;

public sealed class NotchAnimationController
{
    private readonly NotchTransitionCoordinator _coordinator;

    public bool IsAnimating => _coordinator.IsTransitionActive;

    public (double X, double Y)? CachedThumbnailExpandTarget { get; set; }

    private DoubleAnimation? _cachedThumbWidthExpand;
    private DoubleAnimation? _cachedThumbHeightExpand;
    private RectAnimation? _cachedThumbRectExpand;
    private DoubleAnimation? _cachedThumbWidthCollapse;
    private DoubleAnimation? _cachedThumbHeightCollapse;
    private RectAnimation? _cachedThumbRectCollapse;

    public double CollapsedWidth { get; set; }
    public double CollapsedHeight { get; set; }
    public double ExpandedWidth { get; set; } = 480;
    public double ExpandedHeight { get; set; } = 146;
    public double CornerRadiusCollapsed { get; set; }
    public double CornerRadiusExpanded { get; set; } = 24;

    public event Action? ExpandStarted;
    public event Action? ExpandCompleted;
    public event Action? CollapseStarted;
    public event Action? CollapseCompleted;

    public NotchAnimationController(NotchTransitionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public bool IsExpanded => _coordinator.CurrentView != NotchView.Compact;
    public bool CanExpand => !IsAnimating && !IsExpanded;
    public bool CanCollapse => !IsAnimating && IsExpanded;

    public bool TryBeginExpand()
    {
        if (!CanExpand) return false;
        if (!_coordinator.RequestView(NotchView.Media, "NotchAnimationController.Expand")) return false;
        ExpandStarted?.Invoke();
        return true;
    }

    public void CompleteExpand(long transitionId)
    {
        if (!_coordinator.IsTransitionActive || transitionId != _coordinator.ActiveTransitionId) return;
        _coordinator.CompleteTransition(transitionId);
        ExpandCompleted?.Invoke();
    }

    public bool TryBeginCollapse()
    {
        if (!CanCollapse) return false;
        if (!_coordinator.RequestCollapse("NotchAnimationController.Collapse")) return false;
        CollapseStarted?.Invoke();
        return true;
    }

    public void CompleteCollapse(long transitionId)
    {
        if (!_coordinator.IsTransitionActive || transitionId != _coordinator.ActiveTransitionId) return;
        _coordinator.CompleteTransition(transitionId);
        CollapseCompleted?.Invoke();
    }

    public (DoubleAnimation width, DoubleAnimation height, RectAnimation rect) GetOrCreateExpandThumbAnims(
        Duration duration, IEasingFunction easing, TimeSpan? delay, int fps = 0)
    {
        if (fps <= 0) fps = AnimationConfig.TargetFps;
        if (_cachedThumbWidthExpand == null || _cachedThumbHeightExpand == null || _cachedThumbRectExpand == null || _cachedThumbWidthExpand.Duration != duration)
        {
            _cachedThumbWidthExpand = MakeAnim(22, 102, duration, easing, delay);
            _cachedThumbHeightExpand = MakeAnim(22, 102, duration, easing, delay);
            Timeline.SetDesiredFrameRate(_cachedThumbWidthExpand, fps);
            Timeline.SetDesiredFrameRate(_cachedThumbHeightExpand, fps);

            _cachedThumbRectExpand = new RectAnimation(new Rect(0, 0, 22, 22), new Rect(0, 0, 102, 102), duration)
            {
                EasingFunction = easing,
                BeginTime = delay
            };
            Timeline.SetDesiredFrameRate(_cachedThumbRectExpand, fps);

            _cachedThumbWidthExpand.Freeze();
            _cachedThumbHeightExpand.Freeze();
            _cachedThumbRectExpand.Freeze();
        }

        return (_cachedThumbWidthExpand, _cachedThumbHeightExpand, _cachedThumbRectExpand);
    }

    public (DoubleAnimation width, DoubleAnimation height, RectAnimation rect) GetOrCreateCollapseThumbAnims(
        Duration duration, IEasingFunction easing, TimeSpan? delay, int fps = 0)
    {
        if (fps <= 0) fps = AnimationConfig.TargetFps;
        if (_cachedThumbWidthCollapse == null || _cachedThumbHeightCollapse == null || _cachedThumbRectCollapse == null || _cachedThumbWidthCollapse.Duration != duration)
        {
            _cachedThumbWidthCollapse = MakeAnim(102, 22, duration, easing, delay);
            _cachedThumbHeightCollapse = MakeAnim(102, 22, duration, easing, delay);
            Timeline.SetDesiredFrameRate(_cachedThumbWidthCollapse, fps);
            Timeline.SetDesiredFrameRate(_cachedThumbHeightCollapse, fps);

            _cachedThumbRectCollapse = new RectAnimation(new Rect(0, 0, 102, 102), new Rect(0, 0, 22, 22), duration)
            {
                EasingFunction = easing,
                BeginTime = delay
            };
            Timeline.SetDesiredFrameRate(_cachedThumbRectCollapse, fps);

            _cachedThumbWidthCollapse.Freeze();
            _cachedThumbHeightCollapse.Freeze();
            _cachedThumbRectCollapse.Freeze();
        }

        return (_cachedThumbWidthCollapse, _cachedThumbHeightCollapse, _cachedThumbRectCollapse);
    }

    public void InvalidateThumbCache()
    {
        _cachedThumbWidthExpand = null;
        _cachedThumbHeightExpand = null;
        _cachedThumbRectExpand = null;
        _cachedThumbWidthCollapse = null;
        _cachedThumbHeightCollapse = null;
        _cachedThumbRectCollapse = null;
    }
}
