using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// How a spring moves, in the terms of SwiftUI's <c>Spring(duration:bounce:)</c>: <paramref name="Response"/>
/// is the period of the undamped spring, <paramref name="Bounce"/> 0 settles without overshoot and higher
/// values overshoot more (0.2 is about 1.5 %).
/// </summary>
public sealed record SpringMotion(TimeSpan Response, double Bounce)
{
    internal double AngularFrequency => 2 * Math.PI / Response.TotalSeconds;
    internal double DampingRatio => Math.Clamp(1 - Bounce, 0.05, 1);

    internal SpringDoubleAnimation Animate(double from, double to, double velocity = 0) =>
        new(from, to, velocity, AngularFrequency, DampingRatio);
}

/// <summary>
/// A damped spring as a WPF animation. It is solved in closed form, so it looks the same at any frame rate,
/// and it can start with a velocity: a spring sent somewhere new while still moving keeps its momentum
/// instead of starting again from rest. Its duration is the time it needs to settle.
/// </summary>
internal sealed class SpringDoubleAnimation : DoubleAnimationBase
{
    // Settled once the remaining swing is under a quarter of a device-independent pixel.
    private const double SettleDistance = 0.25;
    private static readonly TimeSpan LongestSettle = TimeSpan.FromSeconds(2);

    private readonly double _from;
    private readonly double _to;
    private readonly double _velocity;
    private readonly double _omega;
    private readonly double _zeta;

    internal SpringDoubleAnimation(double from, double to, double velocity, double angularFrequency, double dampingRatio)
    {
        _from = from;
        _to = to;
        _velocity = double.IsFinite(velocity) ? velocity : 0;
        _omega = angularFrequency;
        _zeta = dampingRatio;
        Duration = new Duration(SettleTime());
        FillBehavior = FillBehavior.HoldEnd;
    }

    internal double To => _to;

    /// <summary>Position after <paramref name="seconds"/>.</summary>
    internal double ValueAt(double seconds) => _to + Offset(seconds, out _);

    /// <summary>Speed in units per second after <paramref name="seconds"/>; zero once settled.</summary>
    internal double VelocityAt(double seconds)
    {
        if (seconds >= Duration.TimeSpan.TotalSeconds) return 0;
        Offset(seconds, out double velocity);
        return velocity;
    }

    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock.CurrentProgress is not { } progress || progress >= 1) return _to;
        return ValueAt(progress * Duration.TimeSpan.TotalSeconds);
    }

    protected override Freezable CreateInstanceCore() => new SpringDoubleAnimation(_from, _to, _velocity, _omega, _zeta);

    // Distance from the target (and its rate of change) for x'' + 2 zeta omega x' + omega^2 x = 0.
    private double Offset(double t, out double velocity)
    {
        double x0 = _from - _to, v0 = _velocity;
        if (t <= 0)
        {
            velocity = v0;
            return x0;
        }
        if (_zeta >= 1)
        {
            double decay = Math.Exp(-_omega * t);
            double b = v0 + _omega * x0;
            velocity = decay * (b - _omega * (x0 + b * t));
            return decay * (x0 + b * t);
        }
        double wd = _omega * Math.Sqrt(1 - _zeta * _zeta);
        double envelope = Math.Exp(-_zeta * _omega * t);
        double c = (v0 + _zeta * _omega * x0) / wd;
        double cos = Math.Cos(wd * t), sin = Math.Sin(wd * t);
        double offset = envelope * (x0 * cos + c * sin);
        velocity = envelope * ((c * wd - _zeta * _omega * x0) * cos - (x0 * wd + _zeta * _omega * c) * sin);
        return offset;
    }

    private TimeSpan SettleTime()
    {
        if (Math.Abs(_from - _to) < SettleDistance && Math.Abs(_velocity) < SettleDistance * _omega) return TimeSpan.Zero;
        const double step = 1.0 / 480;
        double lastMoving = 0;
        for (double t = step; t < LongestSettle.TotalSeconds; t += step)
        {
            double offset = Offset(t, out double velocity);
            if (Math.Abs(offset) >= SettleDistance || Math.Abs(velocity) >= SettleDistance * _omega) lastMoving = t;
            else if (t - lastMoving > 0.05) break;
        }
        return TimeSpan.FromSeconds(Math.Min(lastMoving + step, LongestSettle.TotalSeconds));
    }
}
