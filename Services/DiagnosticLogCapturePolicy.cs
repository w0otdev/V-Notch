using System.Threading;

namespace VNotch.Services;

internal sealed class DiagnosticLogCapturePolicy
{
    internal const int MaxEntriesPerSecond = 32;
    private long _windowAndCount = -1;

    internal bool TryCapture(long tickCountMs)
    {
        long window = (tickCountMs / 1000) << 32;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long previous = Volatile.Read(ref _windowAndCount);
            uint count = (previous & ~0xFFFFFFFFL) == window ? (uint)previous : 0;
            if (count >= MaxEntriesPerSecond) return false;
            long next = window | (count + 1);
            if (Interlocked.CompareExchange(ref _windowAndCount, next, previous) == previous)
                return true;
        }
        // Contention is itself a reason to skip diagnostic capture. RuntimeLog's
        // file writer still retains the original operational entry.
        return false;
    }
}
