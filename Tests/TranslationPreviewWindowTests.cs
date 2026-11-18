using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationPreviewWindowTests
{
    [Theory]
    [InlineData("translation.cancelled")]
    [InlineData("translation.timedOut")]
    [InlineData("translation.failed")]
    [InlineData("translation.meaningChanged")]
    [InlineData("translation.literalsChanged")]
    public void PreviewCannotBeCopiedOrReplacedAndIsClearedOnFailure(string status) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("source");
            await window.WaitForEntranceAsync(ct);
            window.SetStatus("translation.working");
            window.ShowPreview("Bản dịch đang tạo");
            Assert.Equal("Bản dịch đang tạo", window.ResultText.Text);
            Assert.False(window.CopyButton.IsEnabled);
            Assert.False(window.ReplaceButton.IsEnabled);
            Assert.False(window.CanReplaceResult);
            window.SetStatus(status);
            Assert.Empty(window.ResultText.Text);
            Assert.False(window.CopyButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(false, true)]
    public void CachedFastAndPreviewedResultsRevealWithoutDelayingCopy(bool cached, bool instant = false) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? previousClientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("source");
            await window.WaitForEntranceAsync(ct);
            window.SetStatus("translation.working");
            if (!cached && !instant) window.ShowPreview("Bản dịch");
            window.ShowResult(new("Bản dịch hoàn tất", "en", "vi", true) { IsCached = cached, InstantPresentation = instant });
            if (!cached && !instant)
            {
                await WpfFrameWaiter.UntilAsync(() => window.ResultText.OpacityMask is DrawingBrush,
                    "coalesced preview and final text begin revealing after layout", ct);
            }
            else Assert.Equal(0, window.ResultText.Opacity);
            Assert.True(window.CopyButton.IsEnabled);
            window.ShowPreview("Late callback");
            Assert.Equal("Bản dịch hoàn tất", window.ResultText.Text);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null,
                "result reveal completes", ct);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); AnimationConfig.ClientAreaAnimationOverride = previousClientAnimation; }
    });

    [Fact]
    public void RapidPreviewAppendsNeverDimAlreadyVisibleWords() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? previousClientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("source");
            await window.WaitForEntranceAsync(ct);
            const string prefix = "Bản dịch";
            window.ShowPreview(prefix);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null,
                "initial words finish revealing", ct);
            long baseline = PrefixBrightness(window, prefix.Length);
            Assert.True(baseline > 0);
            string text = prefix;
            foreach (string suffix in new[] { " đang", " xuất hiện", " từng phần", " rất mượt." })
            {
                text += suffix;
                window.ShowPreview(text);
                Assert.Equal(1, window.ResultText.Opacity);
                for (int frame = 0; frame < 3; frame++)
                {
                    await WpfFrameWaiter.NextAsync(ct);
                    Assert.True(PrefixBrightness(window, prefix.Length) >= baseline * .98,
                        "Appending text must not darken words already on screen.");
                    TranslationTextRevealTests.Capture(window.PopupSurface, $"append-{text.Length}-{frame}");
                }
            }
            window.ShowResult(new(text, "en", "vi", true));
            Assert.Equal(1, window.ResultText.Opacity);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.OpacityMask == null,
                "final handoff preserves and completes the active suffix fade", ct);
            Assert.Null(window.ResultText.OpacityMask);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); AnimationConfig.ClientAreaAnimationOverride = previousClientAnimation; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalFormattingDoesNotHideAnAlreadyDisplayedPreview(bool markdown) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? previousClientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("source");
            await window.WaitForEntranceAsync(ct);
            window.ShowPreview("Bản nháp đang hiển thị");
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Opacity == 1 && window.ResultText.OpacityMask == null,
                "preview becomes readable", ct);
            window.ShowResult(new(markdown ? "**Bản dịch** đã hoàn tất." : "Bản dịch đã hoàn tất.", "en", "vi", true));
            FrameworkElement target = markdown ? window.MarkdownResult : window.ResultText;
            Assert.Equal(Visibility.Visible, target.Visibility);
            Assert.Equal(1, target.Opacity);
            Assert.Null(target.OpacityMask);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(1, target.Opacity);
            Assert.Null(target.OpacityMask);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); AnimationConfig.ClientAreaAnimationOverride = previousClientAnimation; }
    });

    private static long PrefixBrightness(TranslationWindow window, int prefixLength)
    {
        var text = window.ResultText;
        Rect end = text.GetRectFromCharacterIndex(prefixLength - 1, trailingEdge: true);
        int width = (int)text.ActualWidth;
        byte[] pixels = TranslationLoadingMotionTests.Pixels(text);
        long brightness = 0;
        for (int y = 0; y < Math.Min((int)Math.Ceiling(end.Bottom), (int)text.ActualHeight); y++)
        for (int x = 0; x < Math.Min((int)Math.Floor(end.Left), width); x++)
        {
            int offset = (y * width + x) * 4;
            brightness += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
        }
        return brightness;
    }
}
