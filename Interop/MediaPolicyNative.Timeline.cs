using System.Runtime.InteropServices;

namespace VNotch.Interop;

internal static partial class MediaPolicyNative
{
    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_timeline_policy_has_finite_duration(long durationTicks, out byte outFinite);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_timeline_policy_is_indeterminate(long durationTicks, byte reportedIndeterminate, out byte outIndeterminate);

    public static bool HasFiniteDuration(TimeSpan duration)
    {
        int result = vn_timeline_policy_has_finite_duration(duration.Ticks, out byte finite);
        NativeResult.RequireSuccess(result, nameof(HasFiniteDuration));
        return finite != 0;
    }

    public static bool IsIndeterminate(TimeSpan duration, bool reportedIndeterminate)
    {
        int result = vn_timeline_policy_is_indeterminate(duration.Ticks, reportedIndeterminate ? (byte)1 : (byte)0, out byte ind);
        NativeResult.RequireSuccess(result, nameof(IsIndeterminate));
        return ind != 0;
    }
}
