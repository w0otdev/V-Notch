namespace VNotch.Services;

internal static class MediaTimelinePolicy
{
    public static bool HasFiniteDuration(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(30);

    public static bool IsIndeterminate(TimeSpan duration, bool reportedIndeterminate) =>
        reportedIndeterminate || !HasFiniteDuration(duration);
}
