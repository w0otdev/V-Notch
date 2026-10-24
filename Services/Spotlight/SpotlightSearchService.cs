using System.IO;
using System.Runtime.InteropServices;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Spotlight.Providers;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightSearchService
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_search_try_normalize_input(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        int limit,
        out byte outValid,
        out int outStart,
        out int outLen,
        out int outLimit);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_search_compute_boosted_score(
        double baseScore,
        double usageBoost,
        byte isPinned,
        out double outScore);

    private const int MaxQueryLength = 256;
    private const int MaxResults = 50;
    private readonly IReadOnlyList<ISpotlightProvider> _providers;
    private readonly SpotlightUsageStore? _usage;

    public bool IsWindowsSearchAvailable =>
        (_providers.OfType<WindowsSearchProvider>().FirstOrDefault()?.IsAvailable ?? false) ||
        (_providers.OfType<EverythingSearchProvider>().FirstOrDefault()?.IsAvailable ?? false);

    public SpotlightSearchService(
        IEnumerable<ISpotlightProvider> providers,
        SpotlightUsageStore? usage = null)
    {
        _providers = providers.ToArray();
        _usage = usage;
        foreach (var provider in _providers.OfType<EverythingSearchProvider>())
            provider.IsExcluded = path => _usage?.Preferences.IsExcluded(path) == true;
        foreach (var provider in _providers.OfType<WindowsSearchProvider>())
            provider.IsExcluded = path => _usage?.Preferences.IsExcluded(path) == true;
    }

    internal Task WarmupAsync() =>
        Task.WhenAll(
            _providers.OfType<AppSearchProvider>().Select(provider => provider.WarmupAsync())
                .Concat(_providers.OfType<SystemFileSearchProvider>().Select(provider => provider.WarmupAsync())));

    internal Task<IReadOnlyList<SpotlightSearchItem>> SearchInstantAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        SearchGroupAsync(provider => provider.IsInstant, query, limit, cancellationToken);

    internal Task<IReadOnlyList<SpotlightSearchItem>> SearchDeferredAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        SearchGroupAsync(provider => !provider.IsInstant, query, limit, cancellationToken);

    private async Task<IReadOnlyList<SpotlightSearchItem>> SearchGroupAsync(
        Func<ISpotlightProvider, bool> selector,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var results = await Task.WhenAll(_providers.Where(selector).Select(provider =>
            SearchProviderAsync(provider, query, MaxResults, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Merge(results.Select(items => ApplyUsageBoost(items, query))
            .Append(_usage?.Preferences.Search(query) ?? Array.Empty<SpotlightSearchItem>())
            .Append(ApplyUsageBoost(_usage?.GetRememberedItems(query) ?? Array.Empty<SpotlightSearchItem>(), query)), limit);
    }

    private IReadOnlyList<SpotlightSearchItem> ApplyUsageBoost(
        IReadOnlyList<SpotlightSearchItem> results, string query)
    {
        if (_usage == null) return results;
        return results
            .Where(item => !_usage.Preferences.IsExcluded(item.Target))
            .Select(item =>
            {
                bool pinned = _usage.Preferences.IsPinned(item.Target);
                _ = vn_spotlight_search_compute_boosted_score(
                    item.Score, _usage.GetBoost(item.Id, query), pinned ? (byte)1 : (byte)0, out double boosted);
                return item with
                {
                    IsPinned = pinned,
                    Score = boosted
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var providerTasks = _providers.Select(provider =>
            SearchProviderAsync(provider, query, MaxResults, cancellationToken));
        var providerResults = await Task.WhenAll(providerTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return Merge(providerResults.Select(items => ApplyUsageBoost(items, query))
            .Append(_usage?.Preferences.Search(query) ?? Array.Empty<SpotlightSearchItem>())
            .Append(ApplyUsageBoost(_usage?.GetRememberedItems(query) ?? Array.Empty<SpotlightSearchItem>(), query)), limit);
    }

    internal static IReadOnlyList<SpotlightSearchItem> Merge(
        IEnumerable<IReadOnlyList<SpotlightSearchItem>> providerResults,
        int limit)
    {
        var bestByTarget = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in providerResults)
            foreach (var item in group)
            {
                if (!bestByTarget.TryGetValue(item.Target, out var best) || item.Score > best.Score)
                    bestByTarget[item.Target] = item;
            }
        var results = bestByTarget.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return results;
    }

    private static bool TryNormalizeInput(
        string query,
        int limit,
        out string normalizedQuery,
        out int normalizedLimit)
    {
        if (query == null)
        {
            normalizedQuery = string.Empty;
            normalizedLimit = Math.Clamp(limit, 0, MaxResults);
            return false;
        }
        _ = vn_spotlight_search_try_normalize_input(
            query.AsSpan(), query.Length, limit,
            out byte valid, out int start, out int len, out normalizedLimit);
        normalizedQuery = len > 0 ? query.Substring(start, len) : string.Empty;
        return valid != 0;
    }

    private static async Task<IReadOnlyList<SpotlightSearchItem>> SearchProviderAsync(
        ISpotlightProvider provider,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            // An async provider may run synchronously until its first incomplete
            // await (warm indexes and native IPC in particular). Never run that
            // prefix on the caller's UI thread.
            return await Task.Run(() => provider.SearchAsync(query, limit, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-SEARCH", ex, $"{provider.GetType().Name} failed");
            return Array.Empty<SpotlightSearchItem>();
        }
    }

    internal static SpotlightSearchItem LoadIcon(SpotlightSearchItem item)
    {
        string? path = item.IconPath;
        if (item.Icon != null) return item;
        if (string.IsNullOrEmpty(path) || LocalPathPolicy.IsUncPath(path) ||
            (!File.Exists(path) && !Directory.Exists(path))) return item;
        return item with { Icon = FileIconProvider.GetFileIcon(path) };
    }
}
