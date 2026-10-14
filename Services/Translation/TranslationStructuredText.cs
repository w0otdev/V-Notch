using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VNotch.Services.Translation;

// Translate complete passages and reject changes to protected content.
internal static class TranslationStructuredText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseTaskLists().UsePreciseSourceLocation().Build();
    private static readonly Regex Shortcode = new(@":[A-Za-z_][A-Za-z0-9_+-]*:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static bool HasMarkdown(string text) => Markdown.Parse(text, Pipeline).Descendants().Any(node =>
        node is not (ParagraphBlock or LiteralInline or ContainerInline or LineBreakInline) ||
        node is EmphasisInline or LinkInline);

    internal static bool IsEmoji(string element) => element.EnumerateRunes().Any(r =>
        r.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
        >= 0x2190 and <= 0x21FF or >= 0x2300 and <= 0x23FF or
        >= 0x2B00 and <= 0x2BFF or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or
        0xFE0F or 0x20E3 or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139);

    internal static bool IsOnlyIcons(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        bool any = false;
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (!IsEmoji(element)) return false;
            any = true;
        }
        return any;
    }

    // Social clipboard exports often wrap a Unicode emoji in a link to its
    // image asset. The asset URL is presentation, not a destination in the post.
    internal static string NormalizeSocialIcons(string text)
    {
        if (!text.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase)) return text;
        var edits = Markdown.Parse(text, Pipeline).Descendants<LinkInline>()
            .Where(link => link.Span.Start >= 0 && IsSocialIcon(link))
            .OrderByDescending(link => link.Span.Start);
        foreach (var link in edits)
        {
            string label = string.Concat(link.OfType<LiteralInline>().Select(l => l.Content.ToString()));
            text = text.Remove(link.Span.Start, link.Span.Length).Insert(link.Span.Start, label);
        }
        return text;
    }

    internal static bool IsSocialIcon(LinkInline link)
    {
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase)) ||
            !uri.AbsolutePath.StartsWith("/images/emoji.php/", StringComparison.Ordinal)) return false;
        if (link.Any(child => child is not LiteralInline)) return false;
        string label = string.Concat(link.OfType<LiteralInline>().Select(l => l.Content.ToString()));
        var elements = StringInfo.GetTextElementEnumerator(label);
        bool any = false;
        while (elements.MoveNext())
        {
            if (!IsEmoji(elements.GetTextElement())) return false;
            any = true;
        }
        return any;
    }

    internal static async Task<string> TranslateAsync(string text, Func<string, CancellationToken, Task<string>> translate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var document = Markdown.Parse(text, Pipeline);
        bool hasProse = document.Descendants<LiteralInline>().Any(l =>
            l.Parent is not LinkInline { IsAutoLink: true } &&
            Shortcode.Replace(l.Content.ToString(), "").Any(char.IsLetter));
        if (!hasProse) return text;
        // Keep sentences together so the model can disambiguate verbs and reorder words.
        string translated = await translate(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!Signature(text).SequenceEqual(Signature(translated)))
            throw new TranslationException("translation.literalsChanged");
        return translated;
    }

    private static string[] Signature(string text)
    {
        text = NormalizeSocialIcons(text);
        var parts = new List<string>();
        foreach (var node in Markdown.Parse(text, Pipeline).Descendants())
        {
            string? value = node switch
            {
                CodeInline code => "code:" + code.Content,
                CodeBlock code => "block:" + code.Lines.ToString(),
                LinkInline link => "link:" + link.IsImage + ":" + link.Url + ":" + link.Title,
                // '*' and '_' are equivalent CommonMark delimiters. Preserve the
                // rendered emphasis level, not the model's choice of source syntax.
                EmphasisInline emphasis => "emphasis:" + emphasis.DelimiterCount,
                HeadingBlock heading => "heading:" + heading.Level,
                // Clipboard exporters escape list markers and encode spaces. Models
                // may render these as real lists or literal spaces without changing facts.
                ListBlock or ListItemBlock or HtmlEntityInline => null,
                ParagraphBlock or LiteralInline or ContainerInline or LineBreakInline => null,
                _ => node.GetType().Name
            };
            if (value != null) parts.Add(value);
        }
        var elements = StringInfo.GetTextElementEnumerator(System.Net.WebUtility.HtmlDecode(text));
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (IsEmoji(element)) parts.Add("emoji:" + element);
        }
        foreach (Match match in Regex.Matches(text, @":[A-Za-z_][A-Za-z0-9_+-]*:"))
            parts.Add("shortcode:" + match.Value);
        return parts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
