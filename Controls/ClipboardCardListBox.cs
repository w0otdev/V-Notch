using System.Collections;
using System.Windows.Controls;

namespace VNotch.Controls;

public sealed class ClipboardCardListBox : ListBox
{
    // Selector batches changes into one SelectionChanged notification.
    internal void ApplySelection(IEnumerable cards) => SetSelectedItems(cards);
}
