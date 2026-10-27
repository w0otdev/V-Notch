using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private int _labelEditorVersion;
    private void SynchronizeLabels()
    {
        if (_controller == null) return;
        var labels = _controller.Store.GetLabels();
        string[] desired = labels.Select(label => label.Id).SequenceEqual(ClipboardClassifier.GroupNames)
            ? CategoryKinds.Select(kind => kind.ToString()).ToArray()
            : [.. CategoryKinds.Take(8).Select(kind => kind.ToString()), .. labels.Select(label => label.Id), "Personal", "Archive"];
        if (_categoryItems.Select(item => item.Name).SequenceEqual(desired)) return;
        var existing = _categoryItems.ToDictionary(item => item.Name);
        _categoryItems = desired.Select(name => existing.TryGetValue(name, out var category) ? category :
            ClipboardLabel.IsCustomId(name) ? new ClipboardCategoryViewModel(TrayIconKind.Shopping, name, labels.First(label => label.Id == name).Name) :
            new ClipboardCategoryViewModel(Enum.Parse<TrayIconKind>(name))).ToArray();
        CategoryPanel.ItemsSource = _categoryItems;
    }

    private void LocalizeLabelActions()
    {
        AddLabelButton.ToolTip = Loc.Get("clipboard.addLabel");
        SelectAllButton.ToolTip = Loc.Get("clipboard.selectAll");
        System.Windows.Automation.AutomationProperties.SetName(AddLabelButton, Loc.Get("clipboard.addLabel"));
        System.Windows.Automation.AutomationProperties.SetName(SelectAllButton, Loc.Get("clipboard.selectAll"));
        LabelEditorTitle.Text = Loc.Get("clipboard.addLabel");
        LabelEditorHint.Text = Loc.Get("clipboard.labelHint");
        SaveLabelButton.Content = Loc.Get("clipboard.createLabel");
        CancelLabelButton.Content = Loc.Get("clipboard.cancel");
        System.Windows.Automation.AutomationProperties.SetName(LabelNameBox, Loc.Get("clipboard.labelName"));
        UpdateSelectionActions();
    }

    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private void UpdateSelectionActions()
    {
        if (DeleteSelectedButton == null || Cards == null) return;
        DeleteSelectedButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTrayButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        string label = Loc.Get("clipboard.deleteSelected", Cards.SelectedItems.Count);
        DeleteSelectedButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(DeleteSelectedButton, label);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAllTrayItems(null);
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await SafeAsync(() => ConfirmDeleteAsync(SelectedCards()));

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null || _disposed) return;
        ++_labelEditorVersion;
        EndSelection();
        LabelNameBox.Clear();
        LabelEditorError.Text = "";
        LabelEditorPanel.Visibility = Visibility.Visible;
        LabelNameBox.Focus();
    }

    private void CloseLabelEditor()
    {
        bool wasOpen = LabelEditorPanel.Visibility == Visibility.Visible;
        ++_labelEditorVersion;
        LabelEditorPanel.Visibility = Visibility.Collapsed;
        LabelNameBox.Clear();
        if (wasOpen && !_disposed && !_trayLeaving && IsVisible) AddLabelButton.Focus();
    }

    private void CancelLabel_Click(object sender, RoutedEventArgs e) => CloseLabelEditor();
    private async void SaveLabel_Click(object sender, RoutedEventArgs e) => await SaveLabelAsync();
    private async void LabelName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SaveLabelAsync();
    }

    private async Task SaveLabelAsync()
    {
        if (_controller == null || !SaveLabelButton.IsEnabled) return;
        int version = _labelEditorVersion;
        SaveLabelButton.IsEnabled = false;
        try
        {
            var label = await _controller.Store.AddLabelAsync(LabelNameBox.Text);
            if (_disposed || version != _labelEditorVersion || LabelEditorPanel.Visibility != Visibility.Visible) return;
            CloseLabelEditor();
            RebuildCategories();
            SelectCategory(label.Id);
            CategoryScroll.ScrollToRightEnd();
        }
        catch (ArgumentException) { LabelEditorError.Text = Loc.Get("clipboard.invalidLabel"); }
        catch (Exception ex)
        {
            RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
            LabelEditorError.Text = Loc.Get("clipboard.error");
        }
        finally { SaveLabelButton.IsEnabled = true; }
    }

    private void Category_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: ClipboardCategoryViewModel category } button || _controller == null) return;
        var labels = _controller.Store.GetLabels();
        int index = labels.ToList().FindIndex(label => label.Id == category.Name);
        if (index < 0) { e.Handled = true; return; }
        var menu = button.ContextMenu!;
        menu.Items.Clear();
        AddMenu(menu, "clipboard.moveLabelLeft", () => MoveLabelAsync(category.Name, -1));
        AddMenu(menu, "clipboard.moveLabelRight", () => MoveLabelAsync(category.Name, 1));
        ((MenuItem)menu.Items[0]).IsEnabled = index > 0;
        ((MenuItem)menu.Items[1]).IsEnabled = index < labels.Count - 1;
        menu.PlacementTarget = button;
        menu.Opened -= LabelMenu_Opened;
        menu.Opened += LabelMenu_Opened;
        menu.Closed -= LabelMenu_Closed;
        menu.Closed += LabelMenu_Closed;
    }

    private void LabelMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        _openMenus.Add(menu);
        EnsureMenuTopmost(menu);
    }
    private void LabelMenu_Closed(object sender, RoutedEventArgs e) => _openMenus.Remove((ContextMenu)sender);

    private async Task MoveLabelAsync(string id, int offset)
    {
        if (_controller == null) return;
        await _controller.Store.MoveLabelAsync(id, offset);
        if (!_disposed) RebuildCategories();
    }
}
