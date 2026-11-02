using System.Reflection;
using System.Windows.Controls;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassWorkerCadenceTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void GpuHandoffSpikesDoNotReduceConfiguredCaptureRate(int fps)
    {
        double requested = 1000.0 / fps;
        foreach (double externalWaitMs in new[] { 0.5, 10.0, 40.0, 100.0 })
            Assert.Equal(requested, LiquidGlassController.ChooseProcessingIntervalMs(requested, externalWaitMs, true));
        Assert.Equal(50, LiquidGlassController.ChooseProcessingIntervalMs(requested, 40, false));
    }

    [Fact]
    public void PointerRefreshResumesActiveCadenceFromASettledBackdrop()
    {
        Assert.Equal(100, LiquidGlassController.ChooseFrameIntervalMs(8, true, false, true, 1000));
        Assert.Equal(8, LiquidGlassController.ChooseFrameIntervalMs(8, true, false, true, 1000, forceRefresh: true));
    }

    [Fact]
    public void GpuWaitableTimerRetainsResolutionForCompletionAndUiRetries() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        var type = typeof(LiquidGlassController);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var requested = type.GetField("_renderTimerPeriodRequested", flags)!;
        type.GetField("_gpuMode", flags)!.SetValue(controller, true);
        controller.SetActiveForTest(true);
        controller.EnsureWaitableTimerForTest();
        try
        {
            type.GetMethod("RequestRenderTimerPeriod", flags)!.Invoke(controller, null);
            Assert.Equal(1, requested.GetValue(controller));
            controller.SleepUntilRenderDeadline(5);
            Assert.Equal(1, requested.GetValue(controller));
            controller.SetPresentationPaused(true);
            Assert.Equal(0, requested.GetValue(controller));
        }
        finally
        {
            type.GetMethod("ReleaseRenderTimerPeriod", flags)!.Invoke(controller, null);
            controller.SetActiveForTest(false);
            controller.DisposeWaitableTimerForTest();
            type.GetField("_gpuMode", flags)!.SetValue(controller, false);
        }
        Assert.Equal(0, requested.GetValue(controller));
    });
}
