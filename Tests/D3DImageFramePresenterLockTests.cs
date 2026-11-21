using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class D3DImageFramePresenterLockTests
{
    [Fact]
    public void GpuLensMetadataFollowsPresentationAndClearsOnRestart() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var target = new CompositorTarget();
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8, target);
        var pixels = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
        var source = Marshal.AllocHGlobal(pixels.Length);
        Exception? failure = null;
        presenter.Failed += ex => failure = ex;
        D3DImageFramePresenter.GpuFrame Submit(int generation, int lensWidth)
        {
            // Upload controlled pixels; this test never captures the user's desktop.
            var frame = new D3DImageFramePresenter.GpuFrame { Width = 8, Height = 8, Refract = 2 };
            frame.Set(0, 8); frame.Set(1, 8);
            frame.Set(2, lensWidth); frame.Set(3, 4);
            Assert.True(presenter.CaptureGpuFrame(in frame, default, generation, out bool submitted,
                source, 32, pixels.Length));
            Assert.True(submitted);
            Assert.Null(failure);
            return frame;
        }
        async Task PresentAsync()
        {
            await WpfFrameWaiter.UntilAsync(() =>
            {
                presenter.PresentPendingFrame();
                Assert.Null(failure);
                return !presenter.HasPendingFrame;
            }, "the uploaded GPU lens", ct);
        }
        try
        {
            Marshal.Copy(pixels, 0, source, pixels.Length);
            presenter.BeginSession(1);
            Assert.Null(presenter.PresentedGpuFrame);
            var first = Submit(1, 4);
            await PresentAsync();
            Assert.True(first.FrameEquals(Assert.IsType<D3DImageFramePresenter.GpuFrame>(presenter.PresentedGpuFrame)));

            target.Writable = false;
            var second = Submit(1, 8);
            presenter.PresentPendingFrame();
            Assert.True(presenter.HasPendingFrame);
            Assert.True(first.FrameEquals(Assert.IsType<D3DImageFramePresenter.GpuFrame>(presenter.PresentedGpuFrame)));
            target.Writable = true;
            await PresentAsync();
            Assert.True(second.FrameEquals(Assert.IsType<D3DImageFramePresenter.GpuFrame>(presenter.PresentedGpuFrame)));

            presenter.SuspendPresentation();
            Assert.Null(presenter.PresentedGpuFrame);
            presenter.BeginSession(2);
            var reopened = Submit(2, 2);
            Assert.Null(presenter.PresentedGpuFrame);
            await PresentAsync();
            Assert.True(reopened.FrameEquals(Assert.IsType<D3DImageFramePresenter.GpuFrame>(presenter.PresentedGpuFrame)));
        }
        finally { Marshal.FreeHGlobal(source); window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyCompositorDoesNotLeakLocksAndWebcamPresentationResumes(bool busyAttach) => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var target = new CompositorTarget();
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8, target, webcam: true);
        var pixels = new byte[8 * 8 * 4];
        Exception? failure = null;
        int presented = 0;
        presenter.Failed += ex => failure = ex;
        presenter.FramePresented += _ => presented++;
        void Upload(int version)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = (byte)version;
                pixels[i + 3] = 255;
            }
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, 8, 8, BitmapAlphaMode.Ignore);
            Assert.True(presenter.UploadWebcam(bitmap, 1));
        }
        try
        {
            presenter.PrepareResources();
            presenter.BeginSession(1);
            Upload(10);
            presenter.PresentPendingFrame();
            Assert.Equal(1, presented);

            for (int cycle = 0; cycle < 120; cycle++)
            {
                target.Writable = false;
                int before = presented;
                int writesBefore = target.Writes;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Upload(20 + cycle + attempt);
                    if (busyAttach) presenter.AttachBackBuffer();
                    else presenter.PresentPendingFrame();
                    Assert.Equal(0, target.LockCount);
                    Assert.True(presenter.HasPendingFrame);
                    Assert.Equal(before, presented);
                    Assert.Equal(writesBefore, target.Writes);
                    Assert.Null(failure);
                }
                target.Writable = true;
                presenter.PresentPendingFrame();
                Assert.Equal(before + 1, presented);
                Assert.False(presenter.HasPendingFrame);
                Assert.Equal(0, target.LockCount);
                Assert.Null(failure);
            }
        }
        finally
        {
            target.Writable = true;
            window.Close();
        }
    });

    private sealed class CompositorTarget : ID3DFrameTarget
    {
        public ImageSource ImageSource { get; } = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
        public bool IsFrontBufferAvailable => true;
        public event DependencyPropertyChangedEventHandler IsFrontBufferAvailableChanged { add { /* no-op */ } remove { /* no-op */ } }
        public bool Writable { get; set; } = true;
        public int LockCount { get; private set; }
        public int Writes { get; private set; }
        public bool TryLock(Duration timeout) { LockCount++; return Writable; }
        public void Lock() => LockCount++;
        public void Unlock() { Assert.True(LockCount > 0); LockCount--; }
        public void SetBackBuffer(D3DResourceType type, IntPtr surface, bool enableSoftwareFallback = false) => Write();
        public void AddDirtyRect(Int32Rect rectangle)
        {
            Assert.Equal(new Int32Rect(0, 0, 8, 8), rectangle);
            Write();
        }
        private void Write()
        {
            Assert.True(Writable);
            Assert.True(LockCount > 0);
            Writes++;
        }
    }
}
