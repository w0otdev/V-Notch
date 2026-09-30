using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static class RecordingFileOwnership
{
    internal static bool MatchesRecorder(UniqueProcess owner, int processId, long startFileTime)
        => owner.ProcessId == (uint)processId &&
           (((long)owner.StartTime.dwHighDateTime << 32) | (uint)owner.StartTime.dwLowDateTime) == startFileTime;

    internal static bool IsFileOwnedBy(string path, Process process)
    {
        long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        foreach (var owner in GetFileOwners(path))
            if (MatchesRecorder(owner.Process, process.Id, start) && !process.HasExited)
                return true;
        return false;
    }

    private static ProcessInfo[] GetFileOwners(string path)
    {
        if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0)
            return Array.Empty<ProcessInfo>();
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return Array.Empty<ProcessInfo>();

            uint count = 16, reason = 0;
            var owners = new ProcessInfo[count];
            int result = RmGetList(session, out uint needed, ref count, owners, ref reason);
            if (result == 234 && needed <= 128) // ERROR_MORE_DATA; bounded retry for changing owners.
            {
                count = needed;
                owners = new ProcessInfo[count];
                result = RmGetList(session, out _, ref count, owners, ref reason);
            }
            if (result != 0) return Array.Empty<ProcessInfo>();
            Array.Resize(ref owners, (int)count);
            return owners;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UniqueProcess
    {
        public uint ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, AppStatus, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    // Restart Manager is used only to query owners. Never stop or restart applications.
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint appCount, IntPtr apps, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[] info, ref uint reason);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
