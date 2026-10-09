using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

internal sealed class DampedSpringEase : EasingFunctionBase
{
    // A zero-velocity step with zeta=.82 has about 1.1% overshoot. This is
    // relative to travel distance, not a fixed pixel bounce or a live solver.
    private const double NaturalFrequency = 12;
    private readonly double _dampedFrequency;
    private readonly double _endResponse;
    public double DampingRatio { get; }

    public DampedSpringEase() : this(0.82) { }

    internal DampedSpringEase(double dampingRatio)
    {
        if (!double.IsFinite(dampingRatio) || dampingRatio <= 0 || dampingRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        DampingRatio = dampingRatio;
        _dampedFrequency = NaturalFrequency * Math.Sqrt(1 - dampingRatio * dampingRatio);
        _endResponse = StepResponse(1);
        EasingMode = EasingMode.EaseOut;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        double time = Math.Clamp(normalizedTime, 0, 1);
        if (time == 0 || time == 1) return time;
        // WPF applies 1-core(1-t) for EaseOut. Normalize the response so the
        // final frame lands exactly on its target without a position jump.
        return 1 - StepResponse(1 - time) / _endResponse;
    }

    private double StepResponse(double time) => DampingRatio == 1
        ? 1 - Math.Exp(-NaturalFrequency * time) * (1 + NaturalFrequency * time)
        : 1 - Math.Exp(-DampingRatio * NaturalFrequency * time)
        * (Math.Cos(_dampedFrequency * time)
            + DampingRatio * NaturalFrequency / _dampedFrequency * Math.Sin(_dampedFrequency * time));

    protected override Freezable CreateInstanceCore() => new DampedSpringEase(DampingRatio);
}
