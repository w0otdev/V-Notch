using System.Windows;
using System.Windows.Documents;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationStructuredTextTests
{
    [Theory]
    [InlineData("😭")]
    [InlineData("👩🏽‍💻")]
    [InlineData("🇻🇳")]
    [InlineData("❤️")]
    [InlineData("1️⃣")]
    [InlineData(":sob:")]
    public async Task TrailingEmojiCannotBeDroppedByTheModel(string emoji)
    {
        using var engine = new Engine(_ => "This is really painful, sir. " + emoji);
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync("Quá là đau đớn rồi đấy sếp ơi " + emoji, "vi", "en", default);
        Assert.Equal("This is really painful, sir. " + emoji, result.Text);
        Assert.Single(engine.Inputs);
        Assert.Contains(emoji, engine.Inputs[0]);
    }

    [Theory]
    [InlineData("😭👩🏽‍💻🇻🇳❤️1️⃣")]
    [InlineData("```csharp\nvar x = 42; // giữ nguyên 😭\n```")]
    [InlineData("`price = 80` 😭")]
    public async Task EmojiAndCodeOnlyNeedNoInference(string text)
    {
        using var engine = new Engine(_ => throw new InvalidOperationException());
        using var service = new LocalTranslationService(engine);
        Assert.Equal(text, (await service.TranslateAsync(text, "auto", "en", default)).Text);
        Assert.Empty(engine.Inputs);
    }

    [Theory]
    [InlineData("# Hello\n\n**Hello** and *Hello* ~~Hello~~", "# Xin chào\n\n**Xin chào** và *Xin chào* ~~Xin chào~~")]
    [InlineData("> Hello\n>\n> - Hello\n> - [x] Hello", "> Xin chào\n>\n> - Xin chào\n> - [x] Xin chào")]
    [InlineData("[Hello](https://example.com/a?q=42) ![Hello](image.png)", "[Xin chào](https://example.com/a?q=42) ![Xin chào](image.png)")]
    [InlineData("| Hello | Hello |\n| --- | --- |\n| Hello | `code` |", "| Xin chào | Xin chào |\n| --- | --- |\n| Xin chào | `code` |")]
    [InlineData("1. Hello\n2. Hello\n\n```js\nconst text = 'Hello';\n```", "1. Xin chào\n2. Xin chào\n\n```js\nconst text = 'Hello';\n```")]
    [InlineData("Hello 😭 Hello 👩🏽‍💻", "Xin chào 😭 Xin chào 👩🏽‍💻")]
    [InlineData("[Hello][id]\n\n[id]: https://example.com", "[Xin chào][id]\n\n[id]: https://example.com")]
    [InlineData("Hello  \r\nHello", "Xin chào  \r\nXin chào")]
    public async Task PreservesMarkdownAndTranslatesOnlyProse(string source, string expected)
    {
        using var engine = new Engine(s => { Assert.Equal(source, s); return expected; });
        using var service = new LocalTranslationService(engine);
        Assert.Equal(expected, (await service.TranslateAsync(source, "en", "vi", default)).Text);
        Assert.Single(engine.Inputs);
    }

    [Fact]
    public async Task CancellationDoesNotReturnOrCacheMarkdown()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new Engine(s => { cancellation.Cancel(); return s; });
        using var service = new LocalTranslationService(engine);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync("**Hello** *world*", "en", "vi", cancellation.Token));
        Assert.Single(engine.Inputs);
    }

    [Fact]
    public void MarkdownEntitiesAndAutolinksRemainVisible() => SharedStaTestRunner.Run(() =>
    {
        var document = VNotch.Controls.AiMarkdown.Render("**Hello** &amp; <https://example.com>", normalBody: true);
        string rendered = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("&", rendered);
        Assert.Contains("https://example.com", rendered);
    });

    [Fact]
    public void PopupRendersMarkdownAndRetainsExactCopySource() => SharedStaTestRunner.Run(() =>
    {
        const string markdown = "# Title\n\n**Bold** *italic* ~~old~~ 😭\n\n- item\n\n`code`";
        var window = new TranslationWindow("auto", "en");
        try
        {
            window.ShowResult(new(markdown, "auto", "en", true));
            Assert.Equal(markdown, window.ResultText.Text);
            Assert.Equal(Visibility.Visible, window.MarkdownResult.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ResultText.Visibility);
            string rendered = new TextRange(window.MarkdownResult.Document.ContentStart, window.MarkdownResult.Document.ContentEnd).Text;
            Assert.Contains("Bold", rendered);
            Assert.Contains("😭", rendered);
            Assert.DoesNotContain("**", rendered);
            window.ConfigureFormatting(false);
            Assert.Equal(Visibility.Collapsed, window.MarkdownResult.Visibility);
            Assert.Equal(markdown, window.ResultText.Text);
            window.ConfigureFormatting(true);
            window.SetStatus("translation.literalsChanged");
            Assert.Empty(window.MarkdownResult.Document.Blocks);
            Assert.Equal(Visibility.Collapsed, window.MarkdownResult.Visibility);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("A **red** car.", "Một chiếc xe **đỏ**.")]
    [InlineData("I **book** a hotel room.", "Tôi **đặt** phòng khách sạn.")]
    [InlineData("Hello\nworld", "Xin chào\nthế giới")]
    public async Task WholePassageAllowsNaturalWordOrder(string input, string output)
    {
        using var engine = new Engine(s => { Assert.Equal(input, s); return output; });
        using var service = new LocalTranslationService(engine);
        Assert.Equal(output, (await service.TranslateAsync(input, "en", "vi", default)).Text);
        Assert.Single(engine.Inputs);
    }

    [Theory]
    [InlineData("*hello*", "_xin chào_")]
    [InlineData("**hello**", "__xin chào__")]
    public async Task EquivalentCommonMarkEmphasisDelimitersAreAccepted(string input, string output)
    {
        Assert.Equal(output, await TranslationStructuredText.TranslateAsync(input,
            (_, _) => Task.FromResult(output), default));
    }

    [Theory]
    [InlineData("Hello 😭", "Xin chào")]
    [InlineData("Hello `code`", "Xin chào `changed`")]
    [InlineData("[Hello](https://example.com)", "[Xin chào](https://evil.test)")]
    public async Task RejectsChangedProtectedContent(string input, string output)
    {
        await Assert.ThrowsAsync<TranslationException>(() => TranslationStructuredText.TranslateAsync(input,
            (_, _) => Task.FromResult(output), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConvertedPriceCanReplaceOnlyEditableSelection(bool editable) => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("en", "vi");
        try
        {
            typeof(TranslationWindow).GetField("_selection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, new TranslationSelection(1, "USD 10", new Rect(0, 0, 10, 10), IntPtr.Zero, editable));
            BackgroundTestWindows.ProtectInput(window);
            window.Opacity = 0;
            window.ShowActivated = false;
            window.ShowResult(new("250000 VND", "en", "vi", false)
            { Currency = new(new(10, "USD"), "VND", 250000, null, false) });
            Assert.Equal(editable, window.ReplaceButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    private sealed class Engine(Func<string, string> translate) : ILocalTranslationEngine
    {
        internal List<string> Inputs = new();
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Inputs.Add(text); return Task.FromResult(translate(text)); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
