using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace VNotch.Controls;

public sealed class ClipboardCardListBox : ListBox
{
    internal Action<DependencyObject, object>? ClearingContainer { get; set; }

    // Selector batches changes into one SelectionChanged notification.
    internal void ApplySelection(IEnumerable cards) => SetSelectedItems(cards);

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        ClearingContainer?.Invoke(element, item);
        base.ClearContainerForItemOverride(element, item);
    }
}
