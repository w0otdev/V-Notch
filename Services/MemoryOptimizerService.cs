using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public sealed class MemoryOptimizerService : IDisposable
{
    private const string LogTag = "MEMORY";

    private static readonly Lazy<MemoryOptimizerService> _lazy =
        new(() => new MemoryOptimizerService());

    public static MemoryOptimizerService Instance => _lazy.Value;

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private long _lastTrimTimestamp;
    private readonly object _trimLock = new();
    private CancellationTokenSource? _scheduledTrimCts;
    private readonly object _scheduleLock = new();
    private Timer? _periodicTimer;

    private MemoryOptimizerService()
    {
    }

    /// <summary>
    /// Starts a periodic background optimizer.
    /// Note: Periodic forced GC and working set trimming are disabled to prevent frame drops and page thrashing.
    /// </summary>
    public void StartPeriodicOptimizer(int intervalSeconds = 60)
    {
        // Periodic forced GC/trimming disabled by design. The .NET runtime manages collection automatically.
    }

    /// <summary>
    /// Explicitly triggers manual memory compaction and working set trim.
    /// Rate-limited to prevent thrashing.
    /// </summary>
    public void ManualReclaimMemory()
    {
        TrimWorkingSet(aggressive: true);
    }

    /// <summary>
    /// Schedules an explicit garbage collection and working set trim after a specified delay.
    /// Rapid subsequent calls reset the delay. Routine non-aggressive calls are no-ops.
    /// </summary>
    public void ScheduleTrim(int delayMs = 1000, bool aggressive = false)
    {
        if (!aggressive)
        {
            // Routine / interactive callers: do not schedule forced GC or working-set trim.
            return;
        }

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
                    TrimWorkingSet(aggressive: true);
                }
            }, token, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Schedules a post-startup working set trim after application startup has settled.
    /// </summary>
    public void SchedulePostStartupTrim(int firstDelayMs = 1800, int secondDelayMs = 4500)
    {
        ScheduleTrim(firstDelayMs, aggressive: true);
        Task.Delay(secondDelayMs, CancellationToken.None).ContinueWith(_ =>
        {
            TrimWorkingSet(aggressive: true);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Performs an explicit garbage collection and working set trim to release unneeded committed pages back to Windows.
    /// This is only performed when explicitly requested (aggressive = true) to prevent UI micro-stutters and page thrashing.
    /// </summary>
    public void TrimWorkingSet(bool aggressive = false)
    {
        if (!aggressive)
        {
            // Routine / interactive callers: do not force GC or trim working set.
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(now - _lastTrimTimestamp) / Stopwatch.Frequency;

        // Rate limit manual trims to at most once every 5 seconds
        if (elapsedSec < 5.0)
            return;

        lock (_trimLock)
        {
            if ((double)(Stopwatch.GetTimestamp() - _lastTrimTimestamp) / Stopwatch.Frequency < 5.0)
                return;

            _lastTrimTimestamp = Stopwatch.GetTimestamp();

            try
            {
#pragma warning disable S1215 // Intentional for explicit manual low-memory working set trimmer
                // 1. Collect gen 0, 1, and 2 garbage with compaction and run pending finalizers
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
#pragma warning restore S1215

                // 2. Instruct Windows Memory Manager to trim unreferenced working set pages
                Win32Interop.SetProcessWorkingSetSize(_currentProcessHandle, new IntPtr(-1), new IntPtr(-1));
                Win32Interop.EmptyWorkingSet(_currentProcessHandle);
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogTag, $"Memory trim skipped: {ex.Message}");
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
