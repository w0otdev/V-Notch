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
}
