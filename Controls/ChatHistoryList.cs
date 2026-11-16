using System.Windows;
using System.Windows.Controls;
using VNotch.Services.Spotlight;

namespace VNotch.Controls;

/// <summary>Creates card visuals only for realized rows; containers are recycled while scrolling.</summary>
public sealed class ChatHistoryList : ListBox
{
    internal Func<ChatSummary, FrameworkElement>? CreateCard { get; set; }

    public ChatHistoryList()
    {
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        BorderThickness = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ListBoxItem container && item is ChatSummary chat)
        {
            container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            container.Padding = new Thickness(0);
            container.Content = CreateCard?.Invoke(chat);
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem container) container.Content = null;
        base.ClearContainerForItemOverride(element, item);
    }

    internal Control? NextCardStop(DependencyObject? current, bool reverse)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row ||
                current == null || !row.IsAncestorOf(current)) continue;
            if (row.Content is not DependencyObject content) continue;
            var buttons = Buttons(content).Where(b => b.IsEnabled && b.Focusable).ToArray();
            int buttonIndex = Array.FindIndex(buttons, b => b == current || b.IsAncestorOf(current));
            int next = buttonIndex + (reverse ? -1 : 1);
            if (next >= 0 && next < buttons.Length) return buttons[next];
            return CardStop(i + (reverse ? -1 : 1), reverse);
        }
        return null;
    }

    internal Control? CardStop(int index, bool last)
    {
        if (index < 0 || index >= Items.Count) return null;
        ScrollIntoView(Items[index]);
        UpdateLayout();
        if (ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) return null;
        if (row.Content is not DependencyObject content) return null;
        var buttons = Buttons(content).Where(b => b.IsEnabled && b.Focusable);
        return last ? buttons.LastOrDefault() : buttons.FirstOrDefault();
    }

    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Button button) yield return button;
            else if (child is DependencyObject nested)
                foreach (var descendant in Buttons(nested)) yield return descendant;
        }
    }
}
