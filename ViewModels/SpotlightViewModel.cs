using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services.Spotlight;

namespace VNotch.ViewModels;

internal partial class SpotlightViewModel : ObservableObject, IDisposable
{
    private readonly SpotlightSearchService _search;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SpotlightSearchItem> Results { get; } = new();

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private SpotlightSearchItem? _selectedResult;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasNoResults;
    [ObservableProperty] private bool _isWindowsSearchUnavailable;

    public SpotlightViewModel(SpotlightSearchService search)
    {
        _search = search;
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
            Results.Clear();
            SelectedResult = null;
            IsSearching = false;
            HasNoResults = false;
            IsWindowsSearchUnavailable = false;
            return;
        }

        IsSearching = true;
        HasNoResults = false;
        try
        {
            await Task.Delay(100, cancellationToken);
            Task<IReadOnlyList<SpotlightSearchItem>> appResultsTask =
                _search.SearchApplicationsAsync(query, 10, cancellationToken);
            Task<IReadOnlyList<SpotlightSearchItem>> otherResultsTask =
                _search.SearchNonApplicationsAsync(query, 10, cancellationToken);

            var appResults = await appResultsTask;
            cancellationToken.ThrowIfCancellationRequested();
            Publish(appResults);

            var otherResults = await otherResultsTask;
            cancellationToken.ThrowIfCancellationRequested();
            Publish(SpotlightSearchService.Merge([appResults, otherResults], 10));
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

    private void Publish(IReadOnlyList<SpotlightSearchItem> results)
    {
        string? selectedId = SelectedResult?.Id;
        Results.Clear();
        // Applications always form the first visual section, so the collection
        // must match that order for index-based keyboard navigation to work.
        foreach (var result in results.OrderBy(item =>
                     item.Kind == SpotlightResultKind.Application ? 0 : 1))
        {
            Results.Add(result);
        }
        SelectedResult = selectedId == null
            ? Results.FirstOrDefault()
            : Results.FirstOrDefault(result => result.Id == selectedId) ?? Results.FirstOrDefault();
        HasNoResults = Results.Count == 0;
    }

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
