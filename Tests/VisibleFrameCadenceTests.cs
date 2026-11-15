using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class VisibleFrameCadenceTests
{
    private static long Ticks(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void IdleDustRetainsDeadlineRemaindersAtMillisecondPrecision(int displayHz) => SharedStaTestRunner.Run(() =>
    {
        var overlay = new DustParticleOverlay();
        overlay.ResetFramePacing(0);
        int expected = Math.Min(45, AnimationConfig.TargetFps);
        int updates = 0;
        double elapsed = 0;
        for (int frame = 1; frame <= displayHz * 10; frame++)
        {
            long now = (long)Math.Round(frame * 1000d / displayHz);
            if (overlay.TryAdvanceSimulation(now, isTransitioning: false, out float dt))
            {
                updates++;
                elapsed += dt;
            }
            Assert.False(overlay.TryAdvanceSimulation(now, isTransitioning: false, out _));
        }
        Assert.InRange(updates, expected * 10 - 1, expected * 10 + 1);
        Assert.InRange(elapsed, 9.95, 10.001);
    });

    [Fact]
    public void DustRespectsTheConfiguredCapAndResetsPhysicsAfterAStall() => SharedStaTestRunner.Run(() =>
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        int originalFps = (int)typeof(AnimationConfig).GetField("_configuredFps", flags)!.GetValue(null)!;
        bool originalAuto = (bool)typeof(AnimationConfig).GetField("_autoFps", flags)!.GetValue(null)!;
        try
        {
            AnimationConfig.Configure(30, autoFps: false);
            foreach (bool transitioning in new[] { false, true })
            {
                var overlay = new DustParticleOverlay();
                overlay.ResetFramePacing(0);
                int updates = 0;
                for (int frame = 1; frame <= 1440; frame++)
                    if (overlay.TryAdvanceSimulation((long)Math.Round(frame * 1000d / 144), transitioning, out _)) updates++;
                Assert.InRange(updates, 299, 301);
                Assert.True(overlay.TryAdvanceSimulation(15000, transitioning, out float dt));
                Assert.Equal(.05f, dt);
                Assert.False(overlay.TryAdvanceSimulation(15001, transitioning, out _));
                overlay.ResetFramePacing(20000);
                Assert.False(overlay.TryAdvanceSimulation(20001, transitioning, out _));
            }
        }
        finally { AnimationConfig.Configure(originalFps, originalAuto); }
    });

    [Fact]
    public void DustRejectsDelayedDuplicateCompositionCallbacksAndRestartsWithANewTimeOrigin() => SharedStaTestRunner.Run(() =>
    {
        var overlay = new DustParticleOverlay();
        overlay.ResetFramePacing(90000);
        var firstFrame = TimeSpan.FromSeconds(30);
        Assert.True(overlay.TryAdvanceSimulation(90001, false, out _, firstFrame));
        Assert.False(overlay.TryAdvanceSimulation(90050, false, out _, firstFrame));
        Assert.True(overlay.TryAdvanceSimulation(90051, false, out float dt, firstFrame + TimeSpan.FromSeconds(.04)));
        Assert.InRange(dt, .049f, .05f);

        overlay.ResetFramePacing(100000);
        Assert.True(overlay.TryAdvanceSimulation(100001, false, out dt, firstFrame));
        Assert.InRange(dt, .001f, .002f);
        Assert.False(overlay.TryAdvanceSimulation(100050, false, out _, firstFrame));
    });
}
