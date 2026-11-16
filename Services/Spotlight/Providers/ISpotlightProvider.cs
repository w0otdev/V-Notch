using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal enum SearchCompleteness { Complete, Partial, Unavailable }
internal enum FileSearchRole { None, Supplemental, AuthoritativeIndex, Fallback }
internal sealed record SearchBatch(IReadOnlyList<SpotlightSearchItem> Results, SearchCompleteness Completeness);

internal interface ISpotlightProvider
{
    bool IsAvailable { get; }

    bool IsInstant => false;
    bool DeferSearch => !IsInstant;
    FileSearchRole FileRole => FileSearchRole.None;
    void ConfigureExclusions(Func<string, bool>? isExcluded, Func<IReadOnlyList<string>>? excludedFolders) { }
    Task WarmupAsync() => Task.CompletedTask;
    async Task<SearchBatch> SearchBatchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var results = await SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);
        return new(results, IsAvailable ? SearchCompleteness.Complete : SearchCompleteness.Unavailable);
    }

    Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}
