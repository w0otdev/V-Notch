using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    internal static partial int vn_spotlight_search_compare_keys(
        double scoreA,
        ReadOnlySpan<char> titleAPtr,
        int titleALen,
        ReadOnlySpan<char> idAPtr,
        int idALen,
        double scoreB,
        ReadOnlySpan<char> titleBPtr,
        int titleBLen,
        ReadOnlySpan<char> idBPtr,
        int idBLen,
        out int outCmp);

    private const int MaxResults = 50;
    private const int ProviderSearchDebounceMs = 75;

    internal readonly record struct SearchUpdate(
        IReadOnlyList<SpotlightSearchItem> Results, bool IsComplete, bool IsImmediateComplete, SearchCompleteness FileCompleteness = SearchCompleteness.Complete);
    private static readonly Comparison<SpotlightSearchItem> s_mergeComparison = CompareMergedItems;
    internal static readonly Comparison<SpotlightSearchItem> s_scoreAndTitleComparison = CompareScoreAndTitle;

    private readonly ISpotlightProvider[] _providers;
    private readonly ISpotlightProvider[] _instantProviders;
    private readonly ISpotlightProvider[] _deferredProviders;
    private readonly SpotlightUsageStore? _usage;

    public bool IsWindowsSearchAvailable => _providers.Any(p => p.FileRole != FileSearchRole.None && p.IsAvailable);

    public SpotlightSearchService(IEnumerable<ISpotlightProvider> providers, SpotlightUsageStore? usage = null)
    {
        _providers = providers.ToArray();
        _usage = usage;
        _instantProviders = _providers.Where(p => p.IsInstant).ToArray();
        _deferredProviders = _providers.Where(p => !p.IsInstant).ToArray();
        foreach (var provider in _providers)
            provider.ConfigureExclusions(usage == null ? null : path => usage.Preferences.IsExcluded(path),
                usage == null ? null : () => usage.Preferences.GetExcludedFolders());
    }

    internal Task WarmupAsync() => Task.WhenAll(_providers.Select(p => p.WarmupAsync()));

    internal Task<IReadOnlyList<SpotlightSearchItem>> SearchInstantAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        SearchProvidersCoreAsync(_instantProviders, query, limit, cancellationToken);

    internal Task<IReadOnlyList<SpotlightSearchItem>> SearchDeferredAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        SearchProvidersCoreAsync(_deferredProviders, query, limit, cancellationToken);

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        SearchProvidersCoreAsync(_providers, query, limit, cancellationToken);

    internal IAsyncEnumerable<SearchUpdate> SearchUpdatesAsync(
        string query, int limit, CancellationToken cancellationToken) =>
        SearchUpdatesCoreAsync(_providers, query, limit, ProviderSearchDebounceMs, cancellationToken);

    private async Task<IReadOnlyList<SpotlightSearchItem>> SearchProvidersCoreAsync(
        ISpotlightProvider[] selectedProviders,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SpotlightSearchItem> results = Array.Empty<SpotlightSearchItem>();
        await foreach (var update in SearchUpdatesCoreAsync(selectedProviders, query, limit, 0, cancellationToken)
            .ConfigureAwait(false))
            results = update.Results;
        return results;
    }

    private async IAsyncEnumerable<SearchUpdate> SearchUpdatesCoreAsync(
        ISpotlightProvider[] selectedProviders, string query, int limit, int debounceMilliseconds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeInput(query, limit, out query, out limit))
        {
            yield return new(Array.Empty<SpotlightSearchItem>(), IsComplete: true, IsImmediateComplete: true);
            yield break;
        }

        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken queryToken = queryCts.Token;
        string normalizedForUsage = _usage != null ? SettingsSearchMatcher.Normalize(query) : string.Empty;

        var tasks = new Task<SearchBatch>[selectedProviders.Length];
        var immediateProviders = new bool[selectedProviders.Length];
        int immediateRemaining = 1; // Preference/history results are part of the immediate phase.
        int everythingIndex = -1;
        for (int i = 0; i < selectedProviders.Length; i++)
        {
            if (selectedProviders[i].FileRole == FileSearchRole.AuthoritativeIndex && everythingIndex < 0)
                everythingIndex = i;
            if (selectedProviders[i].FileRole == FileSearchRole.Fallback) continue;
            immediateProviders[i] = !selectedProviders[i].DeferSearch;
            if (immediateProviders[i]) immediateRemaining++;
            tasks[i] = SearchDelayedProviderAsync(selectedProviders[i], query,
                immediateProviders[i] ? 0 : debounceMilliseconds, queryToken);
        }
        for (int i = 0; i < selectedProviders.Length; i++)
        {
            if (selectedProviders[i].FileRole == FileSearchRole.Fallback)
                tasks[i] = SearchLocalFallbackAsync(selectedProviders[i],
                    everythingIndex >= 0 ? tasks[everythingIndex] : null, query, queryToken,
                    debounceMilliseconds);
        }

        // Preference/history lookup can probe local paths. Treat it as another
        // independent source so it cannot hold up completed in-memory providers.
        var personalization = Task.Run(() => new IReadOnlyList<SpotlightSearchItem>[]
        {
            _usage?.Preferences.Search(query) ?? Array.Empty<SpotlightSearchItem>(),
            ApplyUsageBoost(_usage?.GetRememberedItems(query) ?? Array.Empty<SpotlightSearchItem>(), normalizedForUsage)
        }, queryToken);

        var allTasks = new Task[tasks.Length + 1];
        Array.Copy(tasks, allTasks, tasks.Length);
        allTasks[tasks.Length] = personalization;

        try
        {
            var groups = new IReadOnlyList<SpotlightSearchItem>[tasks.Length + 2];
            Array.Fill(groups, Array.Empty<SpotlightSearchItem>());
            var pending = new List<Task>(allTasks);
            var fileStates = new Dictionary<int, SearchCompleteness>();

            while (pending.Count > 0)
            {
                await Task.WhenAny(pending).WaitAsync(queryToken).ConfigureAwait(false);
                queryToken.ThrowIfCancellationRequested();
                // Collect providers that finished together into one UI update.
                for (int index = 0; index < tasks.Length; index++)
                {
                    if (!tasks[index].IsCompleted || !pending.Remove(tasks[index])) continue;
                    var batch = await tasks[index].ConfigureAwait(false);
                    groups[index] = ApplyUsageBoost(batch.Results, normalizedForUsage);
                    if (selectedProviders[index].FileRole != FileSearchRole.None) fileStates[index] = batch.Completeness;
                    if (immediateProviders[index]) immediateRemaining--;
                }
                if (personalization.IsCompleted && pending.Remove(personalization))
                {
                    var personalized = await personalization.ConfigureAwait(false);
                    groups[tasks.Length] = personalized[0];
                    groups[tasks.Length + 1] = personalized[1];
                    immediateRemaining--;
                }
                queryToken.ThrowIfCancellationRequested();
                yield return new(Merge(groups, limit), IsComplete: pending.Count == 0,
                    IsImmediateComplete: immediateRemaining == 0, FileCompleteness:
                        fileStates.Any(p => selectedProviders[p.Key].FileRole == FileSearchRole.AuthoritativeIndex && p.Value == SearchCompleteness.Complete)
                            ? SearchCompleteness.Complete
                            : fileStates.Values.Contains(SearchCompleteness.Partial) ? SearchCompleteness.Partial
                            : fileStates.Count > 0 && fileStates.Values.All(v => v == SearchCompleteness.Unavailable)
                                ? SearchCompleteness.Unavailable : SearchCompleteness.Complete);
            }
        }
        finally
        {
            queryCts.Cancel();
            // A superseded provider may still be inside native IPC. Observe its
            // eventual completion without keeping the replacement query waiting.
            _ = Task.WhenAll(allTasks).ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task<SearchBatch> SearchDelayedProviderAsync(
        ISpotlightProvider provider, string query, int debounceMilliseconds, CancellationToken cancellationToken)
    {
        if (debounceMilliseconds > 0)
            await Task.Delay(debounceMilliseconds, cancellationToken).ConfigureAwait(false);
        return await SearchProviderAsync(provider, query, MaxResults, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SearchBatch> SearchLocalFallbackAsync(
        ISpotlightProvider local, Task<SearchBatch>? indexedQuery,
        string query, CancellationToken cancellationToken, int debounceMilliseconds = 0)
    {
        // Delay independently from in-memory results, rather than waiting for
        // the entire instant phase before this provider can start.
        if (debounceMilliseconds > 0)
            await Task.Delay(debounceMilliseconds, cancellationToken).ConfigureAwait(false);
        // A successful indexed query with zero matches is still authoritative.
        // Await this query's IPC result so an unavailable / timed-out index keeps
        // the disk fallback, including on the first search after startup.
        var indexed = indexedQuery == null ? null : await indexedQuery.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (indexed?.Completeness == SearchCompleteness.Complete)
            return new([], SearchCompleteness.Complete);
        return await SearchProviderAsync(local, query, MaxResults, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<SpotlightSearchItem> ApplyUsageBoost(
        IReadOnlyList<SpotlightSearchItem> results, string normalizedQuery)
    {
        if (_usage == null || results.Count == 0) return results;

        var prefs = _usage.Preferences;
        var output = new List<SpotlightSearchItem>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (prefs.IsExcluded(item.Target))
                continue;

            bool pinned = prefs.IsPinned(item.Target);
            double usageBoost = _usage.GetBoostNormalized(item.Id, normalizedQuery);
            output.Add(item with
            {
                IsPinned = pinned,
                Score = item.Score + usageBoost + (pinned ? 150 : 0)
            });
        }

        return output;
    }

    internal static IReadOnlyList<SpotlightSearchItem> Merge(
        IEnumerable<IReadOnlyList<SpotlightSearchItem>> providerResults,
        int limit)
    {
        if (limit <= 0) return Array.Empty<SpotlightSearchItem>();

        var bestByTarget = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in providerResults)
        {
            int count = group.Count;
            for (int i = 0; i < count; i++)
            {
                var item = group[i];
                if (!bestByTarget.TryGetValue(item.Target, out var best) || item.Score > best.Score)
                    bestByTarget[item.Target] = item;
            }
        }

        if (bestByTarget.Count == 0)
            return Array.Empty<SpotlightSearchItem>();

        var buffer = new SpotlightSearchItem[bestByTarget.Count];
        bestByTarget.Values.CopyTo(buffer, 0);
        Array.Sort(buffer, s_mergeComparison);

        if (buffer.Length <= limit)
            return buffer;

        var trimmed = new SpotlightSearchItem[limit];
        Array.Copy(buffer, 0, trimmed, 0, limit);
        return trimmed;
    }

    private static int CompareMergedItems(SpotlightSearchItem a, SpotlightSearchItem b)
    {
        string titleA = a.Title ?? string.Empty;
        string titleB = b.Title ?? string.Empty;
        string idA = a.Id ?? string.Empty;
        string idB = b.Id ?? string.Empty;
        _ = vn_spotlight_search_compare_keys(
            a.Score,
            titleA.AsSpan(), titleA.Length,
            idA.AsSpan(), idA.Length,
            b.Score,
            titleB.AsSpan(), titleB.Length,
            idB.AsSpan(), idB.Length,
            out int cmp);
        return cmp;
    }

    internal static int CompareScoreAndTitle(SpotlightSearchItem a, SpotlightSearchItem b)
    {
        string titleA = a.Title ?? string.Empty;
        string titleB = b.Title ?? string.Empty;
        _ = vn_spotlight_search_compare_keys(
            a.Score,
            titleA.AsSpan(), titleA.Length,
            ReadOnlySpan<char>.Empty, 0,
            b.Score,
            titleB.AsSpan(), titleB.Length,
            ReadOnlySpan<char>.Empty, 0,
            out int cmp);
        return cmp;
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
        if (len <= 0) normalizedQuery = string.Empty;
        else normalizedQuery = start == 0 && len == query.Length ? query : query.Substring(start, len);

        return valid != 0;
    }

    private static async Task<SearchBatch> SearchProviderAsync(
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
            return await Task.Run(() => provider.SearchBatchAsync(query, limit, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-SEARCH", ex, $"{provider.GetType().Name} failed");
            return new([], SearchCompleteness.Unavailable);
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
