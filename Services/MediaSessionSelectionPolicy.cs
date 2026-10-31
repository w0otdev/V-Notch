namespace VNotch.Services;

internal static class MediaSessionSelectionPolicy
{
    internal static T? SelectNewlyPlayingSession<T>(IReadOnlyList<T> newlyPlaying, T? osCurrent, Func<T, T, bool>? areSame = null)
        where T : class
    {
        if (newlyPlaying.Count == 1) return newlyPlaying[0];
        if (osCurrent is null) return null;
        return newlyPlaying.FirstOrDefault(session => areSame != null ? areSame(session, osCurrent) : ReferenceEquals(session, osCurrent));
    }
}
