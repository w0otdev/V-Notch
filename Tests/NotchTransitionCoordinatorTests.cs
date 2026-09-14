using System;
using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public class NotchTransitionCoordinatorTests
{
    [Fact]
    public void RequestView_WhenDifferentView_FiresTransitionRequestedAndUpdatesTarget()
    {
        var coordinator = new NotchTransitionCoordinator();
        TransitionRequestEventArgs? receivedArgs = null;
        coordinator.TransitionRequested += (_, args) => receivedArgs = args;

        bool accepted = coordinator.RequestView(NotchView.Timer, "UserClick");

        Assert.True(accepted);
        Assert.NotNull(receivedArgs);
        Assert.Equal(NotchView.Timer, receivedArgs.TargetView);
        Assert.Equal(NotchView.Compact, receivedArgs.FromView);
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Timer, coordinator.TargetView);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
    }

    [Fact]
    public void CompleteTransition_WhenMatchingId_ConfirmsCurrentViewAndResetsTransition()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Timer, "UserClick");
        long transitionId = coordinator.ActiveTransitionId;

        coordinator.CompleteTransition(transitionId);

        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Timer, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Expanded, coordinator.Snapshot.ShapeState);
    }

    [Fact]
    public void CompleteTransition_WhenStaleId_IsIgnored()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Timer, "First");
        long staleId = coordinator.ActiveTransitionId;

        coordinator.RequestView(NotchView.AudioMixer, "Second");
        long newId = coordinator.ActiveTransitionId;

        // Callback from first animation arrives late
        coordinator.CompleteTransition(staleId);

        // State must remain target AudioMixer, not Timer
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.AudioMixer, coordinator.TargetView);

        // New callback completes successfully
        coordinator.CompleteTransition(newId);
        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.AudioMixer, coordinator.CurrentView);
    }

    [Fact]
    public void SpotlightHandoff_DefersCountdownCompletionUntilHandoffCompletes()
    {
        var coordinator = new NotchTransitionCoordinator();
        long sessionId = coordinator.BeginSpotlightHandoff("SpotlightOpened");

        Assert.Equal(DisplayOwnership.Spotlight, coordinator.Ownership);

        bool displayRequested = false;
        coordinator.CountdownCompletionDisplayRequested += (_, _) => displayRequested = true;

        // Countdown completes while Spotlight owns display
        coordinator.NotifyCountdownCompleted();

        Assert.False(displayRequested);
        Assert.True(coordinator.Snapshot.HasPendingCountdownCompletion);

        // Spotlight closes and completes handoff
        coordinator.CompleteSpotlightHandoff(sessionId);

        Assert.True(displayRequested);
        Assert.Equal(DisplayOwnership.Notch, coordinator.Ownership);
        Assert.False(coordinator.Snapshot.HasPendingCountdownCompletion);
    }

    [Fact]
    public void RapidViewTransitions_ChainCorrectlyAndOnlyLatestSucceeds()
    {
        var coordinator = new NotchTransitionCoordinator();

        // 1. User switches to Timer
        coordinator.RequestView(NotchView.Timer, "UserClickTimer");
        long timerId = coordinator.ActiveTransitionId;
        Assert.Equal(NotchView.Timer, coordinator.TargetView);

        // 2. Before Timer finishes, user rapidly switches to Secondary
        coordinator.RequestView(NotchView.Secondary, "UserClickSecondary");
        long secondaryId = coordinator.ActiveTransitionId;
        Assert.True(secondaryId > timerId);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);

        // 3. Before Secondary finishes, user rapidly switches to Media
        coordinator.RequestView(NotchView.Media, "UserClickMedia");
        long mediaId = coordinator.ActiveTransitionId;
        Assert.True(mediaId > secondaryId);
        Assert.Equal(NotchView.Media, coordinator.TargetView);

        // Stale callback from Timer arrives -> must be ignored
        coordinator.CompleteTransition(timerId);
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Media, coordinator.TargetView);

        // Stale callback from Secondary arrives -> must be ignored
        coordinator.CompleteTransition(secondaryId);
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Media, coordinator.TargetView);

        // Final callback from Media arrives -> completes successfully
        coordinator.CompleteTransition(mediaId);
        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Expanded, coordinator.Snapshot.ShapeState);
    }

    [Fact]
    public void RequestView_SameAsCurrentView_WhenNotTransitioning_IsIgnored()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Media, "OpenMedia");
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);

        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.False(coordinator.IsTransitionActive);

        // Requesting Media again while already in Media and not transitioning
        bool requested = coordinator.RequestView(NotchView.Media, "DuplicateOpen");
        Assert.False(requested);
        Assert.False(coordinator.IsTransitionActive);
    }

    [Fact]
    public void RequestCollapse_TransitionsToCompact()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Timer, "OpenTimer");
        coordinator.CompleteTransition(coordinator.ActiveTransitionId);

        Assert.Equal(NotchView.Timer, coordinator.CurrentView);

        bool collapseRequested = coordinator.RequestCollapse("UserGesture");
        Assert.True(collapseRequested);
        Assert.Equal(NotchView.Compact, coordinator.TargetView);
        Assert.Equal(NotchShapeState.Collapsing, coordinator.Snapshot.ShapeState);

        coordinator.CompleteTransition(coordinator.ActiveTransitionId);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Collapsed, coordinator.Snapshot.ShapeState);
    }

    [Fact]
    public void SpotlightHandoff_WhenCollapsed_DoesNotAutoExpandOnComplete()
    {
        var coordinator = new NotchTransitionCoordinator();
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);

        // Handoff to Spotlight while compact
        long sessionId = coordinator.BeginSpotlightHandoff("SpotlightOpened");
        Assert.Equal(DisplayOwnership.Spotlight, coordinator.Ownership);

        bool transitionRequested = false;
        coordinator.TransitionRequested += (_, _) => transitionRequested = true;

        // Spotlight completes with restorePreviousView = true
        coordinator.CompleteSpotlightHandoff(sessionId, restorePreviousView: true);

        // Must remain in Compact view and NOT request expanding to Media
        Assert.False(transitionRequested);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.False(coordinator.IsTransitionActive);
    }

    [Fact]
    public void CanInitiateTransition_WhenPredicateReturnsFalse_RejectsRequestWithoutTransitionActive()
    {
        var coordinator = new NotchTransitionCoordinator();
        bool allowTransitions = false;
        coordinator.CanInitiateTransition = (target, reason) => allowTransitions;

        bool accepted = coordinator.RequestView(NotchView.Media, "UserClickWhileGreeting");
        Assert.False(accepted);
        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(0, coordinator.ActiveTransitionId);

        // When allowed again, transition succeeds
        allowTransitions = true;
        bool acceptedAfter = coordinator.RequestView(NotchView.Media, "UserClickAfterGreeting");
        Assert.True(acceptedAfter);
        Assert.True(coordinator.IsTransitionActive);
        Assert.Equal(1, coordinator.ActiveTransitionId);
    }

    [Fact]
    public void CancelTransition_ClearsTransitionActiveAndAllowsSubsequentRequest()
    {
        var coordinator = new NotchTransitionCoordinator();
        coordinator.RequestView(NotchView.Media, "OpenMedia");
        long id = coordinator.ActiveTransitionId;
        Assert.True(coordinator.IsTransitionActive);

        // Transition gets canceled (e.g. Greeting active or debug lock)
        coordinator.CancelTransition(id, "GreetingActive");
        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);

        // Re-requesting Media now succeeds because coordinator is no longer stuck
        bool secondAccepted = coordinator.RequestView(NotchView.Media, "OpenMediaAgain");
        Assert.True(secondAccepted);
        Assert.True(coordinator.IsTransitionActive);
    }
}
