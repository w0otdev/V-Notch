using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class MediaTimelinePolicy
{
    public static readonly TimeSpan MaxReasonableDuration = TimeSpan.FromHours(12);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timeline_policy_has_finite_duration(long durationTicks, out byte outFinite);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timeline_policy_is_indeterminate(long durationTicks, byte reportedIndeterminate, out byte outIndeterminate);

    public static bool HasFiniteDuration(TimeSpan duration)
    {
        _ = vn_timeline_policy_has_finite_duration(duration.Ticks, out byte finite);
        return finite != 0;
    }

    public static bool IsIndeterminate(TimeSpan duration, bool reportedIndeterminate)
    {
        _ = vn_timeline_policy_is_indeterminate(duration.Ticks, reportedIndeterminate ? (byte)1 : (byte)0, out byte ind);
        return ind != 0;
    }
}
