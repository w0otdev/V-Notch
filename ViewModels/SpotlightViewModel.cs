using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services.Spotlight;

namespace VNotch.ViewModels;

internal partial class SpotlightViewModel : ObservableObject, IDisposable
{
    private const int ResultLimit = 10;

    // Only the Windows Search phase is debounced; the in-memory providers run
    // on every keystroke so the first paint is instant.
    private const int DeferredSearchDebounceMs = 150;

    private readonly SpotlightSearchService _search;
    private readonly SpotlightUsageStore _usage;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SpotlightSearchItem> Results { get; } = new();

    /// <summary>Raised on the UI thread after every result publish (including no-op ones).</summary>
    public event EventHandler? ResultsPublished;

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private SpotlightSearchItem? _selectedResult;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasNoResults;
    [ObservableProperty] private bool _isWindowsSearchUnavailable;

    public SpotlightViewModel(SpotlightSearchService search, SpotlightUsageStore usage)
    {
        _search = search;
        _usage = usage;
    }

    public async Task SearchAsync(string query)
    {
        Query = query;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(query))
        {
            // An empty query shows only the bare search bar; usage history
            // still feeds the ranking boost but is never displayed on its own.
            IsSearching = false;
            HasNoResults = false;
            IsWindowsSearchUnavailable = false;
            Publish(Array.Empty<SpotlightSearchItem>(), markNoResults: false);
            return;
        }

        IsSearching = true;
        HasNoResults = false;
        try
        {
            var instantResults = await _search.SearchInstantAsync(query, ResultLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Publish(instantResults);

            await Task.Delay(DeferredSearchDebounceMs, cancellationToken);
            var deferredResults = await _search.SearchDeferredAsync(query, ResultLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Publish(SpotlightSearchService.Merge([instantResults, deferredResults], ResultLimit));
            IsWindowsSearchUnavailable = !_search.IsWindowsSearchAvailable;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsSearching = false;
        }
    }

    public void RecordLaunch(SpotlightSearchItem item) => _usage.RecordLaunch(item);

    /// <summary>
    /// Drops an item whose launch failed (stale index entry) and moves the
    /// selection to its nearest neighbour so Enter stays useful.
    /// </summary>
    public void RemoveResult(SpotlightSearchItem item)
    {
        int index = Results.IndexOf(item);
        if (index < 0) return;

        bool wasSelected = SelectedResult == item;
        Results.RemoveAt(index);
        if (wasSelected)
        {
            SelectedResult = Results.Count > 0
                ? Results[Math.Min(index, Results.Count - 1)]
                : null;
        }
        if (Results.Count == 0 && !string.IsNullOrWhiteSpace(Query)) HasNoResults = true;
    }

    private void Publish(IReadOnlyList<SpotlightSearchItem> results, bool markNoResults = true)
    {
        // Sections render in collection order, so the collection must match the
        // visual order for index-based keyboard navigation to work.
        var ordered = results.OrderBy(SectionRank).ToList();

        string? selectedId = SelectedResult?.Id;
        int selectedIndex = SelectedResult == null ? -1 : Results.IndexOf(SelectedResult);

        ApplyDiff(ordered);

        SelectedResult = selectedId == null
            ? Results.FirstOrDefault()
            : Results.FirstOrDefault(result => result.Id == selectedId)
              ?? (Results.Count == 0
                  ? null
                  : Results[Math.Clamp(selectedIndex, 0, Results.Count - 1)]);
        if (markNoResults) HasNoResults = Results.Count == 0;
        ResultsPublished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reconciles Results toward <paramref name="ordered"/> with minimal
    /// Move/Insert/Remove operations instead of Clear+re-add, so unchanged rows
    /// keep their containers: no selection-accent replay, no scroll reset, and
    /// an identical republish (the common phase-2 case) emits no events at all.
    /// </summary>
    private void ApplyDiff(IReadOnlyList<SpotlightSearchItem> ordered)
    {
        for (int i = Results.Count - 1; i >= 0; i--)
        {
            string id = Results[i].Id;
            if (!ordered.Any(item => item.Id == id)) Results.RemoveAt(i);
        }

        for (int target = 0; target < ordered.Count; target++)
        {
            SpotlightSearchItem incoming = ordered[target];
            int existing = -1;
            for (int i = target; i < Results.Count; i++)
            {
                if (Results[i].Id == incoming.Id)
                {
                    existing = i;
                    break;
                }
            }

            if (existing < 0)
            {
                Results.Insert(target, incoming);
                continue;
            }

            if (existing != target) Results.Move(existing, target);
            // Keep the old instance (and its container) when the row would look
            // the same; Score changes alone are invisible.
            if (!VisuallyEqual(Results[target], incoming)) Results[target] = incoming;
        }

        while (Results.Count > ordered.Count) Results.RemoveAt(Results.Count - 1);
    }

    private static bool VisuallyEqual(SpotlightSearchItem current, SpotlightSearchItem incoming) =>
        current.Title == incoming.Title
        && current.Subtitle == incoming.Subtitle
        && current.Kind == incoming.Kind
        && current.IsRecent == incoming.IsRecent
        && current.IconPath == incoming.IconPath
        && (current.Icon == null) == (incoming.Icon == null);

    private static int SectionRank(SpotlightSearchItem item) => item.IsRecent
        ? 0
        : item.Kind switch
        {
            SpotlightResultKind.Calculation => 0,
            SpotlightResultKind.Application => 1,
            _ => 2
        };

    public void Reset()
    {
        CancelPendingSearch();
        Query = string.Empty;
        Results.Clear();
        SelectedResult = null;
        HasNoResults = false;
        IsWindowsSearchUnavailable = false;
    }

    public void CancelPendingSearch()
    {
        var cts = Interlocked.Exchange(ref _searchCts, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        IsSearching = false;
    }

    public void Dispose()
    {
        CancelPendingSearch();
    }
}
