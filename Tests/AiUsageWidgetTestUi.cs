using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace VNotch.Tests;

internal static class AiUsageWidgetTestUi
{
    internal static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants(root).OfType<T>(), element => element.Name == name);

    internal static string Text(DependencyObject root, string name) => Find<TextBlock>(root, name).Text;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
