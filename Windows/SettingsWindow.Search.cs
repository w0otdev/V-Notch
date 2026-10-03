using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
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

    private DispatcherTimer? _searchDebounce;
    private bool _isSearchMode;
    private readonly List<SearchRowEntry> _searchRows = new();

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
            _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
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
        if (SettingsSearchBox.IsFocused || !string.IsNullOrEmpty(query))
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            SearchPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ExecuteSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        string normalizedQuery = SettingsSearchMatcher.Normalize(query);
        bool enteredSearchMode = EnterSearchMode();
        SearchResultsStack.Children.Clear();

        var matches = _searchRows
            .Select(row => (Row: row, Score: GetSearchRowScore(row, normalizedQuery, allowFuzzy: false)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Row)
            .ToList();

        // Approximate spelling is a fallback, never extra noise beside direct matches.
        if (matches.Count == 0)
        {
            matches = _searchRows
                .Select(row => (Row: row, Score: GetSearchRowScore(row, normalizedQuery, allowFuzzy: true)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .Select(match => match.Row)
                .ToList();
        }

        foreach (var match in matches)
        {
            match.OriginalVisibility = match.Row.Visibility;

            if (match.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(match.Row);
            }

            match.Row.Visibility = Visibility.Visible;
            SearchResultsStack.Children.Add(match.Row);
        }

        bool wasShowingEmptyState = SearchingEmptyState.Visibility == Visibility.Visible;
        bool showEmptyState = matches.Count == 0;
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

    private static int GetSearchRowScore(SearchRowEntry row, string query, bool allowFuzzy)
    {
        int contentScore = SettingsSearchMatcher.GetNormalizedMatchScore(row.NormalizedSearchText, query, allowFuzzy);
        // Category names can qualify a query, but must not generate typo matches for
        // every setting in that category. Prefer matches within the setting itself.
        int categoryScore = SettingsSearchMatcher.GetNormalizedMatchScore(
            row.NormalizedSectionText + " " + row.NormalizedSearchText, query, allowFuzzy: false);
        return Math.Max(contentScore > 0 ? contentScore * 10 + 1 : 0, categoryScore * 10);
    }

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
            kvp.Value.Background = isSearching
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;

            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isSearching ? _whiteBrush : _navInactiveBrush;
        }
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
        foreach (var kvp in _navPanels)
        {
            if (kvp.Key == NavSectionSearching) continue;

            foreach (var row in FindVisualChildren<Border>(kvp.Value))
            {
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
                    BuildSearchText(row),
                    BuildSectionSearchText(kvp.Key)));
            }
        }
    }

    private void RestoreSearchRows()
    {
        foreach (var row in _searchRows.OrderBy(r => r.OriginalIndex))
        {
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
        foreach (var row in _searchRows)
        {
            row.OriginalVisibility = row.Row.Visibility;
            row.UpdateSearchText(BuildSearchText(row.Row), BuildSectionSearchText(row.Section));
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
            string searchText,
            string sectionText)
        {
            Section = section;
            Row = row;
            OriginalParent = originalParent;
            OriginalIndex = originalIndex;
            OriginalVisibility = originalVisibility;
            UpdateSearchText(searchText, sectionText);
        }

        public string Section { get; }
        public Border Row { get; }
        public StackPanel OriginalParent { get; }
        public int OriginalIndex { get; }
        public Visibility OriginalVisibility { get; set; }
        public string NormalizedSearchText { get; private set; } = string.Empty;
        public string NormalizedSectionText { get; private set; } = string.Empty;

        public void UpdateSearchText(string searchText, string sectionText)
        {
            NormalizedSearchText = SettingsSearchMatcher.Normalize(searchText);
            NormalizedSectionText = SettingsSearchMatcher.Normalize(sectionText);
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
            kvp.Value.Background = isActive
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;
            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isActive ? _whiteBrush : _navInactiveBrush;
        }
    }

    #endregion
}
