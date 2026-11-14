using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpringAnimationTests
{
    [Fact]
    public void ShellSpringsKeepTheTimingOfTheCurvesTheyReplaced()
    {
        var open = AnimationPrimitives.ShellOpenSpring.Animate(68, 528);
        var close = AnimationPrimitives.ShellCloseSpring.Animate(528, 68);

        Assert.InRange(TimeTo(open, 0.9), 0.17, 0.21);
        Assert.InRange(TimeTo(close, 0.9), 0.14, 0.18);
        // Opening overshoots a little (alive, not wobbly); closing never undershoots the closed notch.
        Assert.InRange(Overshoot(open), 0.01, 0.02);
        Assert.Equal(0, Overshoot(close), 3);
        Assert.InRange(open.Duration.TimeSpan.TotalMilliseconds, 400, 700);
        Assert.Equal(528, open.ValueAt(open.Duration.TimeSpan.TotalSeconds), 0);
    }

    [Fact]
    public void VelocityIsTheSlopeOfThePosition()
    {
        var spring = AnimationPrimitives.ShellOpenSpring.Animate(0, 300, velocity: -400);
        for (double t = 0.01; t < 0.5; t += 0.037)
        {
            double slope = (spring.ValueAt(t + 1e-5) - spring.ValueAt(t - 1e-5)) / 2e-5;
            Assert.Equal(slope, spring.VelocityAt(t), 1);
        }
        Assert.Equal(-400, spring.VelocityAt(0), 6);
    }

    [Fact]
    public void ASpringSentBackMidwayKeepsItsMomentum()
    {
        var opening = AnimationPrimitives.ShellOpenSpring.Animate(68, 528);
        double t = 0.08;
        double position = opening.ValueAt(t), velocity = opening.VelocityAt(t);
        Assert.True(velocity > 1000, "still moving fast when interrupted");

        var closing = AnimationPrimitives.ShellCloseSpring.Animate(position, 68, velocity);
        Assert.Equal(position, closing.ValueAt(0), 6);
        Assert.Equal(velocity, closing.VelocityAt(0), 6);
        // It carries on outward for a moment before turning back, instead of reversing on the spot.
        Assert.True(closing.ValueAt(0.02) > position);
        Assert.Equal(68, closing.ValueAt(closing.Duration.TimeSpan.TotalSeconds), 0);
    }

    private static double TimeTo(SpringDoubleAnimation spring, double fraction)
    {
        double from = spring.ValueAt(0), to = spring.To;
        for (double t = 0; t < 2; t += 0.0005)
            if ((spring.ValueAt(t) - from) / (to - from) >= fraction) return t;
        return double.PositiveInfinity;
    }

    private static double Overshoot(SpringDoubleAnimation spring)
    {
        double from = spring.ValueAt(0), to = spring.To, worst = 0;
        for (double t = 0; t < 2; t += 0.0005)
            worst = Math.Max(worst, (spring.ValueAt(t) - to) / (to - from));
        return worst;
    }
}
