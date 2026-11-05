namespace VNotch.Services;

public interface IMediaMetadataLookupService
{
    Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default);

    Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default);

    Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default);

    Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default);

    Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default);

    Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, string expectedTitle, string expectedArtist = "", CancellationToken ct = default)
        => TryGetSoundCloudArtworkFromUrlAsync(soundCloudUrl, ct);

    Task<string?> TryGetSpotifyFullArtistsAsync(string title, string primaryArtist, TimeSpan duration = default, string? albumTitle = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    Task<string?> TryGetSpotifyArtworkUrlAsync(string title, string primaryArtist, TimeSpan duration = default, string? albumTitle = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
