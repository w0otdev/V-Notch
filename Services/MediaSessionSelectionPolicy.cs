namespace VNotch.Services;

internal static class MediaSessionSelectionPolicy
{
    internal static T? SelectNewlyPlayingSession<T>(IReadOnlyList<T> newlyPlaying, T? osCurrent)
        where T : class
    {
        if (newlyPlaying.Count == 1) return newlyPlaying[0];
        return newlyPlaying.FirstOrDefault(session => ReferenceEquals(session, osCurrent));
    }
}
