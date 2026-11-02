using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassGpuSnapshotTests
{
    [Fact]
    public void FractionalBlurKeepsNativeFrameLayout()
    {
        Assert.Equal(536, System.Runtime.InteropServices.Marshal.SizeOf<D3DImageFramePresenter.GpuFrame>());
        Assert.Equal(16, (int)System.Runtime.InteropServices.Marshal.OffsetOf<D3DImageFramePresenter.GpuFrame>("Blur"));
        var frame = new D3DImageFramePresenter.GpuFrame { Blur = .07f };
        Assert.Equal(.07f, frame.Blur);
    }

    [Fact]
    public void LensUpdatesPublishCompleteGeometryAfterTheUiPass() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
        var effect = new LiquidGlassRefractionEffect { NotchW = 200, NotchH = 40 };
        controller.UpdateGpuLens(effect, 15, 20);

        effect.NotchW = 600;
        effect.NotchH = 160;
        // A capture worker can run between these property changes. It must see
        // the previous complete lens until the UI publishes the new one.
        Assert.Equal((200f, 40f), ReadSize(controller));
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        Assert.Equal((600f, 160f), ReadSize(controller));

        effect.NotchW = 800;
        controller.SetGpuMode(false, null);
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        Assert.False((bool)typeof(LiquidGlassController).GetField("_hasGpuLens",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!);
    });

    private static unsafe (float, float) ReadSize(LiquidGlassController controller)
    {
        var frame = (D3DImageFramePresenter.GpuFrame)typeof(LiquidGlassController).GetField("_gpuLens",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        return (frame.Lens[2 * 4], frame.Lens[3 * 4]);
    }
}
