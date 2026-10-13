using System.Diagnostics;

namespace VNotch.Services;

internal static class ProcessPriorityPolicy
{
    internal const string Normal = "Normal";
    internal const string High = "High";

    internal static string Normalize(string? priority) =>
        string.Equals(priority, High, StringComparison.OrdinalIgnoreCase) ? High : Normal;

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
