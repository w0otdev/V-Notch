using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// WPF interpolation over an immutable native damped-spring curve (Native/Services/AnimationMath.cpp).
/// </summary>
internal sealed partial class DampedSpringEase : EasingFunctionBase
{
    private const int SampleIntervals = 256;
    // Four native-prepared polynomial coefficients per interval keep frame
    // evaluation cheap without retaining thousands of dense sample values.
    // Only the two built-in curves are retained globally. Custom damping ratios
    // belong to their instance instead of growing an unbounded cache.
    private static readonly Lazy<double[]> BounceSamples = new(() => SampleCurve(.82));
    private static readonly Lazy<double[]> MoveSamples = new(() => SampleCurve(1));
    private readonly double[] _samples;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_damped_spring_build_segments(double dampingRatio, Span<double> coefficients, int coefficientCount);

    // A zero-velocity step with zeta=.82 has about 1.1% overshoot. This is
    // relative to travel distance, not a fixed pixel bounce or a live solver.
    public double DampingRatio { get; }

    public DampedSpringEase() : this(0.82) { }

    internal DampedSpringEase(double dampingRatio)
    {
        if (!double.IsFinite(dampingRatio) || dampingRatio <= 0 || dampingRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        DampingRatio = dampingRatio;
        _samples = dampingRatio == .82 ? BounceSamples.Value : dampingRatio == 1 ? MoveSamples.Value : SampleCurve(dampingRatio);
        EasingMode = EasingMode.EaseOut;
    }

    private static double[] SampleCurve(double dampingRatio)
    {
        var samples = new double[SampleIntervals * 4];
        if (vn_damped_spring_build_segments(dampingRatio, samples, samples.Length) != 0)
            throw new InvalidOperationException("Could not sample the native spring curve.");
        return samples;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (!double.IsFinite(normalizedTime)) return normalizedTime > 0 ? 1 : 0;
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;
        double position = normalizedTime * SampleIntervals;
        int segment = (int)position;
        double time = position - segment;
        int index = segment * 4;
        return ((_samples[index + 3] * time + _samples[index + 2]) * time + _samples[index + 1]) * time + _samples[index];
    }

    protected override Freezable CreateInstanceCore() => new DampedSpringEase(DampingRatio);
}
