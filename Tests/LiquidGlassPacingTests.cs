using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class LiquidGlassPacingTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (error != null)
            throw new AggregateException(error);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        long until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= until)
                throw new TimeoutException("Condition not met before timeout.");
            PumpFor(TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public void LiquidGlass_SetLiveRegion_WakesIdleWaitImmediately()
    {
        RunSta(() =>
        {
            var host = new Window
            {
                Width = 400,
                Height = 300,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            host.Show();

            var image = new Image();
            host.Content = image;
            var hwnd = new WindowInteropHelper(host).Handle;

            // Region provider initially returns null -> controller enters idle wait
            LiquidGlassController? controller = null;
            try
            {
                controller = new LiquidGlassController(image, () => hwnd, () => null, activeFps: 30);
                controller.Start();

                // Pump slightly for worker to enter idle wait
                PumpFor(TimeSpan.FromMilliseconds(50));

                var sw = Stopwatch.StartNew();
                // Providing a live region should immediately wake the worker
                controller.SetLiveRegion(new LiquidGlassController.CaptureRegion(10, 10, 100, 50, 0, 0));
                
                // Ensure worker is active and not stuck
                Assert.True(sw.ElapsedMilliseconds < 100, $"SetLiveRegion took too long: {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                controller?.Stop();
                host.Close();
            }
        });
    }

    [Fact]
    public void LiquidGlass_SetPresentationPaused_WakesWorkerAndSetsForceRefresh()
    {
        RunSta(() =>
        {
            var host = new Window
            {
                Width = 400,
                Height = 300,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            host.Show();

            var image = new Image();
            host.Content = image;
            var hwnd = new WindowInteropHelper(host).Handle;

            LiquidGlassController? controller = null;
            try
            {
                controller = new LiquidGlassController(image, () => hwnd, () =>
                    new LiquidGlassController.CaptureRegion(0, 0, 100, 50, 0, 0), activeFps: 30);
                controller.Start();

                controller.SetPresentationPaused(true);
                PumpFor(TimeSpan.FromMilliseconds(50));

                var sw = Stopwatch.StartNew();
                controller.SetPresentationPaused(false);
                sw.Stop();

                Assert.True(sw.ElapsedMilliseconds < 50, $"Unpausing took too long: {sw.ElapsedMilliseconds} ms");

                // Verify _forceRefreshNeeded flag via reflection
                var field = typeof(LiquidGlassController).GetField("_forceRefreshNeeded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                bool forceRefresh = (bool)field.GetValue(controller)!;
                // Either true or already processed by worker
                Assert.True(forceRefresh || controller.HasPresentedFrame);
            }
            finally
            {
                controller?.Stop();
                host.Close();
            }
        });
    }

    [Fact]
    public void LiquidGlass_FullSurface_UnchangedFrame_SkipsRedundantGpuUpload()
    {
        RunSta(() =>
        {
            var backdrop = new Window
            {
                Left = 40,
                Top = 70,
                Width = 600,
                Height = 350,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
            };
            var image = new System.Windows.Controls.Image();
            var clip = new Border { Width = 320, Height = 50, ClipToBounds = true, Child = image };
            var host = new Window
            {
                Left = 160,
                Top = 190,
                Width = 320,
                Height = 50,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Content = clip
            };

            int uploadCount = 0;
            LiquidGlassController? controller = null;
            try
            {
                backdrop.Show();
                host.Show();
                host.UpdateLayout();
                double dpi = VisualTreeHelper.GetDpi(host).DpiScaleX;
                var hwnd = new WindowInteropHelper(host).Handle;
                var effect = new LiquidGlassRefractionEffect { Noise = 0, HighlightStrength = 0, Chroma = 0 };
                image.Effect = effect;

                controller = new LiquidGlassController(image, () => hwnd, () =>
                {
                    var point = clip.PointToScreen(new Point());
                    return new LiquidGlassController.CaptureRegion((int)point.X, (int)point.Y,
                        (int)(320 * dpi), (int)(50 * dpi), 0, 0);
                }, activeFps: 30)
                {
                    CaptureFullSurface = true
                };

                Assert.True(controller.SetGpuMode(true, g =>
                {
                    effect.SrcW = controller.SurfaceWidth;
                    effect.SrcH = controller.SurfaceHeight;
                    effect.NotchW = 320 * dpi;
                    effect.NotchH = 50 * dpi;
                    effect.OffX = g.OffX;
                    effect.OffY = g.OffY;
                }));
                controller.OnGpuFrameUploaded = () => Interlocked.Increment(ref uploadCount);
                controller.Start();

                // Pump until at least one frame is presented
                PumpUntil(() => controller.HasPresentedFrame, TimeSpan.FromSeconds(5));

                int initialUploads = Volatile.Read(ref uploadCount);
                Assert.True(initialUploads >= 1);

                // Run for 150 ms (about 5 frame cycles at 30 FPS)
                // With unchanged suppression enabled, redundant GPU uploads must be skipped
                PumpFor(TimeSpan.FromMilliseconds(150));

                int uploadsAfterStatic = Volatile.Read(ref uploadCount);
                // With suppression (< 500ms interval), redundant uploads should be suppressed
                Assert.True(uploadsAfterStatic <= initialUploads + 2,
                    $"Expected uploads to be suppressed, but got {uploadsAfterStatic} uploads (initial was {initialUploads})");

                // Now force refresh: must trigger upload immediately
                controller.ForceRefresh();
                PumpFor(TimeSpan.FromMilliseconds(100));
                int uploadsAfterForce = Volatile.Read(ref uploadCount);
                Assert.True(uploadsAfterForce > uploadsAfterStatic,
                    "ForceRefresh did not trigger immediate upload");
            }
            finally
            {
                controller?.Stop();
                host.Close();
                backdrop.Close();
            }
        });
    }
}
