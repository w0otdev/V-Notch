using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Controllers;

namespace VNotch.Models;

/// <param name="Spring">When set, the shell's width and height follow this spring (keeping their momentum when
/// a new transition interrupts them) instead of <paramref name="Duration"/> and <paramref name="Easing"/>.</param>
public sealed record TransitionMotionConfig(
    Duration Duration,
    IEasingFunction Easing,
    int TargetFps,
    bool ReduceMotion,
    VNotch.Services.SpringMotion? Spring = null
);

public sealed record TransitionPlan(
    long SessionId,
    NotchView FromView,
    NotchView TargetView,
    NotchShapeState TargetShape,
    double TargetWidth,
    double TargetHeight,
    double TargetCornerRadius,
    TransitionMotionConfig Motion,
    int NavigationDirection = 1
);

public enum TransitionExecutionStatus
{
    Completed,
    Canceled,
    Failed
}

public sealed record TransitionExecutionResult(
    long SessionId,
    TransitionExecutionStatus Status,
    string? Error = null
);
