using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

internal static class PrivacyProcessSnapshot
{
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

    internal static unsafe Dictionary<string, List<uint>> Capture()
    {
        var processes = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        // Enumerate process IDs and executable names once, without opening process
        // handles or loading modules. Full paths are queried only for candidates.
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return processes;
        ProcessEntry entry = default;
        entry.Size = (uint)sizeof(ProcessEntry);
        if (!Process32First(snapshot, ref entry)) return processes;
        do
        {
            if (entry.ProcessId == 0) continue;
            int length = 0;
            while (length < 260 && entry.ExecutableFile[length] != '\0') length++;
            string name = new(entry.ExecutableFile, 0, length);
            if (!processes.TryGetValue(name, out var ids))
                processes[name] = ids = new List<uint>(1);
            ids.Add(entry.ProcessId);
        } while (Process32Next(snapshot, ref entry));
        return processes;
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
