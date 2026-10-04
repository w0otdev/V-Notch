using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class TransitionRegressionTests
{
    [Fact]
    public void SpotlightHandoffPreservesTheExpandedMusicShape()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Media, "Music", isMusic: true);
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        long handoff = coordinator.BeginSpotlightHandoff("Spotlight");
        coordinator.CompleteSpotlightHandoff(handoff, restorePreviousView: true);
        Assert.Equal(NotchShapeState.MusicExpanded, coordinator.ShapeState);
        Assert.False(coordinator.IsTransitionActive);
    }

    [Fact]
    public void MusicExpansionCompletesInMusicExpandedShape()
    {
        var coordinator = new NotchTransitionCoordinator();
        Assert.True(coordinator.RequestView(NotchView.Media, "Music", isMusic: true));
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.Equal(NotchShapeState.MusicExpanded, coordinator.ShapeState);
    }

    [Fact]
    public void CancelingCollapseRestoresMusicShape()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Media, "Music", isMusic: true);
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        coordinator.RequestCollapse("Close", isMusic: true);
        coordinator.CancelTransition(coordinator.ActiveTransitionId, "Reversed");
        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.MusicExpanded, coordinator.ShapeState);
    }

    [Fact]
    public void MediaShapeCanChangeWithoutChangingTheView()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Media, "Open");
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.True(coordinator.RequestView(NotchView.Media, "Music", isMusic: true));
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.Equal(NotchShapeState.MusicExpanded, coordinator.ShapeState);
        Assert.True(coordinator.RequestView(NotchView.Media, "Restore widgets"));
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.Equal(NotchShapeState.Expanded, coordinator.ShapeState);
    }
}
