using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

internal sealed partial class PrivacyProcessSnapshot : IDisposable
{
    private Win32Interop.VnProcessEntry[]? _entries;
    internal int Count { get; private set; }


    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_privproc_matches_exe_name(
        char* entryExeFile,
        ReadOnlySpan<ushort> exeName, int exeNameLen,
        out byte outMatches);

    internal static unsafe PrivacyProcessSnapshot Capture()
    {
        var processes = new PrivacyProcessSnapshot();
        try
        {
            // The native snapshot reports the live process count, so one retry always fits.
            var entries = ArrayPool<Win32Interop.VnProcessEntry>.Shared.Rent(256);
            int written = Win32Interop.CaptureProcessSnapshot(entries, out int total);
            if (total > entries.Length)
            {
                ArrayPool<Win32Interop.VnProcessEntry>.Shared.Return(entries);
                entries = ArrayPool<Win32Interop.VnProcessEntry>.Shared.Rent(total + 64);
                written = Win32Interop.CaptureProcessSnapshot(entries, out _);
            }
            processes._entries = entries;
            processes.Count = written;
            return processes;
        }
        catch
        {
            processes.Dispose();
            throw;
        }
    }

    internal uint GetProcessId(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _entries![index].ProcessId;
    }

    internal unsafe bool MatchesExecutableName(int index, ReadOnlySpan<char> executableName)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        fixed (char* name = _entries![index].ExecutableFile)
        {
            return vn_privproc_matches_exe_name(name, MemoryMarshal.Cast<char, ushort>(executableName), executableName.Length, out byte matches) == 0 && matches != 0;
        }
    }

    internal Process[] GetProcessesByExecutableName(string executableName, string? alternativeName = null)
    {
        List<Process>? matches = null;
        for (int i = 0; i < Count; i++)
        {
            if (!MatchesExecutableName(i, executableName) &&
                (alternativeName == null || !MatchesExecutableName(i, alternativeName))) continue;
            try
            {
                var process = Process.GetProcessById(checked((int)GetProcessId(i)));
                (matches ??= new List<Process>()).Add(process);
            }
            catch (ArgumentException)
            {
                // The process may have exited after the snapshot was captured.
            }
        }
        return matches?.ToArray() ?? Array.Empty<Process>();
    }

    public void Dispose()
    {
        if (_entries == null) return;
        ArrayPool<Win32Interop.VnProcessEntry>.Shared.Return(_entries);
        _entries = null;
        Count = 0;
    }


}
