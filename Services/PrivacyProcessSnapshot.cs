using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

internal sealed class PrivacyProcessSnapshot : IDisposable
{
    private ProcessEntry[]? _entries;
    internal int Count { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    internal static unsafe PrivacyProcessSnapshot Capture()
    {
        var processes = new PrivacyProcessSnapshot();
        // Enumerate process IDs and executable names once, without opening process
        // handles or allocating a string/list for every process. Full paths are
        // queried only for candidates; native entries live in a rented buffer.
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return processes;
        ProcessEntry entry = default;
        entry.Size = (uint)sizeof(ProcessEntry);
        if (!Process32First(snapshot, ref entry)) return processes;
        try
        {
            processes._entries = ArrayPool<ProcessEntry>.Shared.Rent(256);
            do
            {
                if (entry.ProcessId == 0) continue;
                if (processes.Count == processes._entries.Length)
                {
                    var larger = ArrayPool<ProcessEntry>.Shared.Rent(processes.Count * 2);
                    processes._entries.AsSpan(0, processes.Count).CopyTo(larger);
                    ArrayPool<ProcessEntry>.Shared.Return(processes._entries);
                    processes._entries = larger;
                }
                processes._entries[processes.Count++] = entry;
            } while (Process32Next(snapshot, ref entry));
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
            int length = 0;
            while (length < 260 && name[length] != '\0') length++;
            return new ReadOnlySpan<char>(name, length).Equals(executableName, StringComparison.OrdinalIgnoreCase);
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
        ArrayPool<ProcessEntry>.Shared.Return(_entries);
        _entries = null;
        Count = 0;
    }

    // PROCESSENTRY32W contains WCHAR[MAX_PATH]; ANSI marshalling corrupts the names.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        public fixed char ExecutableFile[260];
    }
}
