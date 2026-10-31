using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using VNotch;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

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

    [Fact]
    public void TokenizeMarkdownInlines_ParsesBoldCodeLinksUrlsItalicAndPlainText()
    {
        const string input = "Hello **bold** and *italic* with `code`, [GitHub](https://github.com/w0otdev) and https://example.com/docs.";
        var tokens = VNotchNative.TokenizeMarkdownInlines(input);

        Assert.Collection(
            tokens,
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal("Hello ", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Bold, t.InlineKind);
                Assert.Equal("bold", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal(" and ", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Italic, t.InlineKind);
                Assert.Equal("italic", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal(" with ", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Code, t.InlineKind);
                Assert.Equal("code", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal(", ", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Link, t.InlineKind);
                Assert.Equal("GitHub", input.Substring(t.TextStart, t.TextLen));
                Assert.Equal("https://github.com/w0otdev", input.Substring(t.AuxStart, t.AuxLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal(" and ", input.Substring(t.TextStart, t.TextLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Url, t.InlineKind);
                Assert.Equal("https://example.com/docs", input.Substring(t.TextStart, t.TextLen));
                Assert.Equal("https://example.com/docs", input.Substring(t.AuxStart, t.AuxLen));
            },
            t =>
            {
                Assert.Equal(VnMarkdownInlineKind.Text, t.InlineKind);
                Assert.Equal(".", input.Substring(t.TextStart, t.TextLen));
            });
    }

    [Fact]
    public void TokenizeMarkdownInlines_PathologicalAdversarialInputs_CompletesInMilliseconds()
    {
        string stars = new string('*', 50_000);
        string brackets = new string('[', 50_000);
        string alternatingStars = string.Concat(Enumerable.Repeat("*a*w ", 10_000));
        string unclosedLinks = string.Concat(Enumerable.Repeat("[label](https://example.com/unclosed ", 2_000));

        var sw = Stopwatch.StartNew();
        var starTokens = VNotchNative.TokenizeMarkdownInlines(stars);
        var bracketTokens = VNotchNative.TokenizeMarkdownInlines(brackets);
        var altTokens = VNotchNative.TokenizeMarkdownInlines(alternatingStars);
        var unclosedTokens = VNotchNative.TokenizeMarkdownInlines(unclosedLinks);
        sw.Stop();

        Assert.Single(starTokens);
        Assert.Equal(VnMarkdownInlineKind.Text, starTokens[0].InlineKind);
        Assert.Equal(50_000, starTokens[0].TextLen);

        Assert.Single(bracketTokens);
        Assert.Equal(VnMarkdownInlineKind.Text, bracketTokens[0].InlineKind);
        Assert.Equal(50_000, bracketTokens[0].TextLen);

        Assert.Single(altTokens);
        Assert.Equal(4_001, unclosedTokens.Length);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected O(N) scan under 250ms, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void TokenizeMarkdownBlocks_ClassifiesAllSupportedBlockKindsAndTableCells()
    {
        const string markdown =
            "# Heading 1\n" +
            "## Heading 2\n" +
            "### Heading 3\n" +
            "#### Heading 4\n" +
            "---\n" +
            "| Feature | Status | Version |\n" +
            "| :--- | :---: | ---: |\n" +
            "| Native | Ready | 2.0 |\n" +
            "> Blockquote note\n" +
            "- [ ] Pending item\n" +
            "- [x] Completed item\n" +
            "- Bullet dash\n" +
            "* Bullet star\n" +
            "• Bullet dot\n" +
            "1. First numbered\n" +
            "2) Second numbered\n" +
            "Paragraph with hard break  \n" +
            "Final paragraph";

        var blocks = VNotchNative.TokenizeMarkdownBlocks(markdown);
        Assert.Equal(18, blocks.Length);

        Assert.Equal(VnMarkdownBlockKind.Heading, blocks[0].BlockKind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Heading 1", markdown.Substring(blocks[0].TextStart, blocks[0].TextLen));

        Assert.Equal(VnMarkdownBlockKind.Heading, blocks[1].BlockKind);
        Assert.Equal(2, blocks[1].Level);
        Assert.Equal(VnMarkdownBlockKind.Heading, blocks[2].BlockKind);
        Assert.Equal(3, blocks[2].Level);
        Assert.Equal(VnMarkdownBlockKind.Heading, blocks[3].BlockKind);
        Assert.Equal(4, blocks[3].Level);

        Assert.Equal(VnMarkdownBlockKind.HorizontalRule, blocks[4].BlockKind);
        Assert.Equal(VnMarkdownBlockKind.TableRow, blocks[5].BlockKind);
        Assert.Equal(VnMarkdownBlockKind.TableSeparator, blocks[6].BlockKind);
        Assert.Equal(VnMarkdownBlockKind.TableRow, blocks[7].BlockKind);

        var headerRow = markdown.AsSpan(blocks[5].TextStart, blocks[5].TextLen);
        var cells = VNotchNative.SplitMarkdownTableCells(headerRow);
        Assert.Equal(3, cells.Length);
        Assert.Equal("Feature", headerRow.Slice(cells[0].Start, cells[0].Len).ToString());
        Assert.Equal("Status", headerRow.Slice(cells[1].Start, cells[1].Len).ToString());
        Assert.Equal("Version", headerRow.Slice(cells[2].Start, cells[2].Len).ToString());

        Assert.Equal(VnMarkdownBlockKind.Blockquote, blocks[8].BlockKind);
        Assert.Equal("Blockquote note", markdown.Substring(blocks[8].TextStart, blocks[8].TextLen));

        Assert.Equal(VnMarkdownBlockKind.ChecklistItem, blocks[9].BlockKind);
        Assert.False(blocks[9].Checked);
        Assert.Equal("Pending item", markdown.Substring(blocks[9].TextStart, blocks[9].TextLen));

        Assert.Equal(VnMarkdownBlockKind.ChecklistItem, blocks[10].BlockKind);
        Assert.True(blocks[10].Checked);
        Assert.Equal("Completed item", markdown.Substring(blocks[10].TextStart, blocks[10].TextLen));

        Assert.Equal(VnMarkdownBlockKind.BulletItem, blocks[11].BlockKind);
        Assert.Equal(VnMarkdownBlockKind.BulletItem, blocks[12].BlockKind);
        Assert.Equal(VnMarkdownBlockKind.BulletItem, blocks[13].BlockKind);

        Assert.Equal(VnMarkdownBlockKind.NumberedItem, blocks[14].BlockKind);
        Assert.Equal(1, blocks[14].Level);
        Assert.Equal("1.", markdown.Substring(blocks[14].MarkerStart, blocks[14].MarkerLen));
        Assert.Equal("First numbered", markdown.Substring(blocks[14].TextStart, blocks[14].TextLen));

        Assert.Equal(VnMarkdownBlockKind.NumberedItem, blocks[15].BlockKind);
        Assert.Equal(2, blocks[15].Level);
        Assert.Equal("2)", markdown.Substring(blocks[15].MarkerStart, blocks[15].MarkerLen));
        Assert.Equal("Second numbered", markdown.Substring(blocks[15].TextStart, blocks[15].TextLen));

        Assert.Equal(VnMarkdownBlockKind.Paragraph, blocks[16].BlockKind);
        Assert.True(blocks[16].HasHardBreak);
        Assert.Equal(VnMarkdownBlockKind.Paragraph, blocks[17].BlockKind);
        Assert.False(blocks[17].HasHardBreak);
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

            string adversarial = new string('*', 50_000) + " " + new string('[', 50_000);
            var tb = new TextBlock();
            TermsOfServicePage.AddFormattedInlines(tb, adversarial);
            Assert.NotEmpty(tb.Inlines);

            ChangelogWindow.PopulateInlineMarkdown(tb, adversarial);
            Assert.NotEmpty(tb.Inlines);
        });
    }
}
