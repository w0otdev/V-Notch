using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class PrivacyProcessSnapshot : IDisposable
{
    // A shared ArrayPool keeps a large snapshot on every worker that polls.
    // Keep one spare across workers instead; in-use snapshots remain independent.
    private static Win32Interop.VnProcessEntry[]? _spareEntries;
    private const int MaxRetainedEntries = 1024;
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
            var entries = Interlocked.Exchange(ref _spareEntries, null) ?? new Win32Interop.VnProcessEntry[256];
            processes._entries = entries; // Also return the buffer if native capture fails.
            int written = Win32Interop.CaptureProcessSnapshot(entries, out int total);
            if (total > entries.Length)
            {
                // Leave room for processes starting between the two snapshots.
                // The undersized buffer is no longer worth retaining.
                entries = new Win32Interop.VnProcessEntry[checked(total + 64)];
                processes._entries = entries;
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
        var entries = Interlocked.Exchange(ref _entries, null);
        if (entries == null) return;
        Count = 0;
        if (entries.Length <= MaxRetainedEntries)
            Interlocked.CompareExchange(ref _spareEntries, entries, null);
    }


}
