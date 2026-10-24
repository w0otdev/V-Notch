using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class RecordingFileOwnership
{
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

    internal static unsafe bool IsFileOwnedBy(string path, Process process)
    {
        if (string.IsNullOrEmpty(path)) return false;
        long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        byte owned;
        fixed (char* ptr = path)
        {
            vn_recording_is_file_owned_by_process(ptr, path.Length, process.Id, start, out owned);
        }
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
