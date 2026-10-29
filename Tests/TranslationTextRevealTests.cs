using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationTextRevealTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharactersAppearInOrderWithIndividualFadesWithoutChangingLayout(bool markdown) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ConfigureFormatting(markdown);
            window.ShowClipboard("hello");
            await window.WaitForEntranceAsync(ct);
            string text = string.Join("\n", Enumerable.Repeat("Tiếng Việt: a\u0301, 👩‍💻 — Bản dịch hiển thị đầy đủ khi sao chép.", 5));
            if (markdown) text = "**Formatted translation**\n\n- " + text;
            window.ShowResult(new(text, "en", "vi", true));
            FrameworkElement target = markdown ? window.MarkdownResult : window.ResultText;
            if (markdown)
            {
                Assert.Equal(FontWeights.Bold, window.MarkdownResult.Document.FontWeight);
                Assert.All(window.MarkdownResult.Document.Blocks.Cast<System.Windows.Documents.Block>(),
                    block => Assert.Equal(FontWeights.Bold, block.FontWeight));
            }
            Assert.Equal(text, window.ResultText.Text);
            Assert.Null(target.Clip);
            if (!SystemParameters.ClientAreaAnimation) { Assert.Equal(1, target.Opacity); return; }
            Assert.Equal(0, target.Opacity);
            await WpfFrameWaiter.UntilAsync(() => HasCharacterFades(window),
                "individual characters fade while later characters stay hidden", ct);
            Assert.Null(target.Clip);
            var size = target.RenderSize;
            int partialPixels = CountVisiblePixels(window);
            Assert.True(partialPixels > 0);
            Assert.Equal(text, window.ResultText.Text);
            Assert.True(window.CopyButton.IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(target.Opacity - 1) < 1e-6 && target.OpacityMask == null, "character reveal completes", ct);
            Assert.Equal(size, target.RenderSize);
            Assert.Equal(text, window.ResultText.Text);
            Assert.True(CountVisiblePixels(window) > partialPixels);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("language", false)]
    [InlineData("language", true)]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    [InlineData("dismiss", false)]
    [InlineData("dismiss", true)]
    [InlineData("reduced", false)]
    [InlineData("reduced", true)]
    [InlineData("scroll", false)]
    [InlineData("scroll", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void InterruptingRevealCannotMaskNewContent(string action, bool markdown) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("hello");
            await window.WaitForEntranceAsync(ct);
            window.ConfigureFormatting(markdown);
            string text = string.Join("\n", Enumerable.Repeat("A long translated paragraph that wraps onto several lines.", 15));
            if (markdown) text = "**Formatted translation**\n\n- " + text;
            window.ShowResult(new(text, "en", "vi", true));
            FrameworkElement target = markdown ? window.MarkdownResult : window.ResultText;
            if (SystemParameters.ClientAreaAnimation)
                await WpfFrameWaiter.UntilAsync(() => HasCharacterFades(window), "character fade starts", ct);
            switch (action)
            {
                case "language": window.TargetLanguageCombo.SelectedValue = "fr"; Assert.Empty(window.ResultText.Text); break;
                case "selection": window.ShowClipboard("new source"); Assert.Empty(window.ResultText.Text); break;
                case "dismiss": window.Dismiss(); break;
                case "reduced": AnimationConfig.SetReduceMotion(true); break;
                case "scroll":
                    window.ResultScroll.ScrollToVerticalOffset(100);
                    await WpfFrameWaiter.UntilAsync(() => Math.Abs(target.Opacity - 1) < 1e-6 && target.OpacityMask == null, "scroll reveals complete text", ct);
                    break;
                case "replace": window.ShowResult(new("Intermediate result", "en", "fr", true)); break;
            }
            Assert.Null(window.ResultText.Clip);
            if (action != "replace")
            {
                Assert.Null(target.OpacityMask);
                Assert.Equal(1, target.Opacity);
            }
            window.ConfigureFormatting(false);
            window.ShowResult(new("Newest result", "en", "fr", true));
            if (action == "dismiss") return;
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "Newest result" && Math.Abs(window.ResultText.Opacity - 1) < 1e-6 && window.ResultText.OpacityMask == null, "new character reveal completes", ct);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static bool HasCharacterFades(TranslationWindow window)
    {
        FrameworkElement target = window.MarkdownResult.Visibility == Visibility.Visible ? window.MarkdownResult : window.ResultText;
        if (target.OpacityMask is not DrawingBrush { Drawing: DrawingGroup drawing }) return false;
        var opacities = drawing.Children.OfType<GeometryDrawing>().Skip(1)
            .Select(d => d.Brush?.Opacity ?? 0).ToArray();
        return opacities.Any(o => Math.Abs(o - 1) < 1e-6) && opacities.Any(o => o is > 0 and < 1) && opacities.Any(o => Math.Abs(o) < 1e-6);
    }

    private static int CountVisiblePixels(TranslationWindow window)
    {
        FrameworkElement target = window.MarkdownResult.Visibility == Visibility.Visible ? window.MarkdownResult : window.ResultText;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int count = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 32) count++;
        return count;
    }
}
