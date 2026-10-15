using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class D3DImageFramePresenterLockTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("PresentPendingFrame")]
    [InlineData("AttachBackBuffer")]
    public void BusyCompositorDoesNotLeakLocksAndNewestPixelsResume(string busyOperation) => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8);
        var image = Assert.IsType<D3DImage>(presenter.ImageSource);
        // Control the real WPF compositor gate: a fake TryLock would miss its
        // counter semantics. No window needs to appear on the input desktop.
        var canWrite = (ManualResetEvent)typeof(D3DImage).GetField("_canWriteEvent", Private)!.GetValue(image)!;
        var lockCount = typeof(D3DImage).GetField("_lockCount", Private)!;
        var pixels = new byte[8 * 8 * 4];
        var source = Marshal.AllocHGlobal(pixels.Length);
        Exception? failure = null;
        int presented = 0;
        presenter.Failed += ex => failure = ex;
        presenter.FramePresented += _ => presented++;
        void Invoke(string method) => typeof(D3DImageFramePresenter).GetMethod(method, Private)!.Invoke(presenter, null);
        void Upload(byte red)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }
            Marshal.Copy(pixels, 0, source, pixels.Length);
            Assert.True(presenter.UploadFrame(source, 8, 8, 32, 1, out bool uploaded));
            Assert.True(uploaded);
        }
        try
        {
            presenter.PrepareResources();
            presenter.BeginSession(1);
            Upload(10);
            Invoke("PresentPendingFrame");
            Assert.Equal(1, presented);

            // Repeated busy/recovery cycles must retain the newest pending frame
            // without transferring pixels while WPF owns the back buffer.
            for (int cycle = 0; cycle < 120; cycle++)
            {
                canWrite.Reset();
                int before = presented;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Upload((byte)(20 + cycle + attempt));
                    Invoke(busyOperation);
                    Assert.Equal(0u, (uint)lockCount.GetValue(image)!);
                    Assert.True(presenter.HasPendingFrame);
                    Assert.Equal(before, presented);
                    Assert.Null(failure);
                }
                canWrite.Set();
                Invoke("PresentPendingFrame");
                Assert.Equal(before + 1, presented);
                Assert.False(presenter.HasPendingFrame);
                Assert.Equal(0u, (uint)lockCount.GetValue(image)!);
                Assert.Null(failure);
                var snapshot = (BitmapSource)typeof(D3DImage).GetMethod("CopyBackBuffer", Private)!.Invoke(image, null)!;
                var actual = new byte[pixels.Length];
                snapshot.CopyPixels(actual, 32, 0);
                Assert.Equal(pixels[2], actual[2]);
            }
        }
        finally
        {
            canWrite.Set();
            // Keep a failing regression from leaving WPF locked during cleanup.
            while ((uint)lockCount.GetValue(image)! > 0) image.Unlock();
            Marshal.FreeHGlobal(source);
            window.Close();
        }
    });
}
