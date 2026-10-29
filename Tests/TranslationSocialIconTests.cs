using System.Windows.Documents;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationSocialIconTests
{
    private const string Asset = "https://static.xx.fbcdn.net/images/emoji.php/v9/t40/1/16/1f62d.png";

    [Theory]
    [InlineData("😭")]
    [InlineData("👩🏽‍💻")]
    [InlineData("🇻🇳")]
    [InlineData("1️⃣")]
    [InlineData("❤️")]
    [InlineData("↗")]
    [InlineData("⌚")]
    [InlineData("⭐")]
    [InlineData("▶")]
    public async Task SocialIconsSurviveTranslationAndCannotBeSilentlyDropped(string icon)
    {
        Assert.True(TranslationStructuredText.IsEmoji(icon));
        string output = "Xin chào " + icon;
        Assert.Equal(output, await TranslationStructuredText.TranslateAsync("Hello " + icon, (_, _) => Task.FromResult(output), default));
        await Assert.ThrowsAsync<TranslationException>(() => TranslationStructuredText.TranslateAsync("Hello " + icon, (_, _) => Task.FromResult("Xin chào"), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!")]
    public async Task FacebookEmojiLinksAndImagesCanBecomeUnicode(string imageMarker)
    {
        string source = $"Hello {imageMarker}[😭]({Asset})";
        const string output = "Xin chào 😭";
        Assert.Equal(output, await TranslationStructuredText.TranslateAsync(source, (_, _) => Task.FromResult(output), default));
        Assert.True(TranslationTextIntegrity.Verify(source, output));
        Assert.Equal(output, TranslationTextIntegrity.Protect(source, protectNumbers: false).Restore(output, allowUnverifiedNumbers: true));
    }

    [Fact]
    public void OnlyEmojiAssetLinksAreNormalized()
    {
        Assert.Equal("😭 😭", TranslationStructuredText.NormalizeSocialIcons($"[😭]({Asset}) ![😭]({Asset})"));
        foreach (string source in new[] { $"[Open]({Asset})", "[😭](https://example.com/form)", $"`[😭]({Asset})`" })
            Assert.Equal(source, TranslationStructuredText.NormalizeSocialIcons(source));
    }

    [Theory]
    [InlineData("✨🎮")]
    [InlineData("👩🏽‍💻 🇻🇳 ❤️ 1️⃣")]
    public async Task EmojiOnlySelectionsNeedNoModel(string text)
    {
        using var service = new LocalTranslationService(new UnavailableEngine());
        var result = await service.TranslateAsync(text, "auto", "vi", default);
        Assert.Equal(text, result.Text);
    }

    private sealed class UnavailableEngine : ILocalTranslationEngine
    {
        public Task<string> FitInputAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not load model");
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not infer");
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }

    [Fact]
    public void EmojiAssetsRenderWithoutExposingImageUrls() => SharedStaTestRunner.Run(() =>
    {
        var doc = VNotch.Controls.AiMarkdown.Render($"Hello [😭]({Asset}) ![😭]({Asset}) [Form](https://example.com)");
        string text = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
        Assert.Contains("😭", text);
        Assert.DoesNotContain("fbcdn", text);
        Assert.Contains("https://example.com", text);
    });
}
