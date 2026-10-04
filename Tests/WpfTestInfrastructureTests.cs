using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class WpfTestInfrastructureTests
{
    [Fact]
    public void AsyncStaRunnerAllowsDispatcherContinuations() => SharedStaTestRunner.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        await dispatcher.InvokeAsync(() => Assert.True(dispatcher.CheckAccess()), DispatcherPriority.Background);
        Assert.True(dispatcher.CheckAccess());
    });

    [Fact]
    public void FrameWaiterPropagatesAssertionFailures() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool first = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WpfFrameWaiter.UntilAsync(() =>
        {
            if (first) { first = false; return false; }
            throw new InvalidOperationException("Frame assertion failed");
        }, "a failing assertion"));
    });

    [Fact]
    public void FrameWaiterCancelsWithoutBlockingTheDispatcher() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = WpfFrameWaiter.UntilAsync(() => false, "a cancelled condition", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { });
    });

    [Fact]
    public void PublicSnapshotPreservesBitmapPixels() => SharedStaTestRunner.Run(() =>
    {
        byte[] pixels = [128, 192, 32, 255];
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var snapshot = ImageSourceSnapshot.Capture(source, 1, 1);
        var actual = new byte[4];
        snapshot.CopyPixels(actual, 4, 0);
        Assert.Equal(pixels, actual);
    });

    // A device-backed readback needs the same desktop/GPU opt-in as capture.
    // The HWND itself remains hidden; this test never shows a WPF Window.
    [DesktopFact]
    [Trait("Category", "DesktopIntegration")]
    public void PublicSnapshotReadsPresentedD3DImage() => SharedStaTestRunner.RunAsync(async cancellationToken =>
    {
        using var source = new HwndSource(new HwndSourceParameters("VNotchSnapshotTest")
        {
            Width = 1, Height = 1, WindowStyle = 0
        });
        using var presenter = new D3DImageFramePresenter(Dispatcher.CurrentDispatcher, source.Handle, 1, 1);
        presenter.BeginSession(1);
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.FramePresented += _ => presented.TrySetResult();
        IntPtr pixels = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(pixels, unchecked((int)0xff20c080));
            Assert.True(presenter.UploadFrame(pixels, 1, 1, 4, 1, out bool uploaded));
            Assert.True(uploaded);
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var d3d = Assert.IsType<D3DImage>(presenter.ImageSource);
            var snapshot = ImageSourceSnapshot.Capture(d3d, d3d.PixelWidth, d3d.PixelHeight);
            var actual = new byte[4];
            snapshot.CopyPixels(actual, 4, 0);
            Assert.Equal(new byte[] { 128, 192, 32, 255 }, actual);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixels); }
    });
}
