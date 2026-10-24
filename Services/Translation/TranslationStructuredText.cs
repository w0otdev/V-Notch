using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VNotch.Services.Translation;

internal static partial class TranslationStructuredText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseTaskLists().UsePreciseSourceLocation().Build();
    private static readonly Regex Shortcode = new(@":[A-Za-z_][A-Za-z0-9_+-]*:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static bool HasMarkdown(string text) => Markdown.Parse(text, Pipeline).Descendants().Any(node =>
        node is not (ParagraphBlock or LiteralInline or ContainerInline or LineBreakInline) ||
        node is EmphasisInline or LinkInline);

    internal static bool IsEmoji(string element) => ContainsEmoji(element.AsSpan());

    private static unsafe bool ContainsEmoji(ReadOnlySpan<char> element)
    {
        if (element.IsEmpty) return false;
        fixed (char* ptr = element)
        {
            vn_translation_structured_contains_emoji(ptr, element.Length, out byte contains);
            return contains != 0;
        }
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
        string translated = await translate(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!sourceSignature.SequenceEqual(Signature(translated)))
            throw new TranslationException("translation.literalsChanged");
        return translated;
    }

    private static string[]? SourceSignature(string text)
    {
        var document = Markdown.Parse(text, Pipeline);
        bool hasProse = document.Descendants<LiteralInline>().Any(l =>
            l.Parent is not LinkInline { IsAutoLink: true } &&
            Shortcode.Replace(l.Content.ToString(), "").Any(char.IsLetter));
        return hasProse ? Signature(text, document) : null;
    }

    private static string[] Signature(string text, MarkdownDocument? document = null)
    {
        string normalized = NormalizeSocialIcons(text, document);
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
                EmphasisInline emphasis => "emphasis:" + emphasis.DelimiterCount,
                HeadingBlock heading => "heading:" + heading.Level,
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

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_structured_contains_emoji(
        char* elem_ptr, int elem_len, out byte out_contains);
}
