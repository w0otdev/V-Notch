using VNotch.Services.Interop;

namespace VNotch.Services;

internal static class MediaTimelinePolicy
{
    // Matches the existing native contract (including long recordings).
    public static readonly TimeSpan MaxReasonableDuration = TimeSpan.FromDays(30);

    public static bool HasFiniteDuration(TimeSpan duration) => MediaPolicyNative.HasFiniteDuration(duration);
    public static bool IsIndeterminate(TimeSpan duration, bool reportedIndeterminate) =>
        MediaPolicyNative.IsIndeterminate(duration, reportedIndeterminate);
}
