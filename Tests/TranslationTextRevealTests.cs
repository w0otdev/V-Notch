using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationTextRevealTests
{
    [Fact]
    public void CharactersAppearInOrderWithIndividualFadesWithoutChangingLayout() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ConfigureFormatting(false);
            window.ShowClipboard("hello");
            await window.WaitForEntranceAsync(ct);
            string text = string.Join("\n", Enumerable.Repeat("Tiếng Việt: a\u0301, 👩‍💻 — Bản dịch hiển thị đầy đủ khi sao chép.", 5));
            window.ShowResult(new(text, "en", "vi", true));
            Assert.Equal(text, window.ResultText.Text);
            Assert.Null(window.ResultText.Clip);
            if (!SystemParameters.ClientAreaAnimation) { Assert.Equal(1, window.ResultText.Opacity); return; }
            Assert.Equal(0, window.ResultText.Opacity);
            await WpfFrameWaiter.UntilAsync(() => HasCharacterFades(window),
                "individual characters fade while later characters stay hidden", ct);
            Assert.Null(window.ResultText.Clip);
            var size = window.ResultText.RenderSize;
            int partialPixels = CountVisiblePixels(window);
            Assert.True(partialPixels > 0);
            Assert.Equal(text, window.ResultText.Text);
            Assert.True(window.CopyButton.IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null, "character reveal completes", ct);
            Assert.Equal(size, window.ResultText.RenderSize);
            Assert.Equal(text, window.ResultText.Text);
            Assert.True(CountVisiblePixels(window) > partialPixels);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("language")]
    [InlineData("selection")]
    [InlineData("dismiss")]
    [InlineData("reduced")]
    [InlineData("scroll")]
    [InlineData("replace")]
    public void InterruptingRevealCannotMaskNewContent(string action) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("hello");
            await window.WaitForEntranceAsync(ct);
            string text = string.Join("\n", Enumerable.Repeat("A long translated paragraph that wraps onto several lines.", 15));
            window.ShowResult(new(text, "en", "vi", true));
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
                    await WpfFrameWaiter.UntilAsync(() => window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null, "scroll reveals complete text", ct);
                    break;
                case "replace": window.ShowResult(new("Intermediate result", "en", "fr", true)); break;
            }
            Assert.Null(window.ResultText.Clip);
            if (action != "replace") Assert.Null(window.ResultText.OpacityMask);
            window.ConfigureFormatting(false);
            window.ShowResult(new("Newest result", "en", "fr", true));
            if (action == "dismiss") return;
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "Newest result" && window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null, "new character reveal completes", ct);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static bool HasCharacterFades(TranslationWindow window)
    {
        if (window.ResultText.OpacityMask is not DrawingBrush { Drawing: DrawingGroup drawing }) return false;
        var opacities = drawing.Children.OfType<GeometryDrawing>().Skip(1)
            .Select(d => d.Brush?.Opacity ?? 0).ToArray();
        return opacities.Any(o => o == 1) && opacities.Any(o => o is > 0 and < 1) && opacities.Any(o => o == 0);
    }

    private static int CountVisiblePixels(TranslationWindow window)
    {
        var target = window.ResultText;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int count = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 32) count++;
        return count;
    }
}
