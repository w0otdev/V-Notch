using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VNotch;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Windows;
using Xunit;
using WpfTable = System.Windows.Documents.Table;

namespace VNotch.Tests;

public class MarkdownNativeParserTests
{
    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null)
        {
            throw new AggregateException("STA test failed", captured);
        }
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinks(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink link)
            {
                yield return link;
            }
            if (inline is Span span)
            {
                foreach (var nested in EnumerateHyperlinks(span.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<Hyperlink> EnumerateFlowHyperlinks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    foreach (var link in EnumerateHyperlinks(p.Inlines))
                        yield return link;
                    break;
                case Section s:
                    foreach (var link in EnumerateFlowHyperlinks(s.Blocks))
                        yield return link;
                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var link in EnumerateFlowHyperlinks(item.Blocks))
                            yield return link;
                    }
                    break;
                case WpfTable table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var link in EnumerateFlowHyperlinks(cell.Blocks))
                                    yield return link;
                            }
                        }
                    }
                    break;
            }
        }
    }

    [Fact]
    public void MarkdownWpfRenderer_RendersHeadingsNestedListsTablesFencedCodeAndSafeLinks()
    {
        const string markdown =
            "# Heading One\n" +
            "## Heading Two\n" +
            "- Parent bullet\n" +
            "  - Nested child bullet\n" +
            "- [ ] Pending task\n" +
            "- [x] Completed task\n" +
            "1. First ordered\n" +
            "2) Second ordered\n\n" +
            "| Left | Center | Right |\n" +
            "| :--- | :---: | ---: |\n" +
            "| **Bold** | *Italic* | `Code` |\n\n" +
            "```csharp\n" +
            "int answer = 42;\n" +
            "```\n\n" +
            "> Quoted ~~strike~~ and <tag> literal\n\n" +
            "Links: [Safe](https://github.com/w0otdev) and [BlockedJs](javascript:alert(1)) and [BlockedFile](file:///C:/Windows/win.ini)";

        RunOnSta(() =>
        {
            var panel = new StackPanel();
            MarkdownWpfRenderer.RenderToPanel(panel, markdown);
            Assert.True(panel.Children.Count >= 10);

            var nestedBulletGrid = panel.Children.OfType<Grid>().FirstOrDefault(g => g.Margin.Left > 0);
            Assert.NotNull(nestedBulletGrid);

            var lastParagraph = panel.Children.OfType<TextBlock>().Last();
            var panelLinks = EnumerateHyperlinks(lastParagraph.Inlines).ToList();
            Assert.Single(panelLinks);
            Assert.Equal("https://github.com/w0otdev", panelLinks[0].NavigateUri.OriginalString);

            var flowDoc = MarkdownWpfRenderer.RenderFlowDocument(markdown);
            Assert.Contains(flowDoc.Blocks, b => b is WpfTable);
            Assert.Contains(flowDoc.Blocks, b => b is System.Windows.Documents.List);
            Assert.Contains(flowDoc.Blocks, b => b is Section);

            string flowText = new TextRange(flowDoc.ContentStart, flowDoc.ContentEnd).Text;
            Assert.Contains("int answer = 42;", flowText);
            Assert.Contains("<tag>", flowText);

            var flowLinks = EnumerateFlowHyperlinks(flowDoc.Blocks).ToList();
            Assert.Single(flowLinks);
            Assert.Equal("https://github.com/w0otdev", flowLinks[0].NavigateUri.OriginalString);
        });
    }

    [Fact]
    public void RenderMarkdown_TermsOfServiceAndChangelog_RendersToWpfWithoutThrowing()
    {
        const string doc =
            "# Title\n" +
            "## Subtitle\n" +
            "### Section\n" +
            "#### Subsection\n" +
            "---\n" +
            "| Col1 | Col2 |\n" +
            "|---|---|\n" +
            "| **Bold** | `Code` |\n" +
            "> Quoted *italic* and https://github.com/w0otdev\n" +
            "- [ ] Unchecked\n" +
            "- [x] Checked\n" +
            "- Bullet [SafeLink](https://github.com)\n" +
            "1. Numbered one\n" +
            "2) Numbered two\n\n" +
            "Normal paragraph text.";

        RunOnSta(() =>
        {
            var termsPanel = new StackPanel();
            TermsOfServicePage.RenderMarkdownToPanel(termsPanel, doc);
            Assert.True(termsPanel.Children.Count >= 12);

            var changelogPanel = new StackPanel();
            ChangelogWindow.RenderMarkdownContent(changelogPanel, doc);
            Assert.True(changelogPanel.Children.Count >= 12);

            var httpCheckBlock = new TextBlock();
            TermsOfServicePage.AddFormattedInlines(httpCheckBlock, "[Insecure](http://example.com) and [Secure](https://example.com)");
            var termsLinks = EnumerateHyperlinks(httpCheckBlock.Inlines).ToList();
            Assert.Single(termsLinks);
            Assert.Equal(Uri.UriSchemeHttps, termsLinks[0].NavigateUri.Scheme);

            string adversarial = new string('*', 50_000) + " " + new string('[', 50_000);
            var sw = Stopwatch.StartNew();
            var tb = new TextBlock();
            TermsOfServicePage.AddFormattedInlines(tb, adversarial);
            Assert.NotEmpty(tb.Inlines);

            ChangelogWindow.PopulateInlineMarkdown(tb, adversarial);
            sw.Stop();
            Assert.NotEmpty(tb.Inlines);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected fast linear parse, took {sw.ElapsedMilliseconds}ms");
        });
    }

    [Fact]
    public void AiMarkdown_CloseSpansAndStreamRenderer_ClosesFenceInlineCodeBoldAndItalic()
    {
        Assert.Equal("```csharp\nvar x = a * b;\n```", AiMarkdown.CloseSpans("```csharp\nvar x = a * b;"));
        Assert.Equal("~~~sql\nSELECT * FROM items;\n~~~", AiMarkdown.CloseSpans("~~~sql\nSELECT * FROM items;"));
        Assert.Equal("Run `dotnet build`", AiMarkdown.CloseSpans("Run `dotnet build"));
        Assert.Equal("Use ``code `with tick``", AiMarkdown.CloseSpans("Use ``code `with tick"));
        Assert.Equal("Hello **bold**", AiMarkdown.CloseSpans("Hello **bold"));
        Assert.Equal("Hello **bold** ", AiMarkdown.CloseSpans("Hello **bold "));
        Assert.Equal("Hello __bold__", AiMarkdown.CloseSpans("Hello __bold"));
        Assert.Equal("Hello *italic*", AiMarkdown.CloseSpans("Hello *italic"));
        Assert.Equal("Hello _italic_ ", AiMarkdown.CloseSpans("Hello _italic "));
        Assert.Equal("Hello **bold *italic*** ", AiMarkdown.CloseSpans("Hello **bold *italic "));

        Assert.Equal("* Bullet item", AiMarkdown.CloseSpans("* Bullet item"));
        Assert.Equal("snake_case_name", AiMarkdown.CloseSpans("snake_case_name"));
        Assert.Equal("Visit https://example.com/_docs", AiMarkdown.CloseSpans("Visit https://example.com/_docs"));
        Assert.Equal("Compute 2 * 3", AiMarkdown.CloseSpans("Compute 2 * 3"));

        RunOnSta(() =>
        {
            const string fullStream =
                "First **bold** and *italic* with `inline` code.\n\n" +
                "```python\n" +
                "print('streamed')\n" +
                "```\n\n" +
                "Final _paragraph_ done.";

            var streamer = new AiMarkdown.StreamRenderer();
            int steps = 0;
            while (!streamer.Advance(fullStream, words: 2, fadeMilliseconds: 120))
            {
                steps++;
                Assert.True(steps < 64);
            }

            string renderedText = new TextRange(streamer.Document.ContentStart, streamer.Document.ContentEnd).Text;
            Assert.Contains("First bold and italic with inline code.", renderedText);
            Assert.Contains("print('streamed')", renderedText);
            Assert.Contains("Final paragraph done.", renderedText);
        });
    }

    [Fact]
    public void NativeMarkdownExports_ScanWordSpansAndBoundarySummaries_HandleEdgeCasesAccurately()
    {
        const string sample = "  Hello\u00A0world\nThird  ";
        Span<AiMarkdown.VnMarkdownWordSlice> tinyBuf = stackalloc AiMarkdown.VnMarkdownWordSlice[1];
        int rcSmall = AiMarkdown.ScanWordSpans(sample, tinyBuf, out int needed);
        Assert.Equal(VNotchNative.VN_ERR_BUFFER_TOO_SMALL, rcSmall);
        Assert.Equal(3, needed);

        Span<AiMarkdown.VnMarkdownWordSlice> fullBuf = stackalloc AiMarkdown.VnMarkdownWordSlice[4];
        int rcOk = AiMarkdown.ScanWordSpans(sample, fullBuf, out int written);
        Assert.Equal(VNotchNative.VN_OK, rcOk);
        Assert.Equal(3, written);
        Assert.Equal("Hello\u00A0", sample.Substring(fullBuf[0].Start, fullBuf[0].Length));
        Assert.Equal("world\n", sample.Substring(fullBuf[1].Start, fullBuf[1].Length));
        Assert.Equal("Third  ", sample.Substring(fullBuf[2].Start, fullBuf[2].Length));

        var fenceSummary = AiMarkdown.ScanSpanBoundaries("````ts\nconst a = 1;");
        Assert.Equal(1, fenceSummary.HasUnclosedFence);
        Assert.Equal((byte)'`', fenceSummary.FenceChar);
        Assert.Equal(4, fenceSummary.FenceCount);
        Assert.Equal(0, fenceSummary.HasUnclosedInlineCode);

        var closedFenceWithStars = AiMarkdown.ScanSpanBoundaries("```c\na * b;\n```\nPlain text");
        Assert.Equal(0, closedFenceWithStars.HasUnclosedFence);
        Assert.Equal(0, closedFenceWithStars.HasUnclosedItalic);

        var multiTickCode = AiMarkdown.ScanSpanBoundaries("Prefix ``unclosed `span");
        Assert.Equal(1, multiTickCode.HasUnclosedInlineCode);
        Assert.Equal(2, multiTickCode.InlineCodeTicks);
        Assert.Equal(0, multiTickCode.HasUnclosedBold);

        var escapedAndBlankReset = AiMarkdown.ScanSpanBoundaries("Paragraph *unclosed\n\nEscaped \\*not italic");
        Assert.Equal(0, escapedAndBlankReset.HasUnclosedItalic);
        Assert.Equal(0, escapedAndBlankReset.HasUnclosedBold);
    }
}
