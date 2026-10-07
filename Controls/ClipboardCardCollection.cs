using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using VNotch.ViewModels;

namespace VNotch.Controls;

// Retain the full lightweight list for selection and scrolling, but notify
// WPF once when a large search result replaces it.
internal sealed class ClipboardCardCollection : ObservableCollection<ClipboardCardViewModel>
{
    internal void ReplaceAll(IReadOnlyList<ClipboardCardViewModel> cards)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var card in cards) Items.Add(card);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
