using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class GlassReliabilityTests
{
    [Theory]
    [InlineData(0, 0, 2.0, 3200, 1200)]
    [InlineData(0, 0, 3.0, 4096, 1800)]
    [InlineData(3800, 1400, 1.0, 3800, 1400)]
    public void MainCaptureCapacityScalesForDpiAndGrowsForActualBounds(int width, int height, double dpi, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), MainWindow.GetGlassCaptureCapacity(width, height, dpi));

    [Theory]
    [InlineData(30, 240, 30)]
    [InlineData(60, 165, 60)]
    [InlineData(144, 60, 60)]
    [InlineData(60, 0, 60)]
    public void CaptureCadenceRespectsBothUserCapAndMonitor(int configured, int monitor, int expected) =>
        Assert.Equal(1000.0 / expected, LiquidGlassController.ComputeCaptureIntervalMs(configured, monitor), 6);

    [Fact]
    public void ChangingFpsAfterDisplayDetectionHonorsExplicitSixty() => SharedStaTestRunner.Run(() =>
    {
        var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null, activeFps: 144);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LiquidGlassController).GetField("_displayRefreshHz", flags)!.SetValue(controller, 240);
        controller.UpdateFps(30);
        Assert.Equal(1000.0 / 30, (double)typeof(LiquidGlassController).GetField("_activeIntervalMs", flags)!.GetValue(controller)!, 6);
        controller.UpdateFps(60);
        Assert.Equal(1000.0 / 60, (double)typeof(LiquidGlassController).GetField("_activeIntervalMs", flags)!.GetValue(controller)!, 6);
    });

    [Fact]
    public void FullSurfaceEnvelopeCoversBothSidesOfCenteredExpansion() => SharedStaTestRunner.Run(() =>
    {
        int desktopWidth = VNotch.Services.Win32Interop.GetSystemMetrics(VNotch.Services.Win32Interop.SM_CXVIRTUALSCREEN);
        int desktopLeft = VNotch.Services.Win32Interop.GetSystemMetrics(VNotch.Services.Win32Interop.SM_XVIRTUALSCREEN);
        int desktopTop = VNotch.Services.Win32Interop.GetSystemMetrics(VNotch.Services.Win32Interop.SM_YVIRTUALSCREEN);
        int center = desktopLeft + desktopWidth / 2;
        var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null,
            maxRegionWidth: 640, maxRegionHeight: 240)
        { CaptureFullSurface = true };
        var compute = typeof(LiquidGlassController).GetMethod("ComputeFrameDimensions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object Dimensions(LiquidGlassController.CaptureRegion region) => compute.Invoke(controller,
            new object[] { region, LiquidGlassController.GlassParams.Default, true, true, region.Width, region.Height })!;
        int Value(object dimensions, string name) => (int)dimensions.GetType().GetProperty(name)!.GetValue(dimensions)!;
        var collapsed = Dimensions(new LiquidGlassController.CaptureRegion(center - 50, desktopTop + 100, 100, 50));
        var expanded = Dimensions(new LiquidGlassController.CaptureRegion(center - 300, desktopTop + 100, 600, 200));
        Assert.Equal(Value(collapsed, "SrcX"), Value(expanded, "SrcX"));
        int left = Value(collapsed, "SrcX");
        Assert.True(left <= center - 300);
        Assert.True(left + Value(collapsed, "SrcW") >= center + 300);
    });

    [Theory]
    [InlineData(0, 0, 0, 100)]
    [InlineData(80, 20, 0, 100)]
    [InlineData(-10, 20, 0, 20)]
    [InlineData(100, 120, 0, 100)]
    [InlineData(20, 120, 20, 100)]
    public void DirtyRowsAlwaysProduceValidSurfaceRect(int top, int bottom, int expectedTop, int expectedBottom)
    {
        var rows = D3DImageFramePresenter.NormalizeDirtyRows(new GlassDirtyRows(top, bottom), 100, false);
        Assert.Equal(new GlassDirtyRows(expectedTop, expectedBottom), rows);
        Assert.False(rows.IsEmpty);
        Assert.Equal(new GlassDirtyRows(0, 100), D3DImageFramePresenter.NormalizeDirtyRows(rows, 100, true));
    }

    [Fact]
    public void VisibleCapturePolicySurvivesWorkerFramesAndLiveToggle() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowInTaskbar = false, ShowActivated = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
        try
        {
            controller.Start();
            Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
            Assert.Equal(0u, affinity);
            controller.HideFromScreenCapture = true;
            controller.HideFromScreenCapture = false;
            // Exercise the worker's affinity restore branch without desktop capture.
            typeof(LiquidGlassController).GetMethod("HandleCaptureOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, new object[] { 1000.0 / 60 });
            Assert.True(GetWindowDisplayAffinity(hwnd, out affinity));
            Assert.Equal(0u, affinity);
        }
        finally { controller.Stop(); window.Close(); }
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
