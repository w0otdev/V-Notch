using System;
using VNotch.Models;

namespace VNotch.Services;

public class NotchStateManager
{
    private NotchState _currentState = NotchState.Collapsed;
    private NotchState _previousState = NotchState.Collapsed;
    private readonly object _stateLock = new();
    private DateTime _lastTransitionUtc = DateTime.MinValue;

    public event EventHandler<NotchStateChangedEventArgs>? StateChanged;

    public NotchState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    public NotchState PreviousState
    {
        get { lock (_stateLock) return _previousState; }
    }
    public TimeSpan TimeSinceLastTransition => DateTime.UtcNow - _lastTransitionUtc;

    public bool IsExpanded => CurrentState == NotchState.Expanded || CurrentState == NotchState.SecondaryView || CurrentState == NotchState.CameraExpanded;
    public bool IsMusicExpanded => CurrentState == NotchState.MusicExpanded;
    public bool IsSecondaryView => CurrentState == NotchState.SecondaryView;
    public bool IsTransitioning => CurrentState is NotchState.Expanding or NotchState.Collapsing or NotchState.MusicExpanding or NotchState.MusicCollapsing;

    public bool IsAnimating { get; set; }

    public bool IsLyricsActive { get; set; }

    public bool IsTimerView { get; set; }

    public bool IsAudioView { get; set; }

    public bool CanTransitionTo(NotchState target)
    {
        var current = CurrentState;
        return target switch
        {
            NotchState.Expanding => current == NotchState.Collapsed,
            NotchState.Expanded => current == NotchState.Expanding || current == NotchState.SecondaryView || current == NotchState.CameraExpanded,
            NotchState.Collapsing => current == NotchState.Expanded || current == NotchState.SecondaryView || current == NotchState.CameraExpanded,
            NotchState.Collapsed => current == NotchState.Collapsing || current == NotchState.MusicCollapsing,
            NotchState.SecondaryView => current == NotchState.Expanded,
            NotchState.CameraExpanded => current == NotchState.SecondaryView,
            NotchState.MusicExpanding => current == NotchState.Collapsed,
            NotchState.MusicExpanded => current == NotchState.MusicExpanding,
            NotchState.MusicCollapsing => current == NotchState.MusicExpanded || current == NotchState.MusicExpanding,
            NotchState.Hidden => true,
            _ => false
        };
    }
    public bool TryTransitionTo(NotchState target)
    {
        lock (_stateLock)
        {
            if (!CanTransitionTo(target))
            {
                RuntimeLog.Warn("STATE", $"Invalid transition: {_currentState} → {target}");
                return false;
            }

            _previousState = _currentState;
            _currentState = target;
            _lastTransitionUtc = DateTime.UtcNow;
        }

        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(_previousState, target));
        return true;
    }
    public void ForceState(NotchState target)
    {
        lock (_stateLock)
        {
            _previousState = _currentState;
            _currentState = target;
            _lastTransitionUtc = DateTime.UtcNow;
        }

        RuntimeLog.Log("STATE", $"Forced: {_previousState} → {target}");
        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(_previousState, target));
    }
    public void RecoverFromStuckTransition()
    {
        var current = CurrentState;
        if (!IsTransitioning) return;

        var recoveryTarget = NotchState.Collapsed;

        RuntimeLog.Warn("STATE", $"Recovering from stuck state: {current} → {recoveryTarget} (stuck for {TimeSinceLastTransition.TotalMilliseconds:0}ms)");
        ForceState(recoveryTarget);
    }
    public NotchState GetCollapseTarget()
    {
        return CurrentState switch
        {
            NotchState.MusicExpanded or NotchState.MusicExpanding => NotchState.MusicCollapsing,
            NotchState.Expanded or NotchState.SecondaryView or NotchState.CameraExpanded => NotchState.Collapsing,
            _ => NotchState.Collapsed
        };
    }

    public bool CanExpand() => CurrentState == NotchState.Collapsed;
    public bool CanCollapse() => IsExpanded || IsMusicExpanded;

    public void Collapse() => TryTransitionTo(NotchState.Collapsing);
    public void ExpandCompact() => TryTransitionTo(NotchState.Expanding);
    public void ExpandMedium() => TryTransitionTo(NotchState.Expanding);
    public void ExpandLarge() => TryTransitionTo(NotchState.Expanding);

    public void Hide() => ForceState(NotchState.Hidden);
    public void Show()
    {
        if (CurrentState == NotchState.Hidden)
            ForceState(NotchState.Collapsed);
    }
}

public enum NotchState
{
    Collapsed,
    Expanding,
    Expanded,
    Collapsing,
    SecondaryView,
    CameraExpanded,
    MusicExpanding,
    MusicExpanded,
    MusicCollapsing,
    Hidden
}

public enum NotchExpandMode
{
    Compact,
    Medium,
    Large
}

public class NotchStateChangedEventArgs : EventArgs
{
    public NotchState PreviousState { get; }
    public NotchState NewState { get; }

    public NotchStateChangedEventArgs(NotchState previousState, NotchState newState)
    {
        PreviousState = previousState;
        NewState = newState;
    }
}
