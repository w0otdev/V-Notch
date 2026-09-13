using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Controllers;

namespace VNotch.Models;

public sealed record TransitionMotionConfig(
    Duration Duration,
    IEasingFunction Easing,
    int TargetFps,
    bool ReduceMotion
);

public sealed record TransitionPlan(
    long SessionId,
    NotchView FromView,
    NotchView TargetView,
    NotchShapeState TargetShape,
    double TargetWidth,
    double TargetHeight,
    double TargetCornerRadius,
    TransitionMotionConfig Motion
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
