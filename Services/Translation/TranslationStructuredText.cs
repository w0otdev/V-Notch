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

    internal static bool IsEmoji(string element) => ContainsEmoji(element.AsSpan());

    private static bool ContainsEmoji(ReadOnlySpan<char> element)
    {
        foreach (var rune in element.EnumerateRunes())
            if (rune.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
                >= 0x2190 and <= 0x21FF or >= 0x2300 and <= 0x23FF or
                >= 0x2B00 and <= 0x2BFF or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or
                0xFE0F or 0x20E3 or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139)
                return true;
        return false;
    }

    internal static bool IsOnlyIcons(string text) => ContainsOnlyIcons(text, allowWhitespace: true);

    private static bool ContainsOnlyIcons(string text, bool allowWhitespace)
    {
        bool any = false;
        for (int index = 0; index < text.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(text, index);
            var element = text.AsSpan(index, length);
            index += length;
            if (allowWhitespace && element.IsWhiteSpace()) continue;
            if (!ContainsEmoji(element)) return false;
            any = true;
        }
        return any;
    }

    // Social clipboard exports often wrap a Unicode emoji in a link to its
    // image asset. The asset URL is presentation, not a destination in the post.
    internal static string NormalizeSocialIcons(string text) => NormalizeSocialIcons(text, null);

    private static string NormalizeSocialIcons(string text, MarkdownDocument? document)
    {
        if (!text.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase)) return text;
        var edits = (document ?? Markdown.Parse(text, Pipeline)).Descendants<LinkInline>()
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
        return ContainsOnlyIcons(label, allowWhitespace: false);
    }

    internal static async Task<string> TranslateAsync(string text, Func<string, CancellationToken, Task<string>> translate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourceSignature = SourceSignature(text);
        if (sourceSignature == null) return text;
        // Keep sentences together so the model can disambiguate verbs and reorder words.
        string translated = await translate(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!sourceSignature.SequenceEqual(Signature(translated)))
            throw new TranslationException("translation.literalsChanged");
        return translated;
    }

    private static string[]? SourceSignature(string text)
    {
        // Keep the syntax tree inside this synchronous scope instead of retaining
        // it during model inference. Only the protected-content signature survives.
        var document = Markdown.Parse(text, Pipeline);
        bool hasProse = document.Descendants<LiteralInline>().Any(l =>
            l.Parent is not LinkInline { IsAutoLink: true } &&
            Shortcode.Replace(l.Content.ToString(), "").Any(char.IsLetter));
        return hasProse ? Signature(text, document) : null;
    }

    private static string[] Signature(string text, MarkdownDocument? document = null)
    {
        string normalized = NormalizeSocialIcons(text, document);
        // Social-icon replacement changes source offsets and can affect Markdown
        // parsing, so only reuse the original tree when its text is unchanged.
        if (normalized != text) document = null;
        text = normalized;
        var parts = new List<string>();
        foreach (var node in (document ?? Markdown.Parse(text, Pipeline)).Descendants())
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
        string decoded = System.Net.WebUtility.HtmlDecode(text);
        for (int index = 0; index < decoded.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(decoded, index);
            var element = decoded.AsSpan(index, length);
            if (ContainsEmoji(element)) parts.Add(string.Concat("emoji:".AsSpan(), element));
            index += length;
        }
        foreach (Match match in Shortcode.Matches(text))
            parts.Add("shortcode:" + match.Value);
        parts.Sort(StringComparer.Ordinal);
        return parts.ToArray();
    }
}
