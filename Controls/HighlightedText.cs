using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>
/// Attached behavior that renders a TextBlock with every query token
/// emphasized in bright bold text (used by Spotlight results).
/// </summary>
public static class HighlightedText
{
    private static readonly Brush MatchBrush = Brushes.White;

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HighlightedText),
        new PropertyMetadata(string.Empty, OnHighlightChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(HighlightedText),
        new PropertyMetadata(string.Empty, OnHighlightChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject obj) => (string)obj.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject obj, string value) => obj.SetValue(QueryProperty, value);

    private static void OnHighlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        string text = GetText(textBlock) ?? string.Empty;
        string query = GetQuery(textBlock) ?? string.Empty;

        textBlock.Inlines.Clear();
        if (text.Length == 0) return;

        bool[] mask = BuildMatchMask(text, query);
        int index = 0;
        while (index < text.Length)
        {
            int start = index;
            bool highlighted = mask[index];
            while (index < text.Length && mask[index] == highlighted) index++;

            var run = new Run(text[start..index]);
            if (highlighted)
            {
                run.Foreground = MatchBrush;
                run.FontWeight = FontWeights.Bold;
            }
            textBlock.Inlines.Add(run);
        }
    }

    internal static bool[] BuildMatchMask(string text, string query)
    {
        var mask = new bool[text.Length];
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int searchFrom = 0;
            int matchAt;
            while (searchFrom < text.Length
                   && (matchAt = text.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                for (int i = matchAt; i < matchAt + token.Length; i++) mask[i] = true;
                searchFrom = matchAt + 1;
            }
        }
        return mask;
    }
}
