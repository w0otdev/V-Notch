using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal interface ISpotlightProvider
{
    bool IsAvailable { get; }

    /// <summary>
    /// Instant providers answer from memory and run on every keystroke;
    /// non-instant providers are debounced because each query is expensive.
    /// </summary>
    bool IsInstant => false;

    Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}
