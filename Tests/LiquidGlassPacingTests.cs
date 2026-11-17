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
                if (!DesktopTestMode.Enabled) VNotch.TestSupport.BackgroundTestDesktop.AttachCurrentThread();
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

                var property = typeof(LiquidGlassController).GetProperty("ForceRefreshNeeded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(property);
                bool forceRefresh = (bool)property.GetValue(controller)!;
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

    [DesktopFact]
    [Trait("Category", "DesktopIntegration")]
    public void LiquidGlass_FullSurface_UnchangedFrame_SkipsRedundantGpuUpload()
    {
        RunSta(() =>
        {
            var backdrop = new Window
            {
                // Cover the bounded capture envelope around this test lens.
                Left = 40,
                Top = 70,
                Width = 1000,
                Height = 700,
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

                // DWM animates a newly shown backdrop. Do not count those real
                // content changes as redundant uploads of an unchanged frame.
                PumpFor(TimeSpan.FromMilliseconds(1000));
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
                Task.Delay(30).Wait();

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
        double P95AbsoluteErrorMs,
        double ThreadCpuTimeMs,
        double WallTimeMs,
        double CpuPercent);

    private const double PacingJitterBudgetMs = 1.5;

    private static BenchmarkResult RunBenchmarkScenario(string name, double targetFps, bool animating, bool isOptimized, int frameCount = 180)
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
                VNotch.TestSupport.BackgroundTestDesktop.AttachCurrentThread();
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                // Exercise the production wait, including its timer fallback, without
                // starting capture or creating a visible window.
                using var controller = isOptimized
                    ? new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null, activeFps: (int)targetFps)
                    : null;
                if (controller != null)
                {
                    controller.EnsureWaitableTimerForTest();
                    controller.SetAnimating(animating);
                    controller.SetActiveForTest(true);
                }
                Action<double> sleep = controller == null ? ExecuteBaselineSleep : controller.SleepUntilRenderDeadline;

                // Warm the workload, JIT and native wait path before sampling.
                for (int i = 0; i < 30; i++)
                {
                    SimulateBenchmarkFrameWork();
                    sleep(targetIntervalMs - 0.8);
                }

                GetCurrentThreadTimes(out var kStart, out var uStart);
                long wallStartTicks = Stopwatch.GetTimestamp();

                var clock = Stopwatch.StartNew();
                double nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                double lastFrameMs = nextDeadlineMs;

                for (int i = 0; i < frameCount; i++)
                {
                    SimulateBenchmarkFrameWork();

                    nextDeadlineMs += targetIntervalMs;
                    double nowMs = clock.Elapsed.TotalMilliseconds;
                    double remaining = nextDeadlineMs - nowMs;

                    if (remaining > 0)
                    {
                        sleep(remaining);
                    }

                    double frameDoneMs = clock.Elapsed.TotalMilliseconds;
                    intervals.Add(frameDoneMs - lastFrameMs);
                    lastFrameMs = frameDoneMs;
                }

                GetCurrentThreadTimes(out var kEnd, out var uEnd);
                long wallEndTicks = Stopwatch.GetTimestamp();

                long cpuTicks = (long)((kEnd.ToUInt64() + uEnd.ToUInt64()) - (kStart.ToUInt64() + uStart.ToUInt64()));
                threadCpuTimeMs = cpuTicks / 10_000.0;
                wallTimeMs = (wallEndTicks - wallStartTicks) * 1000.0 / Stopwatch.Frequency;
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException($"Pacing benchmark scenario did not finish: {name}");
        if (threadEx != null) throw new AggregateException(threadEx);
        Assert.Equal(frameCount, intervals.Count);

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
            name, targetFps, targetIntervalMs, mean, p95, p99, stdev,
            ComputeP95AbsoluteIntervalError(intervals, targetIntervalMs), threadCpuTimeMs, wallTimeMs, cpuPercent);
    }

    private static void SimulateBenchmarkFrameWork()
    {
        // Simulate typical frame workload (0.8 ms computation).
        long workEnd = Stopwatch.GetTimestamp() + (long)(0.8 * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < workEnd)
            Thread.SpinWait(8);
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

    private static double ComputeP95AbsoluteIntervalError(IEnumerable<double> intervals, double targetIntervalMs)
    {
        double[] errors = intervals.Select(interval => Math.Abs(interval - targetIntervalMs)).OrderBy(error => error).ToArray();
        Assert.NotEmpty(errors);
        // Nearest-rank P95 checks both early and late frames against the target.
        return errors[(int)Math.Ceiling(errors.Length * 0.95) - 1];
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void PacingJitterBudgetToleratesAnIsolatedSchedulerStallAndCatchUp(int fps)
    {
        double target = 1000.0 / fps;
        double[] intervals = Enumerable.Repeat(target, 180).ToArray();
        intervals[40] += 20;
        double debt = 20;
        for (int i = 41; debt > 0; i++)
        {
            double catchUp = Math.Min(debt, target - 0.8);
            intervals[i] -= catchUp;
            debt -= catchUp;
        }

        double mean = intervals.Average();
        double stdDev = Math.Sqrt(intervals.Select(interval => Math.Pow(interval - mean, 2)).Average());
        Assert.True(stdDev > PacingJitterBudgetMs, "An isolated stall reproduces the old StdDev failure.");
        Assert.InRange(ComputeP95AbsoluteIntervalError(intervals, target), 0, PacingJitterBudgetMs);
    }

    [Theory]
    [InlineData(60, -2)]
    [InlineData(60, 2)]
    [InlineData(120, -2)]
    [InlineData(120, 2)]
    public void PacingJitterBudgetRejectsRecurringEarlyOrLateFrames(int fps, double errorMs)
    {
        double target = 1000.0 / fps;
        double[] intervals = Enumerable.Repeat(target, 180).ToArray();
        for (int i = 0; i < 18; i++) intervals[i] += errorMs;

        Assert.True(ComputeP95AbsoluteIntervalError(intervals, target) > PacingJitterBudgetMs);
    }

    [Fact]
    [Trait("Category", "Performance")]
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
            LIQUID GLASS FRAME PACING BENCHMARK REPORT (Baseline vs Production Wait)
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
            P95 absolute interval error: 60 FPS Anim = {opt60Anim.P95AbsoluteErrorMs:F2} ms; 120 FPS Anim = {opt120Anim.P95AbsoluteErrorMs:F2} ms
            """;

        _output?.WriteLine(report);
        Console.WriteLine(report);

        // Verification assertions:
        // 1. Idle CPU usage must remain low (under 25% across test scenarios)
        Assert.True(opt60Idle.CpuPercent < 25.0, $"60 FPS Idle CPU too high: {opt60Idle.CpuPercent}%");
        Assert.True(opt120Idle.CpuPercent < 25.0, $"120 FPS Idle CPU too high: {opt120Idle.CpuPercent}%");

        // 2. Mean interval must track target interval closely (16.67ms for 60fps, 8.33ms for 120fps)
        Assert.InRange(opt60Anim.MeanIntervalMs, 14.0, 19.0);
        Assert.InRange(opt120Anim.MeanIntervalMs, 7.0, 10.5);

        // Shared runners can deschedule a thread for an isolated long interval,
        // followed by short catch-up frames. Keep raw StdDev/P99 in the report;
        // gate recurring early/late frames with the same 1.5 ms budget at P95.
        Assert.InRange(opt60Anim.P95AbsoluteErrorMs, 0, PacingJitterBudgetMs);
        Assert.InRange(opt120Anim.P95AbsoluteErrorMs, 0, PacingJitterBudgetMs);
    }
}
