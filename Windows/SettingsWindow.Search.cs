using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Search

    private UiScheduledTimer? _searchDebounce;
    private bool _isSearchMode;
    private readonly List<SearchRowEntry> _searchRows = new();
    private readonly List<(SearchRowEntry Row, int Score, int Order)> _scoredSearchMatchesBuffer = new(64);
    private readonly List<SearchRowEntry> _restoreRowsBuffer = new(64);

    private static readonly Comparison<(SearchRowEntry Row, int Score, int Order)> s_scoredMatchComparison =
        static (a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        };

    private static readonly Comparison<SearchRowEntry> s_restoreOrderComparison =
        static (a, b) => a.OriginalIndex.CompareTo(b.OriginalIndex);

    private void SettingsSearchSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!SettingsSearchBox.IsKeyboardFocusWithin)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.CaretIndex = SettingsSearchBox.Text?.Length ?? 0;
        }
    }

    private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SettingsSearchBox.Text))
        {
            e.Handled = true;
            SettingsSearchBox.Clear();
        }
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";

        UpdateSearchPlaceholderVisibility();

        if (string.IsNullOrEmpty(query))
        {
            _searchDebounce?.Stop();
            ExitSearchMode();
            return;
        }

        if (_searchDebounce == null)
        {
            _searchDebounce = new UiScheduledTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _searchDebounce.Tick += (s, _) =>
            {
                _searchDebounce.Stop();
                ExecuteSearch(SettingsSearchBox.Text?.Trim() ?? "");
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SettingsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void SettingsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";
        bool hasText = !string.IsNullOrEmpty(query);
        SearchPlaceholder.Visibility = (SettingsSearchBox.IsFocused || hasText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (FindName("SettingsSearchInlineClearButton") is UIElement clearBtn)
        {
            clearBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ExecuteSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        string normalizedQuery = SettingsSearchMatcher.Normalize(query);
        bool enteredSearchMode = EnterSearchMode();
        SearchResultsStack.Children.Clear();

        _scoredSearchMatchesBuffer.Clear();
        for (int i = 0; i < _searchRows.Count; i++)
        {
            var row = _searchRows[i];
            int score = GetSearchRowScore(row, normalizedQuery, allowFuzzy: false);
            if (score > 0)
            {
                _scoredSearchMatchesBuffer.Add((row, score, i));
            }
        }

        // Approximate spelling is a fallback when direct matches are absent.
        if (_scoredSearchMatchesBuffer.Count == 0)
        {
            for (int i = 0; i < _searchRows.Count; i++)
            {
                var row = _searchRows[i];
                int score = GetSearchRowScore(row, normalizedQuery, allowFuzzy: true);
                if (score > 0)
                {
                    _scoredSearchMatchesBuffer.Add((row, score, i));
                }
            }
        }

        _scoredSearchMatchesBuffer.Sort(s_scoredMatchComparison);

        for (int i = 0; i < _scoredSearchMatchesBuffer.Count; i++)
        {
            var match = _scoredSearchMatchesBuffer[i].Row;
            match.OriginalVisibility = match.Row.Visibility;

            if (match.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(match.Row);
            }

            match.Row.Visibility = Visibility.Visible;
            SearchResultsStack.Children.Add(match.Row);
        }

        bool wasShowingEmptyState = SearchingEmptyState.Visibility == Visibility.Visible;
        bool showEmptyState = _scoredSearchMatchesBuffer.Count == 0;
        SearchingEmptyQuery.Text = showEmptyState ? $"“{query}”" : string.Empty;
        SearchingEmptyState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        SettingsScrollViewer.ScrollToTop();
        // Animate entering search or changing between results and 404, not query edits.
        if (enteredSearchMode || wasShowingEmptyState != showEmptyState)
        {
            AnimateActivePanel(NavSectionSearching);
        }
    }

    private void SearchingClearButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsSearchBox.Clear();
        SettingsSearchBox.Focus();
    }

    private static int GetSearchRowScore(SearchRowEntry row, string query, bool allowFuzzy) =>
        SettingsSearchMatcher.GetRowMatchScore(
            row.NormalizedTitleText,
            row.NormalizedSearchText,
            row.NormalizedCombinedText,
            query,
            allowFuzzy);

    private bool EnterSearchMode()
    {
        if (_isSearchMode)
        {
            RestoreSearchRows();
            RefreshSearchRows();
            return false;
        }

        _isSearchMode = true;
        IndexSearchRows();
        RefreshSearchRows();
        _activeNav = NavSectionSearching;
        UpdateSectionHeader();

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == NavSectionSearching ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateNavButtonsForSearchMode();
        return true;
    }

    private void UpdateNavButtonsForSearchMode()
    {
        foreach (var kvp in _navButtons)
        {
            bool isSearching = kvp.Key == NavSectionSearching;
            kvp.Value.Visibility = Visibility.Visible;
            kvp.Value.IsHitTestVisible = isSearching;
            kvp.Value.Opacity = isSearching ? 1.0 : 0.35;
            UpdateNavButtonVisual(kvp.Value, isSearching, animate: false);
        }
        SyncNavIndicators(animate: true);
    }

    private void ExitSearchMode()
    {
        RestoreSearchRows();
        SearchResultsStack.Children.Clear();
        SearchingEmptyState.Visibility = Visibility.Collapsed;
        SearchingEmptyQuery.Text = string.Empty;
        _isSearchMode = false;

        if (_activeNav == NavSectionSearching)
        {
            _activeNav = NavSectionAppearance;
            UpdateSectionHeader();
        }

        ShowAllNavItems();
        if (_navButtons.TryGetValue(NavSectionSearching, out var searchButton))
        {
            searchButton.Visibility = Visibility.Collapsed;
        }

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == _activeNav ? Visibility.Visible : Visibility.Collapsed;
        }

        AnimateActivePanel(_activeNav);
    }

    private void IndexSearchRows()
    {
        if (_searchRows.Count > 0) return;

        var rowStyle = FindResource("SettingRowBorder") as Style;
        var rowBorders = new List<Border>(32);
        foreach (var kvp in _navPanels)
        {
            if (kvp.Key == NavSectionSearching) continue;

            rowBorders.Clear();
            CollectVisualBorders(kvp.Value, rowBorders);
            string sectionText = BuildSectionSearchText(kvp.Key);

            for (int i = 0; i < rowBorders.Count; i++)
            {
                var row = rowBorders[i];
                if (row.Style != rowStyle || row.Parent is not StackPanel parent)
                {
                    continue;
                }

                _searchRows.Add(new SearchRowEntry(
                    kvp.Key,
                    row,
                    parent,
                    parent.Children.IndexOf(row),
                    row.Visibility,
                    BuildPrimaryTitleText(row),
                    BuildSearchText(row),
                    sectionText));
            }
        }
    }

    private void RestoreSearchRows()
    {
        _restoreRowsBuffer.Clear();
        for (int i = 0; i < _searchRows.Count; i++)
        {
            _restoreRowsBuffer.Add(_searchRows[i]);
        }

        _restoreRowsBuffer.Sort(s_restoreOrderComparison);

        for (int i = 0; i < _restoreRowsBuffer.Count; i++)
        {
            var row = _restoreRowsBuffer[i];
            if (row.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(row.Row);
            }

            int insertIndex = Math.Clamp(row.OriginalIndex, 0, row.OriginalParent.Children.Count);
            row.OriginalParent.Children.Insert(insertIndex, row.Row);
            row.Row.Visibility = row.OriginalVisibility;
        }
    }

    private string BuildSectionSearchText(string section)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { section };
        if (_navButtons.TryGetValue(section, out var navButton))
        {
            CollectSearchText(navButton, parts);
        }

        return string.Join(" ", parts);
    }

    private string BuildPrimaryTitleText(DependencyObject root)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPrimaryTitleText(root, parts);
        return string.Join(" ", parts);
    }

    private static bool CollectPrimaryTitleText(DependencyObject current, ISet<string> parts)
    {
        switch (current)
        {
            case ElasticSlider slider when !string.IsNullOrWhiteSpace(slider.Label):
                AddAllTranslations(slider.Label, parts);
                return true;
            case CheckBox checkBox when checkBox.Content is string checkText && !string.IsNullOrWhiteSpace(checkText):
                AddAllTranslations(checkText, parts);
                return true;
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text) && textBlock.Text.Length > 1:
                AddAllTranslations(textBlock.Text, parts);
                return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < childCount; i++)
        {
            if (CollectPrimaryTitleText(VisualTreeHelper.GetChild(current, i), parts))
                return true;
        }
        return false;
    }

    private string BuildSearchText(DependencyObject root)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSearchText(root, parts);
        return string.Join(" ", parts);
    }

    private static void AddAllTranslations(string text, ISet<string> parts)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (string translation in Loc.GetAllTranslations(text))
        {
            parts.Add(translation);
        }
    }

    private void RefreshSearchRows()
    {
        var sectionTextCache = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < _searchRows.Count; i++)
        {
            var row = _searchRows[i];
            row.OriginalVisibility = row.Row.Visibility;
            if (!sectionTextCache.TryGetValue(row.Section, out string? sectionText))
            {
                sectionText = BuildSectionSearchText(row.Section);
                sectionTextCache[row.Section] = sectionText;
            }

            row.UpdateSearchText(BuildPrimaryTitleText(row.Row), BuildSearchText(row.Row), sectionText);
        }
    }

    private void CollectSearchText(DependencyObject current, ISet<string> parts)
    {
        switch (current)
        {
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                AddAllTranslations(textBlock.Text, parts);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                AddAllTranslations(checkText, parts);
                break;
            case ContentControl contentControl when contentControl.Content is string contentText:
                AddAllTranslations(contentText, parts);
                break;
            case ElasticSlider slider:
                AddAllTranslations(slider.Label, parts);
                AddAllTranslations(slider.Description, parts);
                AddAllTranslations(slider.Unit, parts);
                break;
            case ItemsControl itemsControl:
                foreach (object item in itemsControl.Items)
                {
                    AddSearchItemText(item, parts);
                }

                if (ReferenceEquals(itemsControl, LanguageCombo))
                {
                    AddLanguageSearchTerms(parts);
                }

                break;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < childCount; i++)
        {
            CollectSearchText(VisualTreeHelper.GetChild(current, i), parts);
        }
    }

    private static void AddSearchItemText(object? item, ISet<string> parts)
    {
        switch (item)
        {
            case null:
                return;
            case string text:
                AddAllTranslations(text, parts);
                return;
            case ContentControl contentControl:
                AddSearchItemText(contentControl.Content, parts);
                if (contentControl.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    parts.Add(tag);
                }

                return;
            case SubtitlePriorityItem subtitleItem:
                AddAllTranslations(subtitleItem.DisplayName, parts);
                parts.Add(subtitleItem.Key);
                return;
            case CameraDeviceItem camera:
                parts.Add(camera.Name);
                return;
            case AudioDeviceItem audioDevice:
                parts.Add(audioDevice.Name);
                return;
        }
    }

    private static void AddLanguageSearchTerms(ISet<string> parts)
    {
        foreach (var (code, nativeName) in Loc.GetAvailableLanguages())
        {
            parts.Add(code);
            parts.Add(nativeName);

            string cultureName = code switch
            {
                "vi" => "vi-VN",
                "es" => "es-ES",
                "fr" => "fr-FR",
                "de" => "de-DE",
                "ja" => "ja-JP",
                "hi" => "hi-IN",
                _ => "en-US"
            };
            var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            parts.Add(culture.EnglishName);
            parts.Add(culture.NativeName);
        }
    }

    private static void CollectVisualBorders(DependencyObject parent, List<Border> results)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border)
            {
                results.Add(border);
            }

            CollectVisualBorders(child, results);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SearchRowEntry
    {
        public SearchRowEntry(
            string section,
            Border row,
            StackPanel originalParent,
            int originalIndex,
            Visibility originalVisibility,
            string titleText,
            string searchText,
            string sectionText)
        {
            Section = section;
            Row = row;
            OriginalParent = originalParent;
            OriginalIndex = originalIndex;
            OriginalVisibility = originalVisibility;
            UpdateSearchText(titleText, searchText, sectionText);
        }

        public string Section { get; }
        public Border Row { get; }
        public StackPanel OriginalParent { get; }
        public int OriginalIndex { get; }
        public Visibility OriginalVisibility { get; set; }
        public string NormalizedTitleText { get; private set; } = string.Empty;
        public string NormalizedSearchText { get; private set; } = string.Empty;
        public string NormalizedSectionText { get; private set; } = string.Empty;
        public string NormalizedCombinedText { get; private set; } = string.Empty;

        public void UpdateSearchText(string titleText, string searchText, string sectionText)
        {
            NormalizedTitleText = SettingsSearchMatcher.Normalize(titleText);
            NormalizedSearchText = SettingsSearchMatcher.Normalize(searchText);
            NormalizedSectionText = SettingsSearchMatcher.Normalize(sectionText);
            NormalizedCombinedText = string.Concat(NormalizedSectionText, " ", NormalizedSearchText);
        }
    }

    private static readonly SolidColorBrush _whiteBrush = VNotch.Services.UiPalette.PrimaryBrush;
    private static readonly SolidColorBrush _navInactiveBrush = VNotch.Services.UiPalette.IconBrush;
    private static readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);

    static SettingsWindow()
    {
        _whiteBrush.Freeze();
        _navInactiveBrush.Freeze();
        _transparentBrush.Freeze();
    }

    private void ShowAllNavItems()
    {
        foreach (var kvp in _navButtons)
        {
            kvp.Value.IsHitTestVisible = true;
            kvp.Value.Opacity = 1.0;
            bool isActive = kvp.Key == _activeNav;
            UpdateNavButtonVisual(kvp.Value, isActive, animate: true);
        }
        SyncNavIndicators(animate: true);
    }

    #endregion
}
