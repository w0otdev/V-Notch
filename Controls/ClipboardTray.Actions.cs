using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private Guid? _groupEntryId;
    private readonly HashSet<string> _selectedGroups = new(StringComparer.OrdinalIgnoreCase);
    private Task _groupSave = Task.CompletedTask;
    private int _groupChoiceVersion;
    private ClipboardGroupChoiceViewModel[] _groupChoices = [];

    private static bool IsCardAction(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private async void CardAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _dragStart = null; _pressedCard = null;
        if (_controller == null || sender is not Button { DataContext: ClipboardCardViewModel card, Tag: string action }) return;
        await SafeAsync(async () =>
        {
            switch (action)
            {
                case "details": await ShowDetailsAsync(card); break;
                case "pin": await _controller.Store.UpdateAsync(card.Entry.Id, pinned: !card.Entry.IsPinned); break;
                case "archive": await _controller.Store.UpdateAsync(card.Entry.Id, archived: !card.Entry.IsArchived); break;
                case "delete": await ConfirmDeleteAsync([card]); break;
                case "movePersonal":
                    if (!_controller.Store.IsPersonalUnlocked) { SelectCategory("Personal"); _moveToPersonal = card.Entry.Id; }
                    else await _controller.Store.MovePersonalAsync(card.Entry.Id, !card.Entry.IsPersonal);
                    break;
                case "groups": await ShowGroupChoicesAsync(card); break;
            }
        });
    }

    private async Task ShowGroupChoicesAsync(ClipboardCardViewModel card)
    {
        CloseDetails();
        int version = _groupChoiceVersion;
        await _groupSave;
        if (_disposed || !IsVisible || _controller == null || version != _groupChoiceVersion) return;
        var entry = _controller.Store.GetEntry(card.Entry.Id);
        if (entry == null || entry.IsPersonal != card.Entry.IsPersonal) return;
        DetailTextScroll.Visibility = Visibility.Collapsed;
        GroupChoices.Visibility = Visibility.Visible;
        _groupEntryId = card.Entry.Id;
        _selectedGroups.Clear();
        _selectedGroups.UnionWith(entry.Groups);
        _groupChoices = ClipboardClassifier.GroupNames.Select(group =>
            new ClipboardGroupChoiceViewModel(group, _selectedGroups.Contains(group), GroupChoiceChanged)).ToArray();
        GroupChoices.ItemsSource = _groupChoices;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void GroupChoiceChanged(ClipboardGroupChoiceViewModel choice)
    {
        if (_disposed || _controller == null || _groupEntryId is not Guid id ||
            !_groupChoices.Contains(choice)) return;
        if (choice.IsSelected) _selectedGroups.Add(choice.Name); else _selectedGroups.Remove(choice.Name);
        // Enqueue an immutable snapshot before yielding. Store.Run preserves click order.
        string[] groups = _selectedGroups.ToArray();
        _groupSave = SafeAsync(() => _controller.Store.UpdateAsync(id, groups: groups));
    }

    private void ClearGroupChoices()
    {
        ++_groupChoiceVersion;
        foreach (var choice in _groupChoices) choice.Deactivate();
        _groupChoices = [];
        GroupChoices.ItemsSource = null;
        _groupEntryId = null;
        _selectedGroups.Clear();
    }

}
