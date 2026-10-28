using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using VNotch.ViewModels;

namespace VNotch.Controls;

// Retain the full lightweight list for selection and scrolling, but notify
// WPF once when a large search result replaces it.
internal sealed class ClipboardCardCollection : ObservableCollection<ClipboardCardViewModel>
{
    internal Dictionary<Guid, ClipboardCardViewModel> ById { get; } = new();

    internal void ReplaceAll(IReadOnlyList<ClipboardCardViewModel> cards)
    {
        CheckReentrancy();
        Items.Clear();
        ById.Clear();
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            card.SlotIndex = i;
            Items.Add(card);
            ById[card.Entry.Id] = card;
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void ClearItems()
    {
        ById.Clear();
        base.ClearItems();
    }

    protected override void InsertItem(int index, ClipboardCardViewModel item)
    {
        base.InsertItem(index, item);
        ById[item.Entry.Id] = item;
        for (int i = index; i < Items.Count; i++)
            Items[i].SlotIndex = i;
    }

    protected override void RemoveItem(int index)
    {
        var removed = Items[index];
        ById.Remove(removed.Entry.Id);
        base.RemoveItem(index);
        for (int i = index; i < Items.Count; i++)
            Items[i].SlotIndex = i;
    }

    protected override void MoveItem(int oldIndex, int newIndex)
    {
        base.MoveItem(oldIndex, newIndex);
        int start = Math.Min(oldIndex, newIndex);
        int end = Math.Max(oldIndex, newIndex);
        for (int i = start; i <= end; i++)
            Items[i].SlotIndex = i;
    }

    protected override void SetItem(int index, ClipboardCardViewModel item)
    {
        var previous = Items[index];
        ById.Remove(previous.Entry.Id);
        item.SlotIndex = index;
        ById[item.Entry.Id] = item;
        base.SetItem(index, item);
    }
}
