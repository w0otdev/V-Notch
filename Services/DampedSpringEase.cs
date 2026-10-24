using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// WPF easing wrapper for the native damped-spring curve (Native/Services/AnimationMath.cpp).
/// </summary>
internal sealed partial class DampedSpringEase : EasingFunctionBase
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_damped_spring_ease(double dampingRatio, double normalizedTime, out double value);

    // A zero-velocity step with zeta=.82 has about 1.1% overshoot. This is
    // relative to travel distance, not a fixed pixel bounce or a live solver.
    public double DampingRatio { get; }

    public DampedSpringEase() : this(0.82) { }

    internal DampedSpringEase(double dampingRatio)
    {
        if (vn_damped_spring_ease(dampingRatio, 0.5, out _) != 0)
            throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        DampingRatio = dampingRatio;
        EasingMode = EasingMode.EaseOut;
    }

    protected override double EaseInCore(double normalizedTime) =>
        vn_damped_spring_ease(DampingRatio, normalizedTime, out double value) == 0 ? value : Math.Clamp(normalizedTime, 0, 1);

    protected override Freezable CreateInstanceCore() => new DampedSpringEase(DampingRatio);
}