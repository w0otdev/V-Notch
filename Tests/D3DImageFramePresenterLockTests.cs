using System.Runtime.InteropServices;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyCompositorDoesNotLeakLocksAndNewestFrameResumes(bool busyAttach) => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var target = new CompositorTarget();
        using var presenter = new D3DImageFramePresenter(window.Dispatcher, hwnd, 8, 8, target);
        var pixels = new byte[8 * 8 * 4];
        var source = Marshal.AllocHGlobal(pixels.Length);
        Exception? failure = null;
        int presented = 0;
        LiquidGlassController.GpuGeometry lastPresented = default;
        presenter.Failed += ex => failure = ex;
        presenter.FramePresented += tag => { presented++; lastPresented = tag; };
        LiquidGlassController.GpuGeometry Upload(int version)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = (byte)version;
                pixels[i + 3] = 255;
            }
            Marshal.Copy(pixels, 0, source, pixels.Length);
            var tag = new LiquidGlassController.GpuGeometry { CaptureOriginX = version };
            Assert.True(presenter.UploadFrame(source, 8, 8, 32, 1, out bool uploaded, tag));
            Assert.True(uploaded);
            return tag;
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
                LiquidGlassController.GpuGeometry newest = default;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    newest = Upload(20 + cycle + attempt);
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
                Assert.Equal(newest, lastPresented);
                Assert.False(presenter.HasPendingFrame);
                Assert.Equal(0, target.LockCount);
                Assert.Null(failure);
            }
        }
        finally
        {
            target.Writable = true;
            Marshal.FreeHGlobal(source);
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
        public void AddDirtyRect(Int32Rect rectangle) => Write();
        private void Write()
        {
            Assert.True(Writable);
            Assert.True(LockCount > 0);
            Writes++;
        }
    }
}
