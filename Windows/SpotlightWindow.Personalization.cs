using System.IO;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private bool _personalizationOpen;
    private bool _resultMenuOpen;

    private void AddPersonalizationActions(ContextMenu menu, SpotlightSearchItem? item)
    {
        var preferences = _viewModel.Preferences;
        if (item?.Kind == SpotlightResultKind.Application)
        {
            AddMenuItem(menu, Loc.Get(preferences.IsPinned(item.Target) ? "spotlight.unpin" : "spotlight.pin"),
                () => TogglePinnedApplication(item));
            AddMenuItem(menu, Loc.Get("spotlight.alias"), () => EditSpotlightPreferences(item));
        }
        if (item?.Kind is SpotlightResultKind.File or SpotlightResultKind.Folder)
        {
            AddMenuItem(menu, Loc.Get("spotlight.excludeFolder"), () =>
            {
                string? folder = item.Kind == SpotlightResultKind.Folder ? item.Target : Path.GetDirectoryName(item.Target);
                if (string.IsNullOrEmpty(folder)) return;
                preferences.SetExcludedFolders(preferences.GetExcludedFolders().Append(folder));
                RefreshPersonalizedResults();
            });
        }
        AddMenuItem(menu, Loc.Get("spotlight.excludedFolders"), () => EditSpotlightPreferences(null));
        menu.Opened += (_, _) => _resultMenuOpen = true;
        menu.Closed += (_, _) => _resultMenuOpen = false;
    }

    private long _pinFeedbackVersion;

    private async void TogglePinnedApplication(SpotlightSearchItem item)
    {
        var preferences = _viewModel.Preferences;
        bool pinned = !preferences.IsPinned(item.Target);
        if (!preferences.SetApp(item, pinned, preferences.GetAlias(item.Target)))
        {
            ShowSpotlightMessage(Loc.Get("spotlight.pinLimit"));
            return;
        }

        // Keep the current row alive while the shared media pin animates. In
        // particular, unpinning an empty-query result must not remove it first.
        _viewModel.CancelPendingSearch();
        foreach (var result in _viewModel.Results.Where(result =>
            string.Equals(result.Target, item.Target, StringComparison.OrdinalIgnoreCase)))
            result.IsPinned = pinned;
        item.IsPinned = pinned;
        long version = ++_pinFeedbackVersion;
        int session = _animationGeneration;
        string query = SearchBox.Text;
        if (!AnimationConfig.ReduceMotion) await Task.Delay(pinned ? 240 : 160);
        if (version != _pinFeedbackVersion || session != _animationGeneration || query != SearchBox.Text) return;
        RefreshPersonalizedResults();
    }

    private async void RefreshPersonalizedResults()
    {
        if (!IsSpotlightOpen || _isClosing) return;
        await _viewModel.SearchAsync(SearchBox.Text);
        if (IsSpotlightOpen && !_isClosing) RefreshStatus();
    }

    private void SearchBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Preserve normal text editing commands alongside preference management.
        var menu = new ContextMenu { Style = (Style)FindResource("SpotlightContextMenuStyle"), PlacementTarget = SearchBox };
        AddMenuItem(menu, Loc.Get("spotlight.copy"), SearchBox.Copy);
        AddMenuItem(menu, Loc.Get("tooltip.paste"), SearchBox.Paste);
        AddMenuItem(menu, Loc.Get("tooltip.selectAll"), SearchBox.SelectAll);
        AddPersonalizationActions(menu, null);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void EditSpotlightPreferences(SpotlightSearchItem? item)
    {
        var preferences = _viewModel.Preferences;
        _personalizationOpen = true;
        try
        {
            var dialog = new Windows.SpotlightPreferencesDialog(preferences, item) { Owner = this };
            if (dialog.ShowDialog() == true) RefreshPersonalizedResults();
        }
        finally
        {
            _personalizationOpen = false;
            if (IsSpotlightOpen && !_isClosing) SearchBox.Focus();
        }
    }
}
