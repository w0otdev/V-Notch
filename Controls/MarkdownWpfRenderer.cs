using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VNotch.Services;
using VNotch.Services.Translation;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace VNotch.Controls;

internal enum MarkdownLinkMode
{
    SafeHyperlink = 0,
    LabelWithUrlSuffix = 1,
}

internal sealed class MarkdownFlowOptions
{
    private static readonly FontFamily DefaultBodyFont = new(AppFonts.BaseUri, "./#SF Pro Display, Segoe UI Emoji, Segoe UI, Arial");
    private static readonly FontFamily DefaultCodeFont = new("Consolas, SF Mono, Menlo, monospace");
    private static readonly SolidColorBrush DefaultBodyForeground = Freeze(Color.FromRgb(200, 200, 200));
    private static readonly SolidColorBrush DefaultCodeForeground = Freeze(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush DefaultCodeBackground = Freeze(Color.FromArgb(200, 20, 20, 26));
    private static readonly SolidColorBrush DefaultCodeBorder = Freeze(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush DefaultTableCellBorder = Freeze(Color.FromArgb(50, 255, 255, 255));
    private static readonly SolidColorBrush DefaultInlineCodeForeground = Freeze(Color.FromRgb(225, 225, 225));
    private static readonly SolidColorBrush DefaultInlineCodeBackground = Freeze(Color.FromArgb(36, 255, 255, 255));
    private static readonly SolidColorBrush DefaultQuoteBorder = Freeze(Color.FromRgb(85, 85, 85));
    private static readonly SolidColorBrush DefaultQuoteForeground = Freeze(Color.FromArgb(215, 255, 255, 255));
    private static readonly SolidColorBrush DefaultLinkForeground = Freeze(Color.FromRgb(96, 165, 250));

    public static MarkdownFlowOptions Default { get; } = new();

    public FontFamily BodyFont { get; init; } = DefaultBodyFont;
    public FontFamily CodeFont { get; init; } = DefaultCodeFont;
    public double BodyFontSize { get; init; } = 13.5;
    public double BodyLineHeight { get; init; } = 22;
    public FontWeight BodyFontWeight { get; init; } = FontWeights.Normal;
    public Brush BodyForeground { get; init; } = DefaultBodyForeground;
    public Brush CodeForeground { get; init; } = DefaultCodeForeground;
    public Brush CodeBackground { get; init; } = DefaultCodeBackground;
    public Brush CodeBorder { get; init; } = DefaultCodeBorder;
    public Brush TableCellBorder { get; init; } = DefaultTableCellBorder;
    public Brush InlineCodeForeground { get; init; } = DefaultInlineCodeForeground;
    public Brush InlineCodeBackground { get; init; } = DefaultInlineCodeBackground;
    public Brush QuoteBorder { get; init; } = DefaultQuoteBorder;
    public Brush QuoteForeground { get; init; } = DefaultQuoteForeground;
    public Brush LinkForeground { get; init; } = DefaultLinkForeground;
    public MarkdownLinkMode LinkMode { get; init; } = MarkdownLinkMode.SafeHyperlink;
    public bool HttpsOnlyLinks { get; init; }
    public bool SoftBreakAsSpace { get; init; }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal sealed class MarkdownPanelOptions
{
    private static readonly FontFamily DefaultCodeFont = new("Consolas, SF Mono, Menlo, monospace");
    private static readonly SolidColorBrush DefaultBodyBrush = Freeze(Color.FromRgb(204, 204, 204));
    private static readonly SolidColorBrush DefaultHeadingBrush = Freeze(UiPalette.PrimaryColor);
    private static readonly SolidColorBrush DefaultBulletBrush = Freeze(Color.FromRgb(0, 102, 255));
    private static readonly SolidColorBrush DefaultMutedBrush = Freeze(Color.FromRgb(136, 136, 136));
    private static readonly SolidColorBrush DefaultCheckedBrush = Freeze(Color.FromRgb(0, 204, 102));
    private static readonly SolidColorBrush DefaultQuoteBorderBrush = Freeze(Color.FromRgb(64, 64, 64));
    private static readonly SolidColorBrush DefaultQuoteTextBrush = Freeze(Color.FromRgb(170, 170, 170));
    private static readonly SolidColorBrush DefaultRuleBrush = Freeze(Color.FromRgb(42, 42, 42));
    private static readonly SolidColorBrush DefaultCodeBgBrush = Freeze(Color.FromArgb(200, 20, 20, 26));
    private static readonly SolidColorBrush DefaultCodeBorderBrush = Freeze(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush DefaultCodeTextBrush = Freeze(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush DefaultInlineCodeBgBrush = Freeze(Color.FromArgb(28, 255, 255, 255));
    private static readonly SolidColorBrush DefaultInlineCodeTextBrush = Freeze(Color.FromRgb(230, 230, 230));
    private static readonly SolidColorBrush DefaultTableHeaderBgBrush = Freeze(Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush DefaultTableBorderBrush = Freeze(Color.FromArgb(40, 255, 255, 255));

    public static MarkdownPanelOptions Default { get; } = new();

    public FontFamily? BodyFont { get; init; }
    public FontFamily CodeBlockFont { get; init; } = DefaultCodeFont;
    public FontFamily InlineCodeFont { get; init; } = DefaultCodeFont;
    public double BodyFontSize { get; init; } = 13;
    public double BodyLineHeight { get; init; } = 20;
    public FontWeight BodyFontWeight { get; init; } = FontWeights.Normal;
    public Thickness ParagraphMargin { get; init; } = new(0, 4, 0, 4);
    public bool SoftBreakAsSpace { get; init; }

    public Brush BodyForeground { get; init; } = DefaultBodyBrush;
    public Brush HeadingForeground { get; init; } = DefaultHeadingBrush;
    public Func<int, (double Size, FontWeight Weight, Thickness Margin)>? ResolveHeadingStyle { get; init; }

    public Brush ListTextForeground { get; init; } = DefaultBodyBrush;
    public Brush BulletForeground { get; init; } = DefaultBulletBrush;
    public Brush OrderedMarkerForeground { get; init; } = DefaultMutedBrush;
    public Brush CheckedTaskForeground { get; init; } = DefaultCheckedBrush;
    public Brush UncheckedTaskForeground { get; init; } = DefaultMutedBrush;
    public Thickness ListItemMargin { get; init; } = new(0, 4, 0, 4);
    public double BulletColumnWidth { get; init; }
    public double MarkerColumnWidth { get; init; }

    public Brush QuoteBorderBrush { get; init; } = DefaultQuoteBorderBrush;
    public Brush QuoteForeground { get; init; } = DefaultQuoteTextBrush;
    public Thickness QuotePadding { get; init; } = new(10, 2, 0, 2);
    public Thickness QuoteMargin { get; init; } = new(0, 4, 0, 4);

    public Brush ThematicBreakBrush { get; init; } = DefaultRuleBrush;
    public Thickness ThematicBreakMargin { get; init; } = new(0, 8, 0, 8);

    public Brush CodeBlockBackground { get; init; } = DefaultCodeBgBrush;
    public Brush CodeBlockBorderBrush { get; init; } = DefaultCodeBorderBrush;
    public Brush CodeBlockForeground { get; init; } = DefaultCodeTextBrush;
    public Brush InlineCodeForeground { get; init; } = DefaultInlineCodeTextBrush;
    public Brush? InlineCodeBackground { get; init; } = DefaultInlineCodeBgBrush;

    public Brush TableHeaderForeground { get; init; } = DefaultHeadingBrush;
    public Brush TableCellForeground { get; init; } = DefaultBodyBrush;
    public Brush TableBorderBrush { get; init; } = DefaultTableBorderBrush;
    public Brush? TableHeaderBackground { get; init; } = DefaultTableHeaderBgBrush;

    public Brush? BoldForeground { get; init; }
    public Brush? ItalicForeground { get; init; }
    public Brush LinkForeground { get; init; } = DefaultBulletBrush;
    public bool UnderlineLinks { get; init; }
    public bool HttpsOnlyLinks { get; init; }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal static class MarkdownWpfRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    internal static MarkdownDocument Parse(string markdown) =>
        Markdown.Parse(markdown ?? string.Empty, Pipeline);

    internal static FlowDocument RenderFlowDocument(string markdown, MarkdownFlowOptions? options = null) =>
        RenderFlowDocument(Parse(markdown), options);

    internal static FlowDocument RenderFlowDocument(MarkdownDocument document, MarkdownFlowOptions? options = null)
    {
        var opt = options ?? MarkdownFlowOptions.Default;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = opt.BodyFontSize,
            FontWeight = opt.BodyFontWeight,
            FontFamily = opt.BodyFont,
            LineHeight = opt.BodyLineHeight,
            Foreground = opt.BodyForeground,
            Background = Brushes.Transparent
        };

        foreach (var block in document)
        {
            doc.Blocks.Add(RenderFlowBlock(block, opt));
        }

        return doc;
    }

    internal static void RenderToPanel(Panel targetPanel, string markdown, MarkdownPanelOptions? options = null)
    {
        targetPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        AppendToPanel(targetPanel, markdown, options);
    }

    internal static void AppendToPanel(Panel targetPanel, string markdown, MarkdownPanelOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var opt = options ?? MarkdownPanelOptions.Default;
        try
        {
            var document = Parse(markdown);
            foreach (var block in document)
            {
                AppendBlockToPanel(targetPanel, block, opt, 0);
            }
        }
        catch (Exception)
        {
            var fallback = CreatePanelTextBlock(opt, opt.BodyForeground, opt.ParagraphMargin);
            fallback.Text = markdown;
            targetPanel.Children.Add(fallback);
        }
    }

    internal static void PopulateInlines(
        InlineCollection target,
        string text,
        MarkdownPanelOptions? options = null,
        bool clearFirst = false)
    {
        if (clearFirst)
        {
            target.Clear();
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var opt = options ?? MarkdownPanelOptions.Default;
        int initialCount = target.Count;
        try
        {
            var doc = Parse(text);
            AppendDocumentInlines(target, doc, opt);
            if (target.Count == initialCount)
            {
                target.Add(CreatePanelRun(text, opt));
            }
        }
        catch (Exception)
        {
            if (clearFirst)
            {
                target.Clear();
            }
            target.Add(CreatePanelRun(text, opt));
        }
    }

    private static void AppendDocumentInlines(InlineCollection target, ContainerBlock container, MarkdownPanelOptions opt)
    {
        bool firstBlock = true;
        foreach (var block in container)
        {
            if (!firstBlock && target.Count > 0)
            {
                target.Add(opt.SoftBreakAsSpace ? CreatePanelRun(" ", opt) : new LineBreak());
            }

            if (block is LeafBlock leaf && leaf.Inline != null)
            {
                AddPanelInlines(target, leaf.Inline, opt, skipLeadingTaskList: false);
                firstBlock = false;
            }
            else if (block is ContainerBlock nested)
            {
                AppendDocumentInlines(target, nested, opt);
                firstBlock = false;
            }
        }
    }

    private static WpfBlock RenderFlowBlock(Markdig.Syntax.Block block, MarkdownFlowOptions opt)
    {
        if (block is CodeBlock code)
        {
            return new Paragraph(new Run(code.Lines.ToString()))
            {
                FontFamily = opt.CodeFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                Foreground = opt.CodeForeground,
                Background = opt.CodeBackground,
                BorderBrush = opt.CodeBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 6, 0, 10)
            };
        }

        if (block is ListBlock list)
        {
            var result = new System.Windows.Documents.List
            {
                FontFamily = opt.BodyFont,
                FontWeight = opt.BodyFontWeight,
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(22, 0, 0, 0)
            };
            if (list.IsOrdered &&
                ushort.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out ushort orderedNumber) &&
                orderedNumber > 0)
            {
                result.StartIndex = orderedNumber;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not ListItemBlock item) continue;
                var li = new ListItem { FontFamily = opt.BodyFont, FontWeight = opt.BodyFontWeight };
                foreach (var child in item)
                {
                    li.Blocks.Add(RenderFlowBlock(child, opt));
                }
                result.ListItems.Add(li);
            }
            return result;
        }

        if (block is MdTable table)
        {
            var result = new WpfTable
            {
                FontFamily = opt.BodyFont,
                FontWeight = opt.BodyFontWeight,
                CellSpacing = 0,
                Margin = new Thickness(0, 6, 0, 10)
            };
            var rows = new TableRowGroup();
            result.RowGroups.Add(rows);
            for (int r = 0; r < table.Count; r++)
            {
                if (table[r] is not MdTableRow row) continue;
                var rendered = new WpfTableRow();
                for (int c = 0; c < row.Count; c++)
                {
                    if (row[c] is not MdTableCell cell) continue;
                    var target = new WpfTableCell
                    {
                        FontFamily = opt.BodyFont,
                        Padding = new Thickness(6),
                        BorderBrush = opt.TableCellBorder,
                        BorderThickness = new Thickness(0.5),
                        FontWeight = row.IsHeader ? FontWeights.Bold : opt.BodyFontWeight,
                        TextAlignment = ResolveColumnAlignment(table, c)
                    };
                    foreach (var child in cell)
                    {
                        target.Blocks.Add(RenderFlowBlock(child, opt));
                    }
                    rendered.Cells.Add(target);
                }
                rows.Rows.Add(rendered);
            }
            return result;
        }

        if (block is ThematicBreakBlock)
        {
            return new Paragraph
            {
                BorderBrush = opt.CodeBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 8, 0, 8)
            };
        }

        if (block is ContainerBlock container)
        {
            var section = new Section { FontFamily = opt.BodyFont, FontWeight = opt.BodyFontWeight };
            if (block is QuoteBlock)
            {
                section.BorderBrush = opt.QuoteBorder;
                section.BorderThickness = new Thickness(3, 0, 0, 0);
                section.Padding = new Thickness(12, 4, 4, 4);
                section.Foreground = opt.QuoteForeground;
            }
            foreach (var child in container)
            {
                section.Blocks.Add(RenderFlowBlock(child, opt));
            }
            return section;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = opt.BodyFont,
            FontWeight = opt.BodyFontWeight
        };

        if (block is HeadingBlock heading)
        {
            paragraph.FontSize = Math.Max(14, 20 - (heading.Level - 1) * 2);
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Foreground = opt.BodyForeground;
            paragraph.Margin = new Thickness(0, 10, 0, 4);
        }

        if (block is LeafBlock leaf)
        {
            if (leaf.Inline != null)
            {
                AddFlowInlines(paragraph.Inlines, leaf.Inline, opt);
            }
            else if (leaf.Lines.Count > 0)
            {
                paragraph.Inlines.Add(new Run(leaf.Lines.ToString().TrimEnd('\r', '\n')));
            }
        }

        return paragraph;
    }

    private static void AddFlowInlines(InlineCollection target, ContainerInline container, MarkdownFlowOptions opt)
    {
        bool trimNextSpace = false;
        foreach (var inline in container)
        {
            if (trimNextSpace && inline is LiteralInline trimmedLiteral)
            {
                trimNextSpace = false;
                string s = trimmedLiteral.Content.ToString();
                if (s.Length > 0 && s[0] == ' ')
                {
                    s = s[1..];
                }
                if (s.Length > 0)
                {
                    target.Add(new Run(s));
                }
                continue;
            }
            trimNextSpace = false;

            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;

                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    trimNextSpace = true;
                    break;

                case AutolinkInline autolink:
                    if (!autolink.IsEmail &&
                        opt.LinkMode == MarkdownLinkMode.SafeHyperlink &&
                        TryResolveSafeUri(autolink.Url, opt.HttpsOnlyLinks, out var autoUri))
                    {
                        target.Add(CreateSafeHyperlink(new Run(autolink.Url), autoUri, opt.LinkForeground, underline: true));
                    }
                    else
                    {
                        target.Add(new Run(autolink.Url));
                    }
                    break;

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = opt.CodeFont,
                        FontSize = 12,
                        Foreground = opt.InlineCodeForeground,
                        Background = opt.InlineCodeBackground
                    });
                    break;

                case LineBreakInline lineBreak:
                    if (!lineBreak.IsHard && opt.SoftBreakAsSpace)
                    {
                        target.Add(new Run(" "));
                    }
                    else
                    {
                        target.Add(new LineBreak());
                    }
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();
                    ApplyEmphasisStyle(span, emphasis, boldForeground: null, italicForeground: null);
                    AddFlowInlines(span.Inlines, emphasis, opt);
                    target.Add(span);
                    break;

                case LinkInline link:
                    if (link.IsImage || opt.LinkMode == MarkdownLinkMode.LabelWithUrlSuffix)
                    {
                        var label = new Span { Foreground = opt.BodyForeground };
                        AddFlowInlines(label.Inlines, link, opt);
                        if (!link.IsImage &&
                            !string.IsNullOrEmpty(link.Url) &&
                            !TranslationStructuredText.IsSocialIcon(link))
                        {
                            label.Inlines.Add(new Run(" (" + link.Url + ")"));
                        }
                        target.Add(label);
                    }
                    else if (TryResolveSafeUri(link.Url, opt.HttpsOnlyLinks, out var safeUri))
                    {
                        var hyperlink = CreateSafeHyperlink(inlineChild: null, safeUri, opt.LinkForeground, underline: true);
                        AddFlowInlines(hyperlink.Inlines, link, opt);
                        if (hyperlink.Inlines.Count == 0)
                        {
                            hyperlink.Inlines.Add(new Run(safeUri.AbsoluteUri));
                        }
                        target.Add(hyperlink);
                    }
                    else
                    {
                        var plain = new Span { Foreground = opt.BodyForeground };
                        AddFlowInlines(plain.Inlines, link, opt);
                        target.Add(plain);
                    }
                    break;

                case ContainerInline nested:
                    AddFlowInlines(target, nested, opt);
                    break;
            }
        }
    }

    private static void AppendBlockToPanel(Panel targetPanel, Markdig.Syntax.Block block, MarkdownPanelOptions opt, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                {
                    var (size, weight, margin) = opt.ResolveHeadingStyle != null
                        ? opt.ResolveHeadingStyle(heading.Level)
                        : DefaultHeadingStyle(heading.Level);
                    var tb = new TextBlock
                    {
                        FontSize = size,
                        FontWeight = weight,
                        Foreground = opt.HeadingForeground,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = IndentMargin(margin, listDepth)
                    };
                    if (opt.BodyFont != null)
                    {
                        tb.FontFamily = opt.BodyFont;
                    }
                    if (heading.Inline != null)
                    {
                        AddPanelInlines(tb.Inlines, heading.Inline, opt, skipLeadingTaskList: false);
                    }
                    targetPanel.Children.Add(tb);
                    return;
                }

            case ThematicBreakBlock:
                targetPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = opt.ThematicBreakBrush,
                    Margin = IndentMargin(opt.ThematicBreakMargin, listDepth)
                });
                return;

            case CodeBlock code:
                {
                    var codeText = new TextBlock
                    {
                        Text = code.Lines.ToString().TrimEnd('\r', '\n'),
                        FontFamily = opt.CodeBlockFont,
                        FontSize = 12.5,
                        FontWeight = FontWeights.Normal,
                        Foreground = opt.CodeBlockForeground,
                        TextWrapping = TextWrapping.Wrap
                    };
                    targetPanel.Children.Add(new Border
                    {
                        Background = opt.CodeBlockBackground,
                        BorderBrush = opt.CodeBlockBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = IndentMargin(new Thickness(0, 4, 0, 10), listDepth),
                        Child = codeText
                    });
                    return;
                }

            case ListBlock list:
                AppendListToPanel(targetPanel, list, opt, listDepth);
                return;

            case QuoteBlock quote:
                {
                    UIElement childElement;
                    if (quote.Count == 1 && quote[0] is ParagraphBlock singlePara)
                    {
                        var tb = CreatePanelTextBlock(opt, opt.QuoteForeground, new Thickness(0));
                        if (singlePara.Inline != null)
                        {
                            AddPanelInlines(tb.Inlines, singlePara.Inline, opt, skipLeadingTaskList: false);
                        }
                        childElement = tb;
                    }
                    else
                    {
                        var quoteStack = new StackPanel();
                        foreach (var child in quote)
                        {
                            AppendBlockToPanel(quoteStack, child, opt, 0);
                        }
                        childElement = quoteStack;
                    }

                    targetPanel.Children.Add(new Border
                    {
                        BorderBrush = opt.QuoteBorderBrush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = opt.QuotePadding,
                        Margin = IndentMargin(opt.QuoteMargin, listDepth),
                        Child = childElement
                    });
                    return;
                }

            case MdTable table:
                {
                    var tableElement = CreatePanelTable(table, opt, listDepth);
                    if (tableElement != null)
                    {
                        targetPanel.Children.Add(tableElement);
                    }
                    return;
                }

            case ParagraphBlock paragraph:
                {
                    var tb = CreatePanelTextBlock(opt, opt.BodyForeground, IndentMargin(opt.ParagraphMargin, listDepth));
                    if (paragraph.Inline != null)
                    {
                        AddPanelInlines(tb.Inlines, paragraph.Inline, opt, skipLeadingTaskList: false);
                    }
                    if (tb.Inlines.Count > 0)
                    {
                        targetPanel.Children.Add(tb);
                    }
                    return;
                }

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlockToPanel(targetPanel, child, opt, listDepth);
                }
                return;

            case LeafBlock leaf:
                {
                    string raw = leaf.Lines.ToString().TrimEnd('\r', '\n');
                    if (string.IsNullOrWhiteSpace(raw) && leaf.Inline == null)
                    {
                        return;
                    }
                    var tb = CreatePanelTextBlock(opt, opt.BodyForeground, IndentMargin(opt.ParagraphMargin, listDepth));
                    if (leaf.Inline != null)
                    {
                        AddPanelInlines(tb.Inlines, leaf.Inline, opt, skipLeadingTaskList: false);
                    }
                    else
                    {
                        tb.Inlines.Add(CreatePanelRun(raw, opt));
                    }
                    targetPanel.Children.Add(tb);
                    return;
                }
        }
    }

    private static void AppendListToPanel(Panel targetPanel, ListBlock list, MarkdownPanelOptions opt, int depth)
    {
        int currentNumber = 1;
        if (list.IsOrdered &&
            !string.IsNullOrEmpty(list.OrderedStart) &&
            int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStart) &&
            parsedStart > 0)
        {
            currentNumber = parsedStart;
        }

        char orderedDelim = list.OrderedDelimiter != '\0' ? list.OrderedDelimiter : '.';

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not ListItemBlock item) continue;

            ParagraphBlock? firstPara = item.Count > 0 ? item[0] as ParagraphBlock : null;
            TaskList? taskInline = firstPara?.Inline?.FirstChild as TaskList;

            string markerText;
            Brush markerBrush;
            double colWidth;
            bool isBullet = false;

            if (taskInline != null)
            {
                markerText = taskInline.Checked ? "☑" : "☐";
                markerBrush = taskInline.Checked ? opt.CheckedTaskForeground : opt.UncheckedTaskForeground;
                colWidth = opt.MarkerColumnWidth;
            }
            else if (list.IsOrdered)
            {
                markerText = $"{currentNumber}{orderedDelim}";
                markerBrush = opt.OrderedMarkerForeground;
                colWidth = opt.MarkerColumnWidth;
            }
            else
            {
                markerText = "•";
                markerBrush = opt.BulletForeground;
                colWidth = opt.BulletColumnWidth;
                isBullet = true;
            }

            var rowGrid = new Grid
            {
                Margin = IndentMargin(opt.ListItemMargin, depth)
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = colWidth > 0 ? new GridLength(colWidth) : GridLength.Auto
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            var markerBlock = new TextBlock
            {
                Text = markerText,
                FontSize = isBullet && opt.BulletColumnWidth > 0 ? 14 : opt.BodyFontSize,
                FontWeight = opt.BodyFontWeight,
                Foreground = markerBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = colWidth > 0
                    ? (isBullet ? new Thickness(0, -1, 0, 0) : new Thickness(0))
                    : new Thickness(0, 0, 8, 0)
            };
            if (opt.BodyFont != null)
            {
                markerBlock.FontFamily = opt.BodyFont;
            }
            Grid.SetColumn(markerBlock, 0);
            rowGrid.Children.Add(markerBlock);

            if (firstPara != null)
            {
                var itemText = CreatePanelTextBlock(opt, opt.ListTextForeground, new Thickness(0));
                if (firstPara.Inline != null)
                {
                    AddPanelInlines(itemText.Inlines, firstPara.Inline, opt, skipLeadingTaskList: taskInline != null);
                }
                Grid.SetColumn(itemText, 1);
                rowGrid.Children.Add(itemText);
                targetPanel.Children.Add(rowGrid);

                for (int c = 1; c < item.Count; c++)
                {
                    AppendBlockToPanel(targetPanel, item[c], opt, depth + 1);
                }
            }
            else
            {
                var itemStack = new StackPanel();
                foreach (var child in item)
                {
                    AppendBlockToPanel(itemStack, child, opt, 0);
                }
                Grid.SetColumn(itemStack, 1);
                rowGrid.Children.Add(itemStack);
                targetPanel.Children.Add(rowGrid);
            }

            currentNumber++;
        }
    }

    private static UIElement? CreatePanelTable(MdTable table, MarkdownPanelOptions opt, int listDepth)
    {
        int colCount = 0;
        for (int r = 0; r < table.Count; r++)
        {
            if (table[r] is MdTableRow row && row.Count > colCount)
            {
                colCount = row.Count;
            }
        }
        if (colCount == 0)
        {
            return null;
        }

        var grid = new Grid();
        for (int c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int renderedRowIndex = 0;
        for (int r = 0; r < table.Count; r++)
        {
            if (table[r] is not MdTableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int c = 0; c < colCount; c++)
            {
                var cell = c < row.Count ? row[c] as MdTableCell : null;
                var cellText = new TextBlock
                {
                    FontSize = 12.5,
                    FontWeight = row.IsHeader ? FontWeights.Bold : opt.BodyFontWeight,
                    LineHeight = Math.Max(18, opt.BodyLineHeight - 2),
                    Foreground = row.IsHeader ? opt.TableHeaderForeground : opt.TableCellForeground,
                    TextAlignment = ResolveColumnAlignment(table, c),
                    TextWrapping = TextWrapping.Wrap
                };
                if (opt.BodyFont != null)
                {
                    cellText.FontFamily = opt.BodyFont;
                }
                if (cell != null)
                {
                    AppendDocumentInlines(cellText.Inlines, cell, opt);
                }

                var cellBorder = new Border
                {
                    Background = row.IsHeader ? opt.TableHeaderBackground : Brushes.Transparent,
                    BorderBrush = opt.TableBorderBrush,
                    BorderThickness = new Thickness(
                        0,
                        renderedRowIndex == 0 ? 0 : 1,
                        c == colCount - 1 ? 0 : 1,
                        0),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = cellText
                };
                Grid.SetRow(cellBorder, renderedRowIndex);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
            renderedRowIndex++;
        }

        if (renderedRowIndex == 0)
        {
            return null;
        }

        return new Border
        {
            BorderBrush = opt.TableBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = IndentMargin(new Thickness(0, 4, 0, 10), listDepth),
            Child = grid
        };
    }

    private static void AddPanelInlines(
        InlineCollection target,
        ContainerInline container,
        MarkdownPanelOptions opt,
        bool skipLeadingTaskList)
    {
        bool trimNextSpace = false;
        foreach (var inline in container)
        {
            if (inline is TaskList task)
            {
                if (!skipLeadingTaskList)
                {
                    target.Add(new Run(task.Checked ? "☑ " : "☐ ")
                    {
                        FontWeight = opt.BodyFontWeight,
                        Foreground = task.Checked ? opt.CheckedTaskForeground : opt.UncheckedTaskForeground
                    });
                }
                skipLeadingTaskList = false;
                trimNextSpace = true;
                continue;
            }

            if (trimNextSpace && inline is LiteralInline trimmedLiteral)
            {
                trimNextSpace = false;
                string s = trimmedLiteral.Content.ToString();
                if (s.Length > 0 && s[0] == ' ')
                {
                    s = s[1..];
                }
                if (s.Length > 0)
                {
                    target.Add(CreatePanelRun(s, opt));
                }
                continue;
            }
            trimNextSpace = false;

            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(CreatePanelRun(literal.Content.ToString(), opt));
                    break;

                case HtmlEntityInline entity:
                    target.Add(CreatePanelRun(entity.Transcoded.ToString(), opt));
                    break;

                case HtmlInline html:
                    target.Add(CreatePanelRun(html.Tag, opt));
                    break;

                case AutolinkInline autolink:
                    if (!autolink.IsEmail && TryResolveSafeUri(autolink.Url, opt.HttpsOnlyLinks, out var autoUri))
                    {
                        target.Add(CreateSafeHyperlink(
                            CreatePanelRun(autolink.Url, opt),
                            autoUri,
                            opt.LinkForeground,
                            opt.UnderlineLinks));
                    }
                    else
                    {
                        target.Add(CreatePanelRun(autolink.Url, opt));
                    }
                    break;

                case CodeInline code:
                    {
                        var run = new Run(code.Content)
                        {
                            FontFamily = opt.InlineCodeFont,
                            FontWeight = opt.BodyFontWeight,
                            Foreground = opt.InlineCodeForeground
                        };
                        if (opt.InlineCodeBackground != null)
                        {
                            run.Background = opt.InlineCodeBackground;
                        }
                        target.Add(run);
                        break;
                    }

                case LineBreakInline lineBreak:
                    if (!lineBreak.IsHard && opt.SoftBreakAsSpace)
                    {
                        target.Add(CreatePanelRun(" ", opt));
                    }
                    else
                    {
                        target.Add(new LineBreak());
                    }
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();
                    ApplyEmphasisStyle(span, emphasis, opt.BoldForeground, opt.ItalicForeground);
                    AddPanelInlines(span.Inlines, emphasis, opt, skipLeadingTaskList: false);
                    target.Add(span);
                    break;

                case LinkInline link:
                    if (!link.IsImage && TryResolveSafeUri(link.Url, opt.HttpsOnlyLinks, out var safeUri))
                    {
                        var hyperlink = CreateSafeHyperlink(inlineChild: null, safeUri, opt.LinkForeground, opt.UnderlineLinks);
                        AddPanelInlines(hyperlink.Inlines, link, opt, skipLeadingTaskList: false);
                        if (hyperlink.Inlines.Count == 0)
                        {
                            hyperlink.Inlines.Add(CreatePanelRun(safeUri.AbsoluteUri, opt));
                        }
                        target.Add(hyperlink);
                    }
                    else
                    {
                        var plain = new Span();
                        AddPanelInlines(plain.Inlines, link, opt, skipLeadingTaskList: false);
                        target.Add(plain);
                    }
                    break;

                case ContainerInline nested:
                    AddPanelInlines(target, nested, opt, skipLeadingTaskList: false);
                    break;
            }
        }
    }

    private static void ApplyEmphasisStyle(
        Span span,
        EmphasisInline emphasis,
        Brush? boldForeground,
        Brush? italicForeground)
    {
        if (emphasis.DelimiterChar == '~')
        {
            span.TextDecorations = TextDecorations.Strikethrough;
            return;
        }

        if (emphasis.DelimiterCount >= 2)
        {
            span.FontWeight = FontWeights.Bold;
            if (boldForeground != null)
            {
                span.Foreground = boldForeground;
            }
        }

        if (emphasis.DelimiterCount is 1 or 3)
        {
            span.FontStyle = FontStyles.Italic;
            if (emphasis.DelimiterCount == 1 && italicForeground != null)
            {
                span.Foreground = italicForeground;
            }
        }
    }

    private static TextBlock CreatePanelTextBlock(MarkdownPanelOptions opt, Brush foreground, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize = opt.BodyFontSize,
            FontWeight = opt.BodyFontWeight,
            LineHeight = opt.BodyLineHeight,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };
        if (opt.BodyFont != null)
        {
            tb.FontFamily = opt.BodyFont;
        }
        return tb;
    }

    private static Run CreatePanelRun(string text, MarkdownPanelOptions opt)
    {
        return opt.BodyFontWeight != FontWeights.Normal
            ? new Run(text) { FontWeight = opt.BodyFontWeight }
            : new Run(text);
    }

    private static Thickness IndentMargin(Thickness baseMargin, int depth) =>
        depth <= 0
            ? baseMargin
            : new Thickness(baseMargin.Left + depth * 18, baseMargin.Top, baseMargin.Right, baseMargin.Bottom);

    private static (double Size, FontWeight Weight, Thickness Margin) DefaultHeadingStyle(int level) =>
        level switch
        {
            1 => (20.0, FontWeights.Bold, new Thickness(0, 12, 0, 8)),
            2 => (18.0, FontWeights.Bold, new Thickness(0, 12, 0, 8)),
            3 => (16.0, FontWeights.SemiBold, new Thickness(0, 12, 0, 8)),
            _ => (14.5, FontWeights.SemiBold, new Thickness(0, 12, 0, 8))
        };

    private static TextAlignment ResolveColumnAlignment(MdTable table, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[columnIndex].Alignment switch
        {
            TableColumnAlign.Center => TextAlignment.Center,
            TableColumnAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static bool TryResolveSafeUri(string? rawUrl, bool httpsOnly, out Uri safeUri)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl) &&
            SafeLauncher.IsSafeUrl(rawUrl, out var parsed) &&
            parsed != null &&
            (!httpsOnly || parsed.Scheme == Uri.UriSchemeHttps))
        {
            safeUri = parsed;
            return true;
        }

        safeUri = null!;
        return false;
    }

    private static Hyperlink CreateSafeHyperlink(
        System.Windows.Documents.Inline? inlineChild,
        Uri safeUri,
        Brush foreground,
        bool underline)
    {
        var link = inlineChild != null ? new Hyperlink(inlineChild) : new Hyperlink();
        link.NavigateUri = safeUri;
        link.Foreground = foreground;
        link.ToolTip = safeUri.AbsoluteUri;
        if (!underline)
        {
            link.TextDecorations = null;
        }
        link.RequestNavigate += OnSafeHyperlinkNavigate;
        return link;
    }

    private static void OnSafeHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri != null)
        {
            SafeLauncher.TryOpenUrl(e.Uri);
        }
        e.Handled = true;
    }
}
