using System.Buffers;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class PrivacyProcessSnapshot : IDisposable
{
    private Win32Interop.VnProcessEntry[]? _entries;
    internal int Count { get; private set; }
    internal ReadOnlySpan<Win32Interop.VnProcessEntry> Entries => _entries.AsSpan(0, Count);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_privproc_contains_consumer(ReadOnlySpan<Win32Interop.VnProcessEntry> entries, int count,
        ReadOnlySpan<char> consumer, int length, byte package, out byte running);

    internal bool ContainsConsumer(string consumer, bool package)
        => vn_privproc_contains_consumer(Entries, Count, consumer, consumer.Length, package ? (byte)1 : (byte)0, out byte running) == 0 && running != 0;


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

    public void Dispose()
    {
        if (_entries == null) return;
        ArrayPool<Win32Interop.VnProcessEntry>.Shared.Return(_entries);
        _entries = null;
        Count = 0;
    }


}
