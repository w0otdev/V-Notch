using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public sealed class MemoryOptimizerService : IDisposable
{
    private static readonly Lazy<MemoryOptimizerService> _lazy =
        new(() => new MemoryOptimizerService());

    public static MemoryOptimizerService Instance => _lazy.Value;

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private long _lastTrimTimestamp = 0;
    private readonly object _trimLock = new();
    private CancellationTokenSource? _scheduledTrimCts;
    private readonly object _scheduleLock = new();
    private Timer? _periodicTimer;

    private MemoryOptimizerService()
    {
    }

    /// <summary>
    /// Starts a low-overhead periodic background optimizer that compacts memory during idle periods.
    /// </summary>
    public void StartPeriodicOptimizer(int intervalSeconds = 60)
    {
        lock (_trimLock)
        {
            if (_periodicTimer != null) return;
            _periodicTimer = new Timer(_ =>
            {
                TrimWorkingSet(aggressive: false);
            }, null, TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// Schedules a debounced garbage collection and working set trim after a specified delay.
    /// Rapid subsequent calls reset the delay, ensuring compaction only runs once the UI is idle.
    /// </summary>
    public void ScheduleTrim(int delayMs = 1000, bool aggressive = false)
    {
        lock (_scheduleLock)
        {
            _scheduledTrimCts?.Cancel();
            _scheduledTrimCts?.Dispose();
            _scheduledTrimCts = new CancellationTokenSource();
            var token = _scheduledTrimCts.Token;

            Task.Delay(delayMs, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !token.IsCancellationRequested)
                {
                    TrimWorkingSet(aggressive);
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Schedules a dual-stage working set trim and garbage collection after application startup has settled.
    /// Stage 1 cleans up initial JIT/XAML initialization garbage, and Stage 2 settles after background warmups.
    /// </summary>
    public void SchedulePostStartupTrim(int firstDelayMs = 1800, int secondDelayMs = 4500)
    {
        ScheduleTrim(firstDelayMs, aggressive: true);
        Task.Delay(secondDelayMs).ContinueWith(_ =>
        {
            TrimWorkingSet(aggressive: true);
        }, TaskScheduler.Default);
        StartPeriodicOptimizer(60);
    }

    /// <summary>
    /// Performs an efficient garbage collection and working set trim to release unneeded committed pages back to Windows.
    /// </summary>
    public void TrimWorkingSet(bool aggressive = false)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(now - _lastTrimTimestamp) / Stopwatch.Frequency;

        // Rate limit non-aggressive trims to at most once every 5 seconds
        if (!aggressive && elapsedSec < 5.0)
            return;

        lock (_trimLock)
        {
            if (!aggressive && (double)(Stopwatch.GetTimestamp() - _lastTrimTimestamp) / Stopwatch.Frequency < 5.0)
                return;

            _lastTrimTimestamp = Stopwatch.GetTimestamp();

            try
            {
                // 1. Collect gen 0, 1, and 2 garbage with compaction and run pending finalizers
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                // 2. Instruct Windows Memory Manager to trim unreferenced working set pages
                Win32Interop.SetProcessWorkingSetSize(_currentProcessHandle, new IntPtr(-1), new IntPtr(-1));
                Win32Interop.EmptyWorkingSet(_currentProcessHandle);
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("MEMORY", $"Memory trim skipped: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_scheduleLock)
        {
            _scheduledTrimCts?.Cancel();
            _scheduledTrimCts?.Dispose();
            _scheduledTrimCts = null;
        }

        lock (_trimLock)
        {
            _periodicTimer?.Dispose();
            _periodicTimer = null;
        }
    }
}
