using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class NotchControllerTests
{
    [Fact]
    public void AnimationControllerPublishesOnlyTheCurrentTransitionCompletion()
    {
        var coordinator = new NotchTransitionCoordinator();
        var controller = new NotchAnimationController(coordinator);
        int completed = 0;
        controller.ExpandCompleted += () => completed++;
        Assert.True(controller.TryBeginExpand());
        long stale = coordinator.ActiveTransitionId;
        coordinator.RequestView(NotchView.Timer, "Supersede");
        controller.CompleteExpand(stale);
        Assert.True(controller.IsAnimating);
        Assert.Equal(0, completed);
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.True(controller.IsExpanded);
        Assert.False(controller.CanExpand);
        Assert.True(controller.TryBeginCollapse());
        controller.CompleteCollapse(coordinator.ActiveTransitionId);
        Assert.False(controller.IsAnimating);
        Assert.False(controller.IsExpanded);
        Assert.True(controller.CanExpand);
    }

    [Fact]
    public void MusicControllerSharesNavigationAndCollapsesBackToMediaWidgets()
    {
        var coordinator = new NotchTransitionCoordinator();
        var music = new MusicWidgetController(coordinator);
        var animation = new NotchAnimationController(coordinator);
        Assert.True(music.TryBeginExpand(150));
        Assert.True(music.IsMusicAnimating);
        Assert.False(music.IsMusicExpanded);
        Assert.True(animation.IsAnimating);
        Assert.False(animation.TryBeginExpand());
        music.CompleteExpand(coordinator.ActiveTransitionId);
        Assert.True(music.IsMusicExpanded);
        Assert.True(animation.IsExpanded);
        Assert.False(music.ComputeProgressLayout().UseCompactLayout);
        Assert.True(music.TryBeginCollapse());
        Assert.Equal(NotchShapeState.MusicCollapsing, coordinator.ShapeState);
        music.CompleteCollapse(coordinator.ActiveTransitionId);
        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Expanded, coordinator.ShapeState);
        Assert.False(music.IsMusicExpanded);
        Assert.False(music.IsMusicAnimating);
        Assert.True(music.ComputeProgressLayout().UseCompactLayout);
        Assert.Equal(150, music.GetCollapseTargetWidth(480));
    }

    [Fact]
    public void StaleMusicCallbackDoesNotFinishANewerNavigationRequest()
    {
        var coordinator = new NotchTransitionCoordinator();
        var music = new MusicWidgetController(coordinator);
        int updates = 0;
        music.LayoutUpdateRequested += () => updates++;
        music.TryBeginExpand(150);
        long stale = coordinator.ActiveTransitionId;
        coordinator.RequestView(NotchView.Secondary, "Supersede");
        music.CompleteExpand(stale);
        Assert.Equal(0, updates);
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
    }
}
