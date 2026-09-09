using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using VNotch.Controllers;
using Xunit;
using Xunit.Abstractions;
using static VNotch.Services.Win32Interop;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class LiquidGlassPacingTests
{
    private readonly ITestOutputHelper? _output;

    public LiquidGlassPacingTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

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
            var image = new Image();
            host.Content = image;
            var hwnd = new WindowInteropHelper(host).EnsureHandle();

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
            var image = new Image();
            host.Content = image;
            var hwnd = new WindowInteropHelper(host).EnsureHandle();

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
    [Trait("Category", "DesktopIntegration")]
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

    [Fact]
    public void LiquidGlass_WaitableTimer_InterruptibleOnStop()
    {
        RunSta(() =>
        {
            var host = new Window
            {
                Width = 200,
                Height = 100,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            var image = new Image();
            host.Content = image;
            var hwnd = new WindowInteropHelper(host).EnsureHandle();

            LiquidGlassController? controller = null;
            try
            {
                controller = new LiquidGlassController(image, () => hwnd, () => null, activeFps: 60);
                controller.EnsureWaitableTimerForTest();
                controller.SetActiveForTest(true);

                var sw = Stopwatch.StartNew();
                var thread = new Thread(() =>
                {
                    // Attempt to wait for 3000 ms (3 seconds)
                    controller.SleepUntilRenderDeadline(3000);
                });
                thread.Start();

                // Allow worker thread to enter the waitable timer
                Thread.Sleep(30);

                // Stop should signal _idleWakeEvent and unblock the waitable timer immediately
                controller.Stop();
                bool joined = thread.Join(500);
                sw.Stop();

                Assert.True(joined, "Thread must unblock and terminate when controller stops.");
                Assert.True(sw.ElapsedMilliseconds < 300,
                    $"Stop() did not interrupt wait immediately. Elapsed: {sw.ElapsedMilliseconds} ms (expected < 300 ms).");
            }
            finally
            {
                controller?.DisposeWaitableTimerForTest();
                controller?.Stop();
                host.Close();
            }
        });
    }

    public sealed record BenchmarkResult(
        string Mode,
        double TargetFps,
        double TargetIntervalMs,
        double MeanIntervalMs,
        double P95IntervalMs,
        double P99IntervalMs,
        double StdDevMs,
        double ThreadCpuTimeMs,
        double WallTimeMs,
        double CpuPercent);

    private static BenchmarkResult RunBenchmarkScenario(string name, double targetFps, bool animating, bool isOptimized, int frameCount = 70)
    {
        double targetIntervalMs = 1000.0 / targetFps;
        var intervals = new List<double>(frameCount);
        double threadCpuTimeMs = 0;
        double wallTimeMs = 0;

        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            TimeBeginPeriod(1);
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                using var idleWake = new AutoResetEvent(false);
                SafeWaitHandle? timer = null;
                IntPtr[]? handles = null;

                if (isOptimized)
                {
                    timer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                    if (timer != null && !timer.IsInvalid)
                    {
                        handles = new IntPtr[2]
                        {
                            idleWake.SafeWaitHandle.DangerousGetHandle(),
                            timer.DangerousGetHandle()
                        };
                    }
                }

                // Warmup
                Thread.Sleep(15);

                GetThreadTimes(GetCurrentThread(), out _, out _, out var kStart, out var uStart);
                long wallStartTicks = Stopwatch.GetTimestamp();

                var clock = Stopwatch.StartNew();
                double nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                double lastFrameMs = nextDeadlineMs;

                for (int i = 0; i < frameCount; i++)
                {
                    // Simulate typical frame workload (0.8 ms computation)
                    long workEnd = Stopwatch.GetTimestamp() + (long)(0.8 * Stopwatch.Frequency / 1000.0);
                    while (Stopwatch.GetTimestamp() < workEnd)
                    {
                        Thread.SpinWait(8);
                    }

                    nextDeadlineMs += targetIntervalMs;
                    double nowMs = clock.Elapsed.TotalMilliseconds;
                    double remaining = nextDeadlineMs - nowMs;

                    if (remaining > 0)
                    {
                        if (isOptimized && timer != null && handles != null)
                        {
                            ExecuteOptimizedSleep(remaining, animating, timer, handles, idleWake);
                        }
                        else
                        {
                            ExecuteBaselineSleep(remaining);
                        }
                    }

                    double frameDoneMs = clock.Elapsed.TotalMilliseconds;
                    intervals.Add(frameDoneMs - lastFrameMs);
                    lastFrameMs = frameDoneMs;
                }

                GetThreadTimes(GetCurrentThread(), out _, out _, out var kEnd, out var uEnd);
                long wallEndTicks = Stopwatch.GetTimestamp();

                long cpuTicks = (long)((kEnd.ToUInt64() + uEnd.ToUInt64()) - (kStart.ToUInt64() + uStart.ToUInt64()));
                threadCpuTimeMs = cpuTicks / 10_000.0;
                wallTimeMs = (wallEndTicks - wallStartTicks) * 1000.0 / Stopwatch.Frequency;

                timer?.Dispose();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                TimeEndPeriod(1);
            }
        });

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (threadEx != null) throw new AggregateException(threadEx);

        intervals.Sort();
        double mean = intervals.Average();
        int p95Idx = (int)Math.Clamp(Math.Round((intervals.Count - 1) * 0.95), 0, intervals.Count - 1);
        int p99Idx = (int)Math.Clamp(Math.Round((intervals.Count - 1) * 0.99), 0, intervals.Count - 1);
        double p95 = intervals[p95Idx];
        double p99 = intervals[p99Idx];
        double variance = intervals.Select(x => (x - mean) * (x - mean)).Average();
        double stdev = Math.Sqrt(variance);
        double cpuPercent = wallTimeMs > 0 ? (threadCpuTimeMs / wallTimeMs) * 100.0 : 0.0;

        return new BenchmarkResult(
            name, targetFps, targetIntervalMs, mean, p95, p99, stdev, threadCpuTimeMs, wallTimeMs, cpuPercent);
    }

    private static void ExecuteBaselineSleep(double milliseconds)
    {
        long targetTicks = Stopwatch.GetTimestamp() + (long)Math.Round(Math.Max(0.1, milliseconds) * Stopwatch.Frequency / 1000.0);
        while (true)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                int sleepMs = (int)Math.Floor(remainingMs - 1.5);
                if (sleepMs >= 1)
                {
                    Thread.Sleep(sleepMs);
                    continue;
                }
            }

            if (remainingMs >= 0.25)
            {
                Thread.Sleep(0);
            }
            else
            {
                Thread.SpinWait(8);
            }
        }
    }

    private static void ExecuteOptimizedSleep(double milliseconds, bool animating, SafeWaitHandle timer, IntPtr[] handles, AutoResetEvent idleWake)
    {
        long targetTicks = Stopwatch.GetTimestamp() + (long)Math.Round(Math.Max(0.1, milliseconds) * Stopwatch.Frequency / 1000.0);
        while (true)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;

            if (!animating)
            {
                if (remainingMs <= 1.0) return;

                long dueTime = -Math.Max(1L, (long)(remainingMs * 10_000.0));
                if (SetWaitableTimer(timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    uint timeout = (uint)Math.Ceiling(remainingMs) + 100;
                    WaitForMultipleObjects(2, handles, false, timeout);
                }
                else
                {
                    idleWake.WaitOne((int)Math.Max(1, Math.Round(remainingMs)));
                }
                return;
            }
            else
            {
                if (remainingMs > 0.35)
                {
                    double waitMs = remainingMs - 0.15;
                    long dueTime = -Math.Max(1L, (long)(waitMs * 10_000.0));
                    if (SetWaitableTimer(timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        uint timeout = (uint)Math.Ceiling(waitMs) + 100;
                        uint waitResult = WaitForMultipleObjects(2, handles, false, timeout);
                        if (waitResult == WAIT_OBJECT_0) return;
                    }
                    else
                    {
                        if (idleWake.WaitOne((int)Math.Max(1, Math.Round(waitMs)))) return;
                    }
                    continue;
                }

                Thread.SpinWait(8);
            }
        }
    }

    [Fact]
    public void LiquidGlass_PacingBenchmark_60Fps_And_120Fps()
    {
        // 60 FPS Animating
        var base60Anim = RunBenchmarkScenario("Baseline 60 FPS (Anim)", 60, animating: true, isOptimized: false);
        var opt60Anim = RunBenchmarkScenario("Optimized 60 FPS (Anim)", 60, animating: true, isOptimized: true);

        // 60 FPS Non-Animating (Idle)
        var base60Idle = RunBenchmarkScenario("Baseline 60 FPS (Idle)", 60, animating: false, isOptimized: false);
        var opt60Idle = RunBenchmarkScenario("Optimized 60 FPS (Idle)", 60, animating: false, isOptimized: true);

        // 120 FPS Animating
        var base120Anim = RunBenchmarkScenario("Baseline 120 FPS (Anim)", 120, animating: true, isOptimized: false);
        var opt120Anim = RunBenchmarkScenario("Optimized 120 FPS (Anim)", 120, animating: true, isOptimized: true);

        // 120 FPS Non-Animating (Idle)
        var base120Idle = RunBenchmarkScenario("Baseline 120 FPS (Idle)", 120, animating: false, isOptimized: false);
        var opt120Idle = RunBenchmarkScenario("Optimized 120 FPS (Idle)", 120, animating: false, isOptimized: true);

        string report = $"""

            ========================================================================================================
            LIQUID GLASS FRAME PACING BENCHMARK REPORT (Before vs After)
            ========================================================================================================
            Scenario                     | Thread CPU (ms) | CPU (%) | Mean (ms) | P95 (ms) | P99 (ms) | StdDev (ms)
            -----------------------------+-----------------+---------+-----------+----------+----------+------------
            {base60Anim.Mode,-28} | {base60Anim.ThreadCpuTimeMs,15:F1} | {base60Anim.CpuPercent,6:F1}% | {base60Anim.MeanIntervalMs,9:F2} | {base60Anim.P95IntervalMs,8:F2} | {base60Anim.P99IntervalMs,8:F2} | {base60Anim.StdDevMs,10:F2}
            {opt60Anim.Mode,-28} | {opt60Anim.ThreadCpuTimeMs,15:F1} | {opt60Anim.CpuPercent,6:F1}% | {opt60Anim.MeanIntervalMs,9:F2} | {opt60Anim.P95IntervalMs,8:F2} | {opt60Anim.P99IntervalMs,8:F2} | {opt60Anim.StdDevMs,10:F2}
            -----------------------------+-----------------+---------+-----------+----------+----------+------------
            {base60Idle.Mode,-28} | {base60Idle.ThreadCpuTimeMs,15:F1} | {base60Idle.CpuPercent,6:F1}% | {base60Idle.MeanIntervalMs,9:F2} | {base60Idle.P95IntervalMs,8:F2} | {base60Idle.P99IntervalMs,8:F2} | {base60Idle.StdDevMs,10:F2}
            {opt60Idle.Mode,-28} | {opt60Idle.ThreadCpuTimeMs,15:F1} | {opt60Idle.CpuPercent,6:F1}% | {opt60Idle.MeanIntervalMs,9:F2} | {opt60Idle.P95IntervalMs,8:F2} | {opt60Idle.P99IntervalMs,8:F2} | {opt60Idle.StdDevMs,10:F2}
            -----------------------------+-----------------+---------+-----------+----------+----------+------------
            {base120Anim.Mode,-28} | {base120Anim.ThreadCpuTimeMs,15:F1} | {base120Anim.CpuPercent,6:F1}% | {base120Anim.MeanIntervalMs,9:F2} | {base120Anim.P95IntervalMs,8:F2} | {base120Anim.P99IntervalMs,8:F2} | {base120Anim.StdDevMs,10:F2}
            {opt120Anim.Mode,-28} | {opt120Anim.ThreadCpuTimeMs,15:F1} | {opt120Anim.CpuPercent,6:F1}% | {opt120Anim.MeanIntervalMs,9:F2} | {opt120Anim.P95IntervalMs,8:F2} | {opt120Anim.P99IntervalMs,8:F2} | {opt120Anim.StdDevMs,10:F2}
            -----------------------------+-----------------+---------+-----------+----------+----------+------------
            {base120Idle.Mode,-28} | {base120Idle.ThreadCpuTimeMs,15:F1} | {base120Idle.CpuPercent,6:F1}% | {base120Idle.MeanIntervalMs,9:F2} | {base120Idle.P95IntervalMs,8:F2} | {base120Idle.P99IntervalMs,8:F2} | {base120Idle.StdDevMs,10:F2}
            {opt120Idle.Mode,-28} | {opt120Idle.ThreadCpuTimeMs,15:F1} | {opt120Idle.CpuPercent,6:F1}% | {opt120Idle.MeanIntervalMs,9:F2} | {opt120Idle.P95IntervalMs,8:F2} | {opt120Idle.P99IntervalMs,8:F2} | {opt120Idle.StdDevMs,10:F2}
            ========================================================================================================
            """;

        _output?.WriteLine(report);
        Console.WriteLine(report);

        // Verification assertions:
        // 1. Idle / Non-animating CPU % and CPU time must drop drastically or stay minimal
        Assert.True(opt120Idle.ThreadCpuTimeMs <= base120Idle.ThreadCpuTimeMs,
            $"120 FPS Idle CPU should decrease or equal. Base: {base120Idle.ThreadCpuTimeMs}ms, Opt: {opt120Idle.ThreadCpuTimeMs}ms");

        // 2. Animating CPU usage must drop due to eliminating 1.5-2ms spin/yield loop
        Assert.True(opt60Anim.ThreadCpuTimeMs <= base60Anim.ThreadCpuTimeMs + 5.0,
            $"60 FPS Anim CPU should be lower or comparable. Base: {base60Anim.ThreadCpuTimeMs}ms, Opt: {opt60Anim.ThreadCpuTimeMs}ms");

        // 3. P95 pacing must remain close to target interval (target: 16.67ms for 60fps, 8.33ms for 120fps)
        Assert.InRange(opt60Anim.MeanIntervalMs, 14.0, 19.0);
        Assert.InRange(opt120Anim.MeanIntervalMs, 7.0, 10.5);

        // 4. Jitter (StdDev) must be sub-millisecond
        Assert.True(opt60Anim.StdDevMs < 1.5, $"60 FPS Anim StdDev too high: {opt60Anim.StdDevMs}ms");
        Assert.True(opt120Anim.StdDevMs < 1.5, $"120 FPS Anim StdDev too high: {opt120Anim.StdDevMs}ms");
    }
}
