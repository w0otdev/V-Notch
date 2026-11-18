using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ProcessMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool;
        public nuint PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile;
        public nuint PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(nint process, ref Counters counters, uint size);

    internal static object Read(Process process, double seconds, string phase)
    {
        process.Refresh();
        var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new
        {
            Seconds = seconds, Phase = phase,
            WorkingSetBytes = (ulong)counters.WorkingSet,
            PrivateWorkingSetBytes = (ulong)counters.PrivateWorkingSet,
            PrivateBytes = (ulong)counters.PrivateUsage,
            ManagedBytes = GC.GetTotalMemory(false),
            ManagedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
            AllocatedBytes = GC.GetTotalAllocatedBytes(false),
            Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
            CpuSeconds = process.TotalProcessorTime.TotalSeconds,
            Threads = process.Threads.Count, Handles = process.HandleCount
        };
    }
}
