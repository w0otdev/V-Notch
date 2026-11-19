namespace VNotch.Models;

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

public interface INotchNavigation
{
    NotchTransitionSnapshot Snapshot { get; }
    event EventHandler<NotchTransitionSnapshot>? StateChanged;
    bool RequestView(NotchView target, string reason, bool isMusic = false);
    bool RequestCollapse(string reason, bool isMusic = false);
}
