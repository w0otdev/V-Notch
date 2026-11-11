using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;

namespace VNotch;

public partial class SettingsWindow
{
    private readonly HashSet<SearchRowEntry> _dirtySearchRows = [];
    private readonly HashSet<DependencyObject> _observedSearchInputs = [];
    private readonly List<Action> _detachSearchObservers = [];
    private bool _updatingSearchPresentation;

    private void InvalidateSearchIndex() => _dirtySearchRows.UnionWith(_searchRows);

    private void ObserveSearchRow(SearchRowEntry row)
    {
        ObserveSearchInputs(row.Row, row);
        RoutedEventHandler loaded = (_, _) => ObserveSearchInputs(row.Row, row);
        row.Row.Loaded += loaded;
        _detachSearchObservers.Add(() => row.Row.Loaded -= loaded);
    }

    // Search text is independent of the query. Watch the inputs once instead of
    // traversing every Settings panel and normalizing every row on each keypress.
    private void ObserveSearchInputs(DependencyObject input, SearchRowEntry? row)
    {
        if (_observedSearchInputs.Add(input))
        {
            void Changed(object? sender, EventArgs e)
            {
                if (_updatingSearchPresentation) return;
                if (row == null) InvalidateSearchIndex();
                else _dirtySearchRows.Add(row);
            }

            void Watch(DependencyProperty property)
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, input.GetType());
                if (descriptor == null) return;
                descriptor.AddValueChanged(input, Changed);
                _detachSearchObservers.Add(() => descriptor.RemoveValueChanged(input, Changed));
            }

            if (input is TextBlock) Watch(TextBlock.TextProperty);
            if (input is ContentControl) Watch(ContentControl.ContentProperty);
            if (input is ElasticSlider)
            {
                Watch(ElasticSlider.LabelProperty);
                Watch(ElasticSlider.DescriptionProperty);
                Watch(ElasticSlider.UnitProperty);
            }
            if (input is UIElement)
            {
                var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, input.GetType());
                EventHandler visibilityChanged = (_, _) =>
                {
                    if (_updatingSearchPresentation) return;
                    if (row != null && ReferenceEquals(input, row.Row))
                        row.OriginalVisibility = row.Row.Visibility;
                    Changed(input, EventArgs.Empty);
                };
                visibility.AddValueChanged(input, visibilityChanged);
                _detachSearchObservers.Add(() => visibility.RemoveValueChanged(input, visibilityChanged));
            }
            if (input is ItemsControl items)
            {
                NotifyCollectionChangedEventHandler collectionChanged = (_, _) =>
                {
                    Changed(input, EventArgs.Empty);
                    ObserveSearchItemInputs(items, row);
                };
                ((INotifyCollectionChanged)items.Items).CollectionChanged += collectionChanged;
                _detachSearchObservers.Add(() => ((INotifyCollectionChanged)items.Items).CollectionChanged -= collectionChanged);
            }
            // Newly realized templates may contribute text that was absent when
            // a collapsed section was first indexed.
            if (row != null) _dirtySearchRows.Add(row);
        }

        if (input is ItemsControl itemsControl) ObserveSearchItemInputs(itemsControl, row);
        if (input is not Visual) return;
        int count = VisualTreeHelper.GetChildrenCount(input);
        for (int i = 0; i < count; i++)
            ObserveSearchInputs(VisualTreeHelper.GetChild(input, i), row);
    }

    private void ObserveSearchItemInputs(ItemsControl items, SearchRowEntry? row)
    {
        foreach (var item in items.Items)
            if (item is DependencyObject dependencyObject)
                ObserveSearchInputs(dependencyObject, row);
    }

    private void DetachSearchObservers()
    {
        foreach (var detach in _detachSearchObservers) detach();
        _detachSearchObservers.Clear();
        _observedSearchInputs.Clear();
        _dirtySearchRows.Clear();
    }

    private void RestoreSearchRow(SearchRowEntry row)
    {
        if (ReferenceEquals(row.Row.Parent, row.OriginalParent)) return;
        if (row.Row.Parent is Panel parent) parent.Children.Remove(row.Row);
        int insertIndex = row.OriginalIndex;
        // Earlier rows can remain in the results. Account for their vacant slots
        // so restoring only one changed result still preserves its source order.
        foreach (var peer in _searchRows)
            if (ReferenceEquals(peer.OriginalParent, row.OriginalParent) &&
                peer.OriginalIndex < row.OriginalIndex && !ReferenceEquals(peer.Row.Parent, row.OriginalParent))
                insertIndex--;
        row.OriginalParent.Children.Insert(Math.Clamp(insertIndex, 0, row.OriginalParent.Children.Count), row.Row);
        row.Row.Visibility = row.OriginalVisibility;
    }

    private void ApplySearchResultDiff(ReadOnlySpan<VnSettingsSearchMatchResult> matches)
    {
        var desired = new HashSet<Border>();
        foreach (var match in matches)
            if ((uint)match.RowIndex < (uint)_searchRows.Count) desired.Add(_searchRows[match.RowIndex].Row);

        _updatingSearchPresentation = true;
        try
        {
            foreach (var row in _searchRows)
                if (ReferenceEquals(row.Row.Parent, SearchResultsStack) && !desired.Contains(row.Row))
                    RestoreSearchRow(row);

            int index = 0;
            foreach (var match in matches)
            {
                if ((uint)match.RowIndex >= (uint)_searchRows.Count) continue;
                var row = _searchRows[match.RowIndex];
                if (index >= SearchResultsStack.Children.Count || !ReferenceEquals(SearchResultsStack.Children[index], row.Row))
                {
                    if (row.Row.Parent is Panel parent) parent.Children.Remove(row.Row);
                    SearchResultsStack.Children.Insert(index, row.Row);
                }
                row.Row.Visibility = Visibility.Visible;
                index++;
            }
        }
        finally { _updatingSearchPresentation = false; }
    }
}
