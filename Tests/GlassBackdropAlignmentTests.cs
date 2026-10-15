using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class GlassBackdropAlignmentTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureGeometryMapsTheSameScreenPointAcrossResizeAndDesktopEdges(bool fullSurface) => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        controller.CaptureFullSurface = fullSurface;
        var parameters = LiquidGlassController.GlassParams.Default;
        var compute = typeof(LiquidGlassController).GetMethod("ComputeFrameDimensions", Private)!;
        var geometry = typeof(LiquidGlassController).GetMethod("CreateGpuGeometry", Private)!;

        foreach (var region in new[]
        {
            new LiquidGlassController.CaptureRegion(900, 8, 300, 40, SubX: 0.25, SubY: -0.25),
            new LiquidGlassController.CaptureRegion(750, 8, 600, 184),
            new LiquidGlassController.CaptureRegion(820, 16, 460, 100),
            new LiquidGlassController.CaptureRegion(-1700, -100, 300, 80),
            new LiquidGlassController.CaptureRegion(0, 0, 300, 80)
        })
        {
            var dims = compute.Invoke(controller, [region, parameters, true, true, region.Width, region.Height])!;
            var g = (LiquidGlassController.GpuGeometry)geometry.Invoke(controller, [parameters, region, dims])!;
            // An arbitrary feature must sample its own desktop coordinate before
            // applying the optical displacement, including a retained capture origin.
            double localX = region.Width * 0.37;
            double localY = region.Height * 0.61;
            Assert.Equal(region.X + region.SubX + localX, g.CaptureOriginX + g.OffX + localX, 6);
            Assert.Equal(region.Y + region.SubY + localY, g.CaptureOriginY + g.OffY + localY, 6);
        }
    });

    [Fact]
    public void MissingExactCaptureUsesPlainMaterialInsteadOfPixelsFromAnotherLocation() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        var bits = Marshal.AllocHGlobal(4 * 8 * 6);
        var dib = typeof(LiquidGlassController).GetField("_dibBits", Private)!;
        try
        {
            dib.SetValue(controller, bits);
            typeof(LiquidGlassController).GetField("_averageBackgroundColorRgb", Private)!.SetValue(controller, 0x203040);
            var captureType = typeof(LiquidGlassController).GetNestedType("BackdropCaptureParams", BindingFlags.NonPublic)!;
            var capture = Activator.CreateInstance(captureType, [100, 10, 8, 6, 8, 6, 10, 40, false, IntPtr.Zero]);
            Assert.True((bool)typeof(LiquidGlassController).GetMethod("CaptureBackdrop", Private)!.Invoke(controller, [capture])!);
            for (int i = 0; i < 8 * 6; i++)
                Assert.Equal(unchecked((int)0xFF203040), Marshal.ReadInt32(bits, i * 4));
        }
        finally
        {
            dib.SetValue(controller, IntPtr.Zero);
            Marshal.FreeHGlobal(bits);
        }
    });
}
