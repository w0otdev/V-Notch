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


    private const int MaxResults = 50;
    private static readonly Comparison<SpotlightSearchItem> s_mergeComparison = CompareMergedItems;

    private readonly ISpotlightProvider[] _providers;
    private readonly ISpotlightProvider[] _instantProviders;
    private readonly ISpotlightProvider[] _deferredProviders;
    private readonly WindowsSearchProvider? _windowsSearchProvider;
    private readonly EverythingSearchProvider? _everythingSearchProvider;
    private readonly LocalFileSearchProvider? _localFileSearchProvider;
    private readonly AppSearchProvider[] _appSearchProviders;
    private readonly SystemFileSearchProvider[] _systemFileSearchProviders;
    private readonly SpotlightUsageStore? _usage;

    public bool IsWindowsSearchAvailable =>
        (_windowsSearchProvider?.IsAvailable ?? false) ||
        (_everythingSearchProvider?.IsAvailable ?? false) ||
        (_localFileSearchProvider?.IsAvailable ?? false);

    public SpotlightSearchService(
        IEnumerable<ISpotlightProvider> providers,
        SpotlightUsageStore? usage = null)
    {
        _providers = providers as ISpotlightProvider[] ?? new List<ISpotlightProvider>(providers).ToArray();
        _usage = usage;

        var instantList = new List<ISpotlightProvider>(_providers.Length);
        var deferredList = new List<ISpotlightProvider>(_providers.Length);
        var appProviders = new List<AppSearchProvider>(2);
        var sysFileProviders = new List<SystemFileSearchProvider>(2);
        Func<string, bool>? isExcludedDelegate = _usage != null
            ? path => _usage.Preferences.IsExcluded(path)
            : null;

        for (int i = 0; i < _providers.Length; i++)
        {
            var provider = _providers[i];
            if (provider.IsInstant)
                instantList.Add(provider);
            else
                deferredList.Add(provider);

            if (provider is EverythingSearchProvider everything)
            {
                _everythingSearchProvider ??= everything;
                if (isExcludedDelegate != null)
                    everything.IsExcluded = isExcludedDelegate;
            }
            else if (provider is WindowsSearchProvider windows)
            {
                _windowsSearchProvider ??= windows;
                if (isExcludedDelegate != null)
                    windows.IsExcluded = isExcludedDelegate;
            }
            else if (provider is AppSearchProvider appProvider)
            {
                appProviders.Add(appProvider);
            }
            else if (provider is SystemFileSearchProvider sysFileProvider)
            {
                sysFileProviders.Add(sysFileProvider);
            }
            else if (provider is LocalFileSearchProvider localFiles)
            {
                _localFileSearchProvider = localFiles;
                localFiles.IsExcluded = isExcludedDelegate;
                localFiles.ExcludedFolders = _usage != null ? () => _usage.Preferences.GetExcludedFolders() : null;
            }
        }

        _instantProviders = instantList.ToArray();
        _deferredProviders = deferredList.ToArray();
        _appSearchProviders = appProviders.ToArray();
        _systemFileSearchProviders = sysFileProviders.ToArray();
    }

    internal Task WarmupAsync()
    {
        int total = _appSearchProviders.Length + _systemFileSearchProviders.Length;
        if (total == 0) return Task.CompletedTask;

        var tasks = new Task[total];
        int idx = 0;
        for (int i = 0; i < _appSearchProviders.Length; i++)
            tasks[idx++] = _appSearchProviders[i].WarmupAsync();
        for (int i = 0; i < _systemFileSearchProviders.Length; i++)
            tasks[idx++] = _systemFileSearchProviders[i].WarmupAsync();

        return Task.WhenAll(tasks);
    }

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

    private async Task<IReadOnlyList<SpotlightSearchItem>> SearchProvidersCoreAsync(
        ISpotlightProvider[] selectedProviders,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(query, limit, out query, out limit))
            return Array.Empty<SpotlightSearchItem>();

        var tasks = new Task<IReadOnlyList<SpotlightSearchItem>>[selectedProviders.Length];
        for (int i = 0; i < selectedProviders.Length; i++)
        {
            tasks[i] = SearchProviderAsync(selectedProviders[i], query, MaxResults, cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedForUsage = _usage != null ? SettingsSearchMatcher.Normalize(query) : string.Empty;
        var groups = new IReadOnlyList<SpotlightSearchItem>[results.Length + 2];
        for (int i = 0; i < results.Length; i++)
        {
            groups[i] = ApplyUsageBoost(results[i], normalizedForUsage);
        }

        groups[results.Length] = _usage?.Preferences.Search(query) ?? Array.Empty<SpotlightSearchItem>();
        groups[results.Length + 1] = ApplyUsageBoost(
            _usage?.GetRememberedItems(query) ?? Array.Empty<SpotlightSearchItem>(),
            normalizedForUsage);

        return Merge(groups, limit);
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
            _ = vn_spotlight_search_compute_boosted_score(
                item.Score, usageBoost, pinned ? (byte)1 : (byte)0, out double boosted);
            output.Add(item with
            {
                IsPinned = pinned,
                Score = boosted
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
        int scoreCmp = b.Score.CompareTo(a.Score);
        if (scoreCmp != 0) return scoreCmp;

        int titleCmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title);
        if (titleCmp != 0) return titleCmp;

        return StringComparer.Ordinal.Compare(a.Id, b.Id);
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
