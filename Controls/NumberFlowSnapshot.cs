using System.Windows;
using System.Windows.Media;

namespace VNotch.Controls;

// Quota views rebuild their rows on each monitor update. Carry displayed values across that rebuild.
internal sealed class NumberFlowSnapshot
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    internal void Capture(FrameworkElement root)
    {
        _values.Clear();
        if (root.IsLoaded && root.IsVisible)
            Visit(root, "", (key, text) => _values[key] = text.Text);
    }

    internal void Restore(FrameworkElement root, bool animate = true)
    {
        if (animate) Visit(root, "", (key, text) =>
        {
            if (_values.TryGetValue(key, out string? previous)) text.AnimateFrom(previous);
        });
        _values.Clear();
    }

    private static void Visit(DependencyObject root, string path, Action<string, NumberFlowText> action)
    {
        if (root is NumberFlowText text) { action(path, text); return; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            string key = child is FrameworkElement { Name.Length: > 0 } element ? element.Name : i.ToString();
            Visit(child, path + "/" + key, action);
        }
    }
}
