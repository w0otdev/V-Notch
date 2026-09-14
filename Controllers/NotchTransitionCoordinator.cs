using System;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public enum NotchShapeState
{
    Collapsed,
    Expanding,
    Expanded,
    Collapsing,
    MusicExpanding,
    MusicExpanded,
    MusicCollapsing,
    Hidden
}

public enum DisplayOwnership
{
    Notch,
    Spotlight
}

public sealed record NotchTransitionSnapshot(
    NotchView CurrentView,
    NotchView TargetView,
    NotchShapeState ShapeState,
    long ActiveTransitionId,
    bool IsTransitionActive,
    DisplayOwnership Ownership,
    NotchView ViewToRestoreOnSpotlightClose,
    bool HasPendingCountdownCompletion,
    bool IsEffectivelyVisible
);

public sealed class TransitionRequestEventArgs : EventArgs
{
    public long TransitionId { get; }
    public NotchView FromView { get; }
    public NotchView TargetView { get; }
    public NotchShapeState TargetShape { get; }
    public string Reason { get; }

    public TransitionRequestEventArgs(
        long transitionId,
        NotchView fromView,
        NotchView targetView,
        NotchShapeState targetShape,
        string reason)
    {
        TransitionId = transitionId;
        FromView = fromView;
        TargetView = targetView;
        TargetShape = targetShape;
        Reason = reason;
    }
}

/// <summary>
/// Quản lý tập trung toàn bộ trạng thái điều hướng notch, phiên chuyển cảnh và quyền hiển thị.
/// Nguồn chân lý duy nhất cho Navigation State theo Workstream A (implement.md).
/// </summary>
public sealed class NotchTransitionCoordinator
{
    private const string LogTag = "NOTCH-COORDINATOR";

    private readonly object _lock = new();
    private NotchView _currentView = NotchView.Compact;
    private NotchView _targetView = NotchView.Compact;
    private NotchShapeState _shapeState = NotchShapeState.Collapsed;
    private long _activeTransitionId;
    private bool _isTransitionActive;
    private DisplayOwnership _ownership = DisplayOwnership.Notch;
    private long _spotlightSessionId;
    private NotchView _viewToRestoreOnSpotlightClose = NotchView.Compact;
    private bool _hasPendingCountdownCompletion;
    private bool _isEffectivelyVisible = true;
    private bool _isDisposed;

    /// <summary>
    /// Cho phép view/window đăng ký tiền điều kiện từ chối yêu cầu chuyển cảnh trước khi cấp token (tránh kẹt animation).
    /// </summary>
    public Func<NotchView, string, bool>? CanInitiateTransition { get; set; }

    public event EventHandler<NotchTransitionSnapshot>? StateChanged;
    public event EventHandler<TransitionRequestEventArgs>? TransitionRequested;
    public event EventHandler? CountdownCompletionDisplayRequested;

    public NotchTransitionSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new NotchTransitionSnapshot(
                    _currentView,
                    _targetView,
                    _shapeState,
                    _activeTransitionId,
                    _isTransitionActive,
                    _ownership,
                    _viewToRestoreOnSpotlightClose,
                    _hasPendingCountdownCompletion,
                    _isEffectivelyVisible);
            }
        }
    }

    public NotchView CurrentView
    {
        get { lock (_lock) return _currentView; }
    }

    public NotchView TargetView
    {
        get { lock (_lock) return _targetView; }
    }

    public NotchShapeState ShapeState
    {
        get { lock (_lock) return _shapeState; }
    }

    public bool IsExpanded
    {
        get
        {
            lock (_lock)
            {
                return _shapeState is NotchShapeState.Expanded
                    or NotchShapeState.Expanding
                    or NotchShapeState.MusicExpanded
                    or NotchShapeState.MusicExpanding;
            }
        }
    }

    public bool IsTransitionActive
    {
        get { lock (_lock) return _isTransitionActive; }
    }

    public long ActiveTransitionId
    {
        get { lock (_lock) return _activeTransitionId; }
    }

    public DisplayOwnership Ownership
    {
        get { lock (_lock) return _ownership; }
    }

    public bool RequestView(NotchView target, string reason, bool isMusic = false)
    {
        TransitionRequestEventArgs? requestArgs = null;
        NotchTransitionSnapshot? newSnapshot = null;

        lock (_lock)
        {
            if (_isDisposed)
            {
                RuntimeLog.Warn(LogTag, $"RequestView({target}) rejected: coordinator is disposed");
                return false;
            }

            if (CanInitiateTransition != null && !CanInitiateTransition(target, reason))
            {
                RuntimeLog.Debug(LogTag, () => $"RequestView({target}) rejected by CanInitiateTransition predicate ({reason})");
                return false;
            }

            // Quy tắc: Spotlight đang giữ quyền hiển thị
            if (_ownership == DisplayOwnership.Spotlight)
            {
                if (target == NotchView.Timer && reason.Contains("Countdown", StringComparison.OrdinalIgnoreCase))
                {
                    _hasPendingCountdownCompletion = true;
                    RuntimeLog.Log(LogTag, "Countdown completed while Spotlight has ownership; recorded pending completion");
                }
                else
                {
                    RuntimeLog.Log(LogTag, $"RequestView({target}) deferred: Spotlight has ownership ({reason})");
                }
                return false;
            }

            bool isCountdownCompletion = string.Equals(reason, "CountdownCompletion", StringComparison.OrdinalIgnoreCase);

            // Quy tắc: Yêu cầu lại đúng đích đang mở và không trong chuyển cảnh
            if (target == _currentView && !_isTransitionActive && !isCountdownCompletion)
            {
                RuntimeLog.Debug(LogTag, () => $"RequestView({target}) ignored: already in target view ({reason})");
                return false;
            }

            // Đang chuyển tới đúng đích đó rồi
            if (target == _targetView && _isTransitionActive && !isCountdownCompletion)
            {
                RuntimeLog.Debug(LogTag, () => $"RequestView({target}) ignored: already transitioning to target ({reason})");
                return false;
            }

            long transitionId = ++_activeTransitionId;
            _targetView = target;
            _isTransitionActive = true;

            NotchShapeState targetShape;
            if (target == NotchView.Compact)
            {
                targetShape = isMusic ? NotchShapeState.MusicCollapsing : NotchShapeState.Collapsing;
            }
            else
            {
                targetShape = isMusic ? NotchShapeState.MusicExpanding : NotchShapeState.Expanding;
            }

            _shapeState = targetShape;

            requestArgs = new TransitionRequestEventArgs(
                transitionId,
                _currentView,
                _targetView,
                targetShape,
                reason);

            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Transition #{requestArgs.TransitionId} requested: {requestArgs.FromView} -> {requestArgs.TargetView} (Shape: {requestArgs.TargetShape}, Reason: {requestArgs.Reason})");
        StateChanged?.Invoke(this, newSnapshot);
        TransitionRequested?.Invoke(this, requestArgs);
        return true;
    }

    public bool RequestCollapse(string reason, bool isMusic = false)
    {
        return RequestView(NotchView.Compact, reason, isMusic);
    }

    public long BeginSpotlightHandoff(string reason)
    {
        long sessionId;
        NotchTransitionSnapshot newSnapshot;

        lock (_lock)
        {
            _viewToRestoreOnSpotlightClose = _currentView;

            _ownership = DisplayOwnership.Spotlight;
            sessionId = ++_spotlightSessionId;
            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Spotlight handoff session #{sessionId} started ({reason})");
        StateChanged?.Invoke(this, newSnapshot);
        return sessionId;
    }

    public void CompleteSpotlightHandoff(long sessionId, bool restorePreviousView = false)
    {
        NotchTransitionSnapshot? newSnapshot = null;
        NotchView? restoreView = null;
        bool showDeferredCountdown = false;

        lock (_lock)
        {
            if (sessionId != _spotlightSessionId)
            {
                RuntimeLog.Warn(LogTag, $"CompleteSpotlightHandoff rejected: session #{sessionId} != active #{_spotlightSessionId}");
                return;
            }

            _ownership = DisplayOwnership.Notch;

            if (_hasPendingCountdownCompletion)
            {
                _hasPendingCountdownCompletion = false;
                showDeferredCountdown = true;
                restoreView = NotchView.Timer;
            }
            else if (restorePreviousView)
            {
                restoreView = _viewToRestoreOnSpotlightClose;
            }

            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Spotlight handoff session #{sessionId} completed");
        StateChanged?.Invoke(this, newSnapshot);

        if (showDeferredCountdown)
        {
            CountdownCompletionDisplayRequested?.Invoke(this, EventArgs.Empty);
            RequestView(NotchView.Timer, "CountdownCompletion");
        }
        else if (restoreView.HasValue)
        {
            RequestView(restoreView.Value, "SpotlightHandoffRestoration");
        }
    }

    public long NextGeneration()
    {
        lock (_lock)
        {
            return ++_activeTransitionId;
        }
    }

    public void NotifyCountdownCompleted()
    {
        bool requestNow = false;

        lock (_lock)
        {
            if (_ownership == DisplayOwnership.Spotlight)
            {
                _hasPendingCountdownCompletion = true;
                RuntimeLog.Log(LogTag, "Countdown completion received during Spotlight; deferred");
                return;
            }

            requestNow = true;
        }

        if (requestNow)
        {
            RequestView(NotchView.Timer, "CountdownCompletion");
        }
    }

    public void CompleteTransition(long transitionId)
    {
        NotchTransitionSnapshot? newSnapshot = null;
        NotchView finalView;

        lock (_lock)
        {
            // Quy tắc: Callback phiên cũ đến muộn -> Bỏ qua, không được sửa state
            if (transitionId != _activeTransitionId)
            {
                RuntimeLog.Debug(LogTag, () => $"CompleteTransition ignored for stale transition #{transitionId} (active is #{_activeTransitionId})");
                return;
            }

            _currentView = _targetView;
            finalView = _currentView;
            _isTransitionActive = false;

            if (_currentView == NotchView.Compact)
            {
                _shapeState = NotchShapeState.Collapsed;
            }
            else
            {
                _shapeState = NotchShapeState.Expanded;
                _viewToRestoreOnSpotlightClose = _currentView;
            }

            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Transition #{transitionId} completed: View={finalView}, Shape={newSnapshot.ShapeState}");
        StateChanged?.Invoke(this, newSnapshot);
    }

    public void CancelTransition(long transitionId, string reason)
    {
        NotchTransitionSnapshot? newSnapshot = null;

        lock (_lock)
        {
            if (transitionId != _activeTransitionId)
            {
                RuntimeLog.Debug(LogTag, () => $"CancelTransition ignored for stale transition #{transitionId}");
                return;
            }

            _isTransitionActive = false;
            // Roll back shape state to match current stable view
            _shapeState = _currentView == NotchView.Compact ? NotchShapeState.Collapsed : NotchShapeState.Expanded;
            _targetView = _currentView;

            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Transition #{transitionId} canceled: {reason}");
        StateChanged?.Invoke(this, newSnapshot);
    }

    public void ForceState(NotchView view, NotchShapeState shape, string reason)
    {
        NotchTransitionSnapshot newSnapshot;

        lock (_lock)
        {
            _activeTransitionId++;
            _currentView = view;
            _targetView = view;
            _shapeState = shape;
            _isTransitionActive = false;
            _viewToRestoreOnSpotlightClose = view;

            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Warn(LogTag, $"ForceState: View={view}, Shape={shape} ({reason})");
        StateChanged?.Invoke(this, newSnapshot);
    }

    public void SetEffectivelyVisible(bool isVisible, string reason)
    {
        NotchTransitionSnapshot? newSnapshot = null;

        lock (_lock)
        {
            if (_isEffectivelyVisible == isVisible) return;
            _isEffectivelyVisible = isVisible;
            newSnapshot = CreateSnapshotUnderLock();
        }

        RuntimeLog.Log(LogTag, $"Visibility changed to {isVisible} ({reason})");
        StateChanged?.Invoke(this, newSnapshot);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            _isTransitionActive = false;
            _activeTransitionId++;
        }
    }

    private NotchTransitionSnapshot CreateSnapshotUnderLock()
    {
        return new NotchTransitionSnapshot(
            _currentView,
            _targetView,
            _shapeState,
            _activeTransitionId,
            _isTransitionActive,
            _ownership,
            _viewToRestoreOnSpotlightClose,
            _hasPendingCountdownCompletion,
            _isEffectivelyVisible);
    }
}
