using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace VNotch.Controls;

public static class HighlightedText
{
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

        if (text.Length == 0 || string.IsNullOrWhiteSpace(query))
        {
            textBlock.Text = text;
            return;
        }

        bool[]? rented = null;
        Span<bool> mask = text.Length <= 512
            ? stackalloc bool[text.Length]
            : (rented = ArrayPool<bool>.Shared.Rent(text.Length)).AsSpan(0, text.Length);
        try
        {
            if (!FillMatchMask(text, query, mask))
            {
                textBlock.Text = text;
                return;
            }

            Inline? existing = textBlock.Inlines.FirstInline;
            int index = 0;
            while (index < text.Length)
            {
                int start = index;
                bool highlighted = mask[index];
                while (index < text.Length && mask[index] == highlighted) index++;

                var run = existing as Run;
                if (run == null)
                {
                    run = new Run();
                    textBlock.Inlines.Add(run);
                }
                existing = run.NextInline;
                run.Text = text[start..index];
                if (highlighted)
                {
                    if (run.ReadLocalValue(TextElement.FontWeightProperty) is not FontWeight weight || weight != FontWeights.Bold)
                    {
                        run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                        run.FontWeight = FontWeights.Bold;
                    }
                }
                else
                {
                    run.ClearValue(TextElement.ForegroundProperty);
                    run.ClearValue(TextElement.FontWeightProperty);
                }
            }
            while (existing != null)
            {
                var next = existing.NextInline;
                textBlock.Inlines.Remove(existing);
                existing = next;
            }
        }
        finally
        {
            if (rented != null) ArrayPool<bool>.Shared.Return(rented);
        }
    }

    internal static bool[] BuildMatchMask(string text, string query)
    {
        var mask = new bool[text.Length];
        FillMatchMask(text, query, mask);
        return mask;
    }

    private static bool FillMatchMask(ReadOnlySpan<char> text, ReadOnlySpan<char> query, Span<bool> mask)
    {
        mask.Clear();
        bool matched = false;
        while (!query.IsEmpty)
        {
            int separator = query.IndexOf(' ');
            var token = (separator < 0 ? query : query[..separator]).Trim();
            query = separator < 0 ? ReadOnlySpan<char>.Empty : query[(separator + 1)..];
            if (token.IsEmpty) continue;
            int searchFrom = 0;
            int matchAt;
            while (searchFrom < text.Length
                   && (matchAt = text[searchFrom..].IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                matchAt += searchFrom;
                mask.Slice(matchAt, token.Length).Fill(true);
                matched = true;
                searchFrom = matchAt + 1;
            }
        }
        return matched;
    }
}
