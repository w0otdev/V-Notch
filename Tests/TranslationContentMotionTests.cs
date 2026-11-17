using System.Windows;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationContentMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputAndStatusMorphWithoutReflowingGlyphs(bool markdown) => Run(async (window, ct) =>
    {
        window.SetStatus("translation.working");
        await Settled(window, ct);
        double before = window.ActualHeight;
        double footerBefore = FooterY(window);
        string result = string.Join("\n", Enumerable.Repeat("Một câu dịch được hiển thị đầy đủ.", 7));
        if (markdown) result = "**Kết quả**\n\n" + result;
        window.ShowResult(new(result, "en", "vi", true));
        Assert.Equal(before, window.ActualHeight, 1);
        await WpfFrameWaiter.UntilAsync(() => window.ResultHeightHost.IsMorphing &&
            window.ResultHeightHost.PresentedHeight > 40 &&
            window.ResultHeightHost.PresentedHeight < window.ResultHeightHost.TargetHeight - 5 &&
            (markdown ? window.MarkdownResult.OpacityMask : window.ResultText.OpacityMask) is DrawingBrush { Drawing: DrawingGroup drawing } &&
            drawing.Children.OfType<GeometryDrawing>().Skip(1).Any(item => item.Brush.Opacity > .1),
            "characters reveal while the result height is still morphing", ct);
        FrameworkElement text = markdown ? window.MarkdownResult : window.ResultText;
        var textSize = text.RenderSize;
        double middle = window.ActualHeight;
        double footerMiddle = FooterY(window);
        TranslationTextRevealTests.Capture(window.PopupSurface, markdown ? "morph-markdown-middle" : "morph-plain-middle");
        await Settled(window, ct);
        Assert.InRange(middle, before + 1, window.ActualHeight - 1);
        Assert.InRange(footerMiddle, footerBefore + 1, FooterY(window) - 1);
        Assert.Equal(textSize, text.RenderSize);
        Assert.Equal(0, window.StatusHeightHost.ActualHeight, 1);
        TranslationTextRevealTests.Capture(window.PopupSurface, markdown ? "morph-markdown-end" : "morph-plain-end");
        double tall = window.ActualHeight;
        window.ShowResult(new("Ngắn hơn.", "en", "vi", true));
        Assert.Equal(tall, window.ActualHeight, 1);
        await WpfFrameWaiter.UntilAsync(() => window.ResultHeightHost.IsMorphing &&
            window.ResultHeightHost.PresentedHeight > window.ResultHeightHost.TargetHeight + 5 && window.ActualHeight < tall - 5,
            "short answer shrinks smoothly", ct);
        await Settled(window, ct);
        Assert.True(window.ActualHeight < tall - 40);
    });

    [Fact]
    public void AppendingDuringAFadePreservesItsBrushAndProgress() => Run(async (window, ct) =>
    {
        const string first = "Bản dịch đang hiển thị từng ký tự";
        window.ShowPreview(first);
        await WpfFrameWaiter.UntilAsync(() => Brushes(window).Any(brush => brush.Opacity is > .05 and < .6),
            "a character is partway through its fade", ct);
        var active = Brushes(window).First(brush => brush.Opacity is > .05 and < .6);
        double opacity = active.Opacity;
        double height = window.ActualHeight;
        window.ShowPreview(first + "\nDòng tiếp theo xuất hiện trong khi dòng đầu vẫn đang hiện.");
        Assert.Equal(opacity, active.Opacity, 5);
        Assert.Equal(height, window.ActualHeight, 1);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Contains(active, Brushes(window));
        Assert.True(active.Opacity >= opacity);
        Assert.Equal(FontWeights.Bold, window.ResultText.FontWeight);
        string final = window.ResultText.Text;
        window.ShowResult(new(final, "en", "vi", true));
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.OpacityMask == null && window.ResultText.Opacity == 1,
            "final handoff finishes the existing reveal", ct);
        await Settled(window, ct);
    });

    [Fact]
    public void StreamingPastTheLongTextThresholdKeepsTheSameTypography() => Run(async (window, ct) =>
    {
        const string prefix = "Dòng đầu giữ nguyên kích thước khi có thêm nội dung.";
        window.ShowPreview(prefix);
        await Settled(window, ct);
        double fontSize = window.ResultText.FontSize;
        var firstGlyph = window.ResultText.GetRectFromCharacterIndex(0);
        string text = prefix + "\n" + string.Join("\n", Enumerable.Repeat("Nội dung tiếp theo rất dài đang được dịch.", 15));
        window.ShowPreview(text);
        await Settled(window, ct);
        Assert.True(text.Length > 400);
        Assert.Equal(fontSize, window.ResultText.FontSize);
        Assert.Equal(firstGlyph, window.ResultText.GetRectFromCharacterIndex(0));
        window.ShowResult(new(text, "en", "vi", true));
        await Settled(window, ct);
        Assert.Equal(fontSize, window.ResultText.FontSize);
        Assert.Equal(firstGlyph, window.ResultText.GetRectFromCharacterIndex(0));
    });

    [Fact]
    public void RetargetingAndReducedMotionDoNotLeaveAStaleHeight() => Run(async (window, ct) =>
    {
        window.ShowPreview(string.Join("\n", Enumerable.Repeat("Nội dung đang được dịch.", 6)));
        await WpfFrameWaiter.UntilAsync(() => window.ResultHeightHost.IsMorphing && window.ResultHeightHost.PresentedHeight > 45,
            "first growth is in flight", ct);
        double before = window.ResultHeightHost.PresentedHeight;
        window.ShowPreview(window.ResultText.Text + "\nThêm một dòng nữa.");
        window.UpdateLayout();
        Assert.InRange(window.ResultHeightHost.PresentedHeight, before - 1, before + 1);
        AnimationConfig.SetReduceMotion(true);
        await Settled(window, ct);
        Assert.Equal(window.ResultHeightHost.TargetHeight, window.ResultHeightHost.ActualHeight, 1);
        Assert.Null(window.ResultText.OpacityMask);
        window.ShowResult(new("Hoàn tất.", "en", "vi", true));
        await Settled(window, ct);
        Assert.False(window.ResultHeightHost.IsMorphing);
        Assert.Equal(window.ResultHeightHost.TargetHeight, window.ResultHeightHost.ActualHeight, 1);
    });

    private static double FooterY(TranslationWindow window) =>
        window.TranslationActions.TranslatePoint(default, window.PopupSurface).Y;

    private static IEnumerable<SolidColorBrush> Brushes(TranslationWindow window) =>
        window.ResultText.OpacityMask is DrawingBrush { Drawing: DrawingGroup drawing }
            ? drawing.Children.OfType<GeometryDrawing>().Skip(1).Select(item => item.Brush).OfType<SolidColorBrush>()
            : Enumerable.Empty<SolidColorBrush>();

    private static async Task Settled(TranslationWindow window, CancellationToken ct)
    {
        await WpfFrameWaiter.NextAsync(ct);
        await WpfFrameWaiter.UntilAsync(() => !window.ResultHeightHost.IsMorphing && !window.StatusHeightHost.IsMorphing,
            "content height settles", ct);
        await WpfFrameWaiter.NextAsync(ct);
    }

    private static void Run(Func<TranslationWindow, CancellationToken, Task> test) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? clientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "source", new Rect(100, 100, 80, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            await test(window, ct);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previous);
            AnimationConfig.ClientAreaAnimationOverride = clientAnimation;
        }
    });
}
