using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AnimationFrameClockTests
{
    private static long Ticks(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    [Theory]
    [InlineData(120, 144)]
    [InlineData(60, 144)]
    [InlineData(30, 144)]
    [InlineData(45, 60)]
    [InlineData(120, 120)]
    [InlineData(120, 240)]
    [InlineData(240, 144)]
    [InlineData(550, 60)]
    public void MaintainsTargetAcrossDisplayRatesWithoutLosingRemainder(int target, int displayHz)
    {
        var clock = new AnimationFrameClock();
        clock.Reset(0);
        int updates = 0;
        double elapsed = 0;
        for (int frame = 1; frame <= displayHz * 10; frame++)
        {
            long now = Ticks(frame / (double)displayHz);
            if (clock.TryAdvance(now, target, out double seconds)) { updates++; elapsed += seconds; }
            Assert.False(clock.TryAdvance(now, target, out _));
        }
        Assert.InRange(updates, Math.Min(target, displayHz) * 10 - 1, Math.Min(target, displayHz) * 10 + 1);
        Assert.InRange(elapsed, 9.95, 10.001);
    }

    [Fact]
    public void UsesCompositionTimeAndRejectsRepeatedCallbacksForTheSameFrame()
    {
        var clock = new AnimationFrameClock();
        clock.Reset(Ticks(900)); // Stopwatch and WPF use different time origins.
        int updates = 0;
        for (int frame = 0; frame < 1440; frame++)
        {
            var time = TimeSpan.FromSeconds(30 + frame / 144d);
            if (clock.TryAdvance(time, 120, out double seconds))
            {
                updates++;
                Assert.InRange(seconds, .001, .05);
            }
            Assert.False(clock.TryAdvance(time, 120, out _));
        }
        Assert.InRange(updates, 1199, 1201);
    }

    [Fact]
    public void StallClampsPhysicsAndSkipsMissedFrames()
    {
        var clock = new AnimationFrameClock();
        clock.Reset(0);
        Assert.True(clock.TryAdvance(Ticks(.4), 120, out double seconds));
        Assert.Equal(.05, seconds);
        Assert.False(clock.TryAdvance(Ticks(.4002), 120, out _));
        Assert.True(clock.TryAdvance(Ticks(.41), 120, out seconds));
        Assert.InRange(seconds, .0099, .0101);
    }

    [Fact]
    public void TargetChangesAndRestartUseCurrentSessionTime()
    {
        var clock = new AnimationFrameClock();
        clock.Reset(0);
        Assert.False(clock.TryAdvance(Ticks(.01), 60, out _));
        Assert.True(clock.TryAdvance(Ticks(.01), 120, out _));
        clock.Reset(Ticks(10));
        Assert.False(clock.TryAdvance(Ticks(10.001), 120, out _));
        Assert.True(clock.TryAdvance(Ticks(10.009), 120, out double seconds));
        Assert.InRange(seconds, .0089, .0091);
    }
}
