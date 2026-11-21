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
            var dims = compute.Invoke(controller, [region, parameters, true, region.Width, region.Height])!;
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
    public void MissingFirstCaptureDoesNotPresentAPlaceholder() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null)
        {
            CompositeBackdropCapture = static (_, _, _, _, _) => false
        };
        var bits = Marshal.AllocHGlobal(4 * 8 * 6);
        var dib = typeof(LiquidGlassController).GetField("_dibBits", Private)!;
        try
        {
            dib.SetValue(controller, bits);
            var captureType = typeof(LiquidGlassController).GetNestedType("BackdropCaptureParams", BindingFlags.NonPublic)!;
            var capture = Activator.CreateInstance(captureType, [100, 10, 8, 6, 8, 6, 10, 40, false, IntPtr.Zero]);
            Assert.False((bool)typeof(LiquidGlassController).GetMethod("CaptureBackdrop", Private)!.Invoke(controller, [capture])!);
            Assert.False(controller.HasPresentedFrame);
        }
        finally
        {
            dib.SetValue(controller, IntPtr.Zero);
            Marshal.FreeHGlobal(bits);
        }
    });

    [Fact]
    public void MissingMagnifierUsesTheExactCompositeBackdropBeforeTheGreyMaterial() => SharedStaTestRunner.Run(() =>
    {
        bool composed = false;
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null)
        {
            CompositeBackdropCapture = (x, y, width, height, destination) =>
            {
                Assert.Equal((100, 10, 8, 6), (x, y, width, height));
                for (int i = 0; i < width * height; i++)
                    Marshal.WriteInt32(destination, i * 4, unchecked((int)0xFF446688));
                composed = true;
                return true;
            }
        };
        var bits = Marshal.AllocHGlobal(4 * 8 * 6);
        var dib = typeof(LiquidGlassController).GetField("_dibBits", Private)!;
        try
        {
            dib.SetValue(controller, bits);
            var captureType = typeof(LiquidGlassController).GetNestedType("BackdropCaptureParams", BindingFlags.NonPublic)!;
            var capture = Activator.CreateInstance(captureType, [100, 10, 8, 6, 8, 6, 10, 40, false, IntPtr.Zero]);
            object?[] arguments = [capture];
            Assert.True((bool)typeof(LiquidGlassController).GetMethod("CaptureBackdrop", Private)!.Invoke(controller, arguments)!);
            Assert.True(composed);
            Assert.Equal(unchecked((int)0xFF446688), Marshal.ReadInt32(bits));
        }
        finally
        {
            dib.SetValue(controller, IntPtr.Zero);
            Marshal.FreeHGlobal(bits);
        }
    });

    [Theory]
    [InlineData(0xFF000000u)]
    [InlineData(0xFF808080u)]
    [InlineData(0xFF336699u)]
    public void ATransientCaptureMissDoesNotReplaceThePresentedFrameAndRecoveryAcceptsRealDarkContent(uint recoveredPixel) => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        var bits = Marshal.AllocHGlobal(4 * 8 * 6);
        var dib = typeof(LiquidGlassController).GetField("_dibBits", Private)!;
        try
        {
            dib.SetValue(controller, bits);
            var captureType = typeof(LiquidGlassController).GetNestedType("BackdropCaptureParams", BindingFlags.NonPublic)!;
            var capture = Activator.CreateInstance(captureType, [100, 10, 8, 6, 8, 6, 10, 40, false, IntPtr.Zero]);
            var method = typeof(LiquidGlassController).GetMethod("CaptureBackdrop", Private)!;
            object?[] arguments = [capture];
            controller.CompositeBackdropCapture = (_, _, width, height, destination) =>
            {
                for (int i = 0; i < width * height; i++) Marshal.WriteInt32(destination, i * 4, unchecked((int)0xFF446688));
                return true;
            };
            Assert.True((bool)method.Invoke(controller, arguments)!);
            typeof(LiquidGlassController).GetField("_hasVisibleFrame", Private)!.SetValue(controller, true);

            // A failed provider is allowed to have touched its staging buffer.
            controller.CompositeBackdropCapture = (_, _, width, height, destination) =>
            {
                for (int i = 0; i < width * height; i++) Marshal.WriteInt32(destination, i * 4, 0);
                return false;
            };
            for (int miss = 0; miss < 3; miss++)
            {
                Assert.False((bool)method.Invoke(controller, arguments)!);
                Assert.True(controller.HasPresentedFrame);
            }
            controller.CompositeBackdropCapture = (_, _, width, height, destination) =>
            {
                for (int i = 0; i < width * height; i++) Marshal.WriteInt32(destination, i * 4, unchecked((int)recoveredPixel));
                return true;
            };
            Assert.True((bool)method.Invoke(controller, arguments)!);
            Assert.Equal(unchecked((int)recoveredPixel), Marshal.ReadInt32(bits));
        }
        finally
        {
            dib.SetValue(controller, IntPtr.Zero);
            Marshal.FreeHGlobal(bits);
        }
    });
}
