using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class RecordingFileOwnership
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string Path, int Pid, long Start), (bool Owned, long Expires)> Cache = new();
    internal static bool MatchesRecorder(UniqueProcess owner, int processId, long startFileTime)
    {
        vn_recording_matches_recorder(
            owner.ProcessId,
            owner.StartTime.dwHighDateTime,
            (uint)owner.StartTime.dwLowDateTime,
            processId,
            startFileTime,
            out byte matches);
        return matches != 0;
    }

    internal static bool IsFileOwnedByCached(string path, Process process, int positiveTtlMs = 1000, int negativeTtlMs = 1000)
    {
        if (string.IsNullOrEmpty(path)) return false;
        long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        var key = (System.IO.Path.GetFullPath(path).ToUpperInvariant(), process.Id, start);
        long now = Environment.TickCount64;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached) && now < cached.Expires)
                return cached.Owned && !process.HasExited;
        }
        bool result = IsFileOwnedBy(path, process);
        lock (CacheLock)
        {
            foreach (var entry in Cache.Where(entry => entry.Value.Expires <= now).ToArray()) Cache.Remove(entry.Key);
            if (Cache.Count >= 128) Cache.Remove(Cache.Keys.First());
            Cache[key] = (result, now + (result ? positiveTtlMs : negativeTtlMs));
        }
        return result;
    }

    internal static unsafe bool IsFileOwnedBy(string path, Process process)
    {
        if (string.IsNullOrEmpty(path)) return false;
        long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        byte owned;
        fixed (char* ptr = path)
            vn_recording_is_file_owned_by_process(ptr, path.Length, process.Id, start, out owned);
        return owned != 0 && !process.HasExited;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UniqueProcess
    {
        public uint ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_recording_matches_recorder(
        uint owner_pid,
        int high_date_time,
        uint low_date_time,
        int process_id,
        long start_file_time,
        out byte out_matches);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_recording_is_file_owned_by_process(
        char* path_ptr, int path_len,
        int process_id, long start_file_time,
        out byte out_owned);
}
