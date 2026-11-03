using System.Reflection;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassWorkerCadenceTests
{
    [Fact]
    public void CompletingAnOlderFramePreservesRefreshesRequestedWhileItWasRendering() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LiquidGlassController);
        var version = type.GetField("_refreshVersion", flags)!;
        var needed = type.GetProperty("ForceRefreshNeeded", flags)!;
        var complete = type.GetMethod("CompleteRefresh", flags)!;
        controller.ForceRefresh();
        long captured = (long)version.GetValue(controller)!;
        // The UI changes the lens after the worker has captured its parameters.
        Task.Run(controller.ForceRefresh).GetAwaiter().GetResult();
        complete.Invoke(controller, [captured]);
        Assert.True((bool)needed.GetValue(controller)!);
        complete.Invoke(controller, [version.GetValue(controller)]);
        Assert.False((bool)needed.GetValue(controller)!);
    });

    [Fact]
    public void PendingWpfHandoffDoesNotConsumeAnotherCaptureDeadlineAndPresentationWakesWorker() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var controller = new LiquidGlassController(new Image(), () => hwnd,
            () => throw new InvalidOperationException("Pending handoff must not capture another frame"), activeFps: 120);
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LiquidGlassController);
        var pending = typeof(D3DImageFramePresenter).GetField("_pendingFrame", flags)!;
        var wake = (AutoResetEvent)type.GetField("_idleWakeEvent", flags)!.GetValue(controller)!;
        type.GetField("_gpuMode", flags)!.SetValue(controller, true);
        type.GetField("_d3dPresenter", flags)!.SetValue(controller, presenter);
        controller.SetActiveForTest(true);
        try
        {
            pending.SetValue(presenter, true);
            object[] args = [0, Stopwatch.StartNew(), 0.0, double.NegativeInfinity];
            Assert.True((bool)type.GetMethod("ExecuteWorkerFrame", flags)!.Invoke(controller, args)!);
            Assert.Equal(double.NegativeInfinity, (double)args[3]);
            pending.SetValue(presenter, false);
            wake.Reset();
            type.GetField("_waitingForGpuPresentation", flags)!.SetValue(controller, true);
            type.GetMethod("OnD3DFramePresented", flags)!.Invoke(controller, [new LiquidGlassController.GpuGeometry()]);
            Assert.True(wake.WaitOne(0));
            type.GetField("_waitingForGpuPresentation", flags)!.SetValue(controller, false);
            type.GetMethod("OnD3DFramePresented", flags)!.Invoke(controller, [new LiquidGlassController.GpuGeometry()]);
            Assert.False(wake.WaitOne(0));
        }
        finally
        {
            type.GetMethod("ReleaseRenderTimerPeriod", flags)!.Invoke(controller, null);
            controller.SetActiveForTest(false);
            type.GetField("_d3dPresenter", flags)!.SetValue(controller, null);
            type.GetField("_gpuMode", flags)!.SetValue(controller, false);
            window.Close();
        }
    });

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
