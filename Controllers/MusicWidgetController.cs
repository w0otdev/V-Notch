using System;
using VNotch.Models;

namespace VNotch.Controllers;

public sealed class MusicWidgetController
{
    private readonly NotchTransitionCoordinator _coordinator;

    private double _musicWidgetSmallWidth = 0;

    public bool IsMusicExpanded => _coordinator.ShapeState == NotchShapeState.MusicExpanded;
    public bool IsMusicAnimating => _coordinator.IsTransitionActive &&
        _coordinator.ShapeState is NotchShapeState.MusicExpanding or NotchShapeState.MusicCollapsing;
    public double SmallWidth => _musicWidgetSmallWidth;

    public event Action? ExpandRequested;
    public event Action? CollapseRequested;
    public event Action? LayoutUpdateRequested;

    public MusicWidgetController(NotchTransitionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public bool TryBeginExpand(double currentWidgetWidth)
    {
        if (_coordinator.IsTransitionActive) return false;
        _musicWidgetSmallWidth = currentWidgetWidth;

        if (!_coordinator.RequestView(NotchView.Media, "MusicWidgetController.Expand", isMusic: true)) return false;

        ExpandRequested?.Invoke();
        return true;
    }

    public void CompleteExpand(long transitionId)
    {
        if (!_coordinator.IsTransitionActive || transitionId != _coordinator.ActiveTransitionId) return;
        _coordinator.CompleteTransition(transitionId);
        LayoutUpdateRequested?.Invoke();
    }

    public bool TryBeginCollapse()
    {
        if (_coordinator.IsTransitionActive || !IsMusicExpanded) return false;
        if (!_coordinator.RequestView(NotchView.Media, "MusicWidgetController.Collapse")) return false;

        CollapseRequested?.Invoke();
        return true;
    }

    public void CompleteCollapse(long transitionId)
    {
        if (!_coordinator.IsTransitionActive || transitionId != _coordinator.ActiveTransitionId) return;
        _coordinator.CompleteTransition(transitionId);
        LayoutUpdateRequested?.Invoke();
    }

    public double GetCollapseTargetWidth(double expandedContentWidth)
    {
        return _musicWidgetSmallWidth > 0
            ? _musicWidgetSmallWidth
            : (expandedContentWidth / 3.0) - 8;
    }

    public record ProgressLayout(
        double ContainerHeight,
        double BarHeight,
        double BarRadius,
        double TimeTopMargin,
        double TimeSideMargin,
        bool UseCompactLayout);

    public ProgressLayout ComputeProgressLayout()
    {
        bool useCompact = !IsMusicExpanded;
        return new ProgressLayout(
            ContainerHeight: useCompact ? 8 : 12,
            BarHeight: useCompact ? 3 : 4,
            BarRadius: useCompact ? 1.5 : 2.0,
            TimeTopMargin: useCompact ? 4 : 2,
            TimeSideMargin: useCompact ? 4 : 6,
            UseCompactLayout: useCompact);
    }

    public static double ComputeVisibleTextWidth(
        double widgetWidth, double thumbnailWidth, double thumbnailGap,
        double infoSectionWidth, double fallbackWidth)
    {
        if (widgetWidth > 0)
        {
            double available = widgetWidth - thumbnailWidth - thumbnailGap - 4;
            return Math.Max(0, Math.Min(340, available));
        }

        if (infoSectionWidth > 0)
            return Math.Max(0, Math.Min(340, infoSectionWidth));

        return fallbackWidth;
    }
}
