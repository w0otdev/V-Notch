using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public sealed class MemoryOptimizerService
{
    private static readonly Lazy<MemoryOptimizerService> _lazy =
        new(() => new MemoryOptimizerService());

    public static MemoryOptimizerService Instance => _lazy.Value;

    private readonly IntPtr _currentProcessHandle = Win32Interop.GetCurrentProcess();
    private long _lastTrimTimestamp = 0;
    private readonly object _trimLock = new();

    private MemoryOptimizerService()
    {
    }

    /// <summary>
    /// Schedules a full working set trim and garbage collection after application startup has settled.
    /// </summary>
    public void SchedulePostStartupTrim(int delayMs = 3500)
    {
        Task.Delay(delayMs).ContinueWith(_ =>
        {
            TrimWorkingSet(aggressive: true);
            RuntimeLog.Log("MEMORY", "Post-startup working set compaction completed.");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Performs an efficient garbage collection and working set trim to release unneeded committed pages back to Windows.
    /// </summary>
    public void TrimWorkingSet(bool aggressive = false)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (double)(now - _lastTrimTimestamp) / Stopwatch.Frequency;

        // Rate limit non-aggressive trims to at most once every 30 seconds
        if (!aggressive && elapsedSec < 30.0)
            return;

        lock (_trimLock)
        {
            if (!aggressive && (double)(Stopwatch.GetTimestamp() - _lastTrimTimestamp) / Stopwatch.Frequency < 30.0)
                return;

            _lastTrimTimestamp = Stopwatch.GetTimestamp();

            try
            {
                // 1. Collect gen 0, 1, and 2 garbage with compaction
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);

                // 2. Instruct Windows Memory Manager to trim unreferenced working set pages
                if (!Win32Interop.SetProcessWorkingSetSize(_currentProcessHandle, new IntPtr(-1), new IntPtr(-1)))
                {
                    Win32Interop.EmptyWorkingSet(_currentProcessHandle);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("MEMORY", $"Memory trim skipped: {ex.Message}");
            }
        }
    }
}
