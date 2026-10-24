using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class ProcessPriorityPolicy
{
    internal const string Normal = "Normal";
    internal const string High = "High";

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_process_priority_is_high(char* priority, int priorityLen, out byte outIsHigh);

    internal static unsafe string Normalize(string? priority)
    {
        if (string.IsNullOrEmpty(priority))
            return Normal;

        fixed (char* ptr = priority)
        {
            _ = vn_process_priority_is_high(ptr, priority.Length, out byte isHigh);
            return isHigh != 0 ? High : Normal;
        }
    }

    internal static ProcessPriorityClass Resolve(string? priority) =>
        string.Equals(Normalize(priority), High, StringComparison.Ordinal)
            ? ProcessPriorityClass.High
            : ProcessPriorityClass.Normal;

    internal static void Apply(string? priority)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.PriorityClass = Resolve(priority);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SYSTEM", $"Failed to set process priority to {Normalize(priority)}: {ex.Message}");
        }
    }
}
