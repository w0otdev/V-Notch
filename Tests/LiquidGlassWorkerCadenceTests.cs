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
        controller.ForceRefresh();
        long captured = controller.RefreshVersion;
        // The UI changes the lens after the worker has captured its parameters.
        Task.Run(controller.ForceRefresh).GetAwaiter().GetResult();
        controller.CompleteRefresh(captured);
        Assert.True(controller.ForceRefreshNeeded);
        controller.CompleteRefresh(controller.RefreshVersion);
        Assert.False(controller.ForceRefreshNeeded);
    });

    [Fact]
    public void PendingWpfHandoffDoesNotConsumeAnotherCaptureDeadlineAndPresentationWakesWorker() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var controller = new LiquidGlassController(new Image(), () => hwnd,
            () => throw new InvalidOperationException("Pending handoff must not capture another frame"), activeFps: 120);
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8);
        controller.ConfigureGpuForTest(true, presenter);
        controller.SetActiveForTest(true);
        try
        {
            presenter.SetPendingFrameForTest(true);
            double nextFrame = 0, lastCapture = double.NegativeInfinity;
            Assert.True(controller.ExecuteWorkerFrame(0, Stopwatch.StartNew(), ref nextFrame, ref lastCapture));
            Assert.Equal(double.NegativeInfinity, lastCapture);
            presenter.SetPendingFrameForTest(false);
            controller.ResetWorkerWake();
            controller.SetWaitingForPresentation(true);
            controller.OnD3DFramePresented(new LiquidGlassController.GpuGeometry());
            Assert.True(controller.WorkerWake.WaitOne(0));
            controller.SetWaitingForPresentation(false);
            controller.OnD3DFramePresented(new LiquidGlassController.GpuGeometry());
            Assert.False(controller.WorkerWake.WaitOne(0));
        }
        finally
        {
            controller.ReleaseRenderTimerPeriod();
            controller.SetActiveForTest(false);
            controller.ConfigureGpuForTest(false);
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
        Assert.Equal(1000.0 / 30, LiquidGlassController.ChooseFrameIntervalMs(8, true, false, true, 1000));
        Assert.Equal(50, LiquidGlassController.ChooseFrameIntervalMs(50, true, false, true, 1000));
        Assert.Equal(8, LiquidGlassController.ChooseFrameIntervalMs(8, true, false, true, 1000, forceRefresh: true));
    }

    [Fact]
    public void GpuWaitableTimerRetainsResolutionForCompletionAndUiRetries() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        controller.ConfigureGpuForTest(true);
        controller.SetActiveForTest(true);
        controller.EnsureWaitableTimerForTest();
        try
        {
            controller.RequestRenderTimerPeriod();
            Assert.Equal(1, controller.RenderTimerPeriodRequested);
            controller.SleepUntilRenderDeadline(5);
            Assert.Equal(1, controller.RenderTimerPeriodRequested);
            controller.SetPresentationPaused(true);
            Assert.Equal(0, controller.RenderTimerPeriodRequested);
        }
        finally
        {
            controller.ReleaseRenderTimerPeriod();
            controller.SetActiveForTest(false);
            controller.DisposeWaitableTimerForTest();
            controller.ConfigureGpuForTest(false);
        }
        Assert.Equal(0, controller.RenderTimerPeriodRequested);
    });
}
