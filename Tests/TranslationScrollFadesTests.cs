using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationScrollFadesTests
{
    [Fact]
    public void FadesFollowScrollBoundariesAndEmptyStatusDoesNotReserveSpace() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("Original text");
            await window.WaitForEntranceAsync(ct);
            string text = string.Join("\n", Enumerable.Repeat("Dịch vụ thương mại được cung cấp bởi các đối tác trao đổi của Apple. Giá trị trao đổi phụ thuộc vào tình trạng và kiểu thiết bị.", 12));
            window.ShowResult(new(text, "en", "vi", true));
            await WpfFrameWaiter.UntilAsync(() => window.ResultScroll.ScrollableHeight > 1 && window.ResultBottomFade.Opacity > .99 && window.TranslationStatusRow.Visibility == Visibility.Collapsed, "top boundary settles", ct);
            Assert.Equal(0, window.ResultTopFade.Opacity);
            Assert.False(window.ResultTopFade.IsHitTestVisible);
            Assert.False(window.ResultBottomFade.IsHitTestVisible);
            Point content = window.ResultViewport.TransformToAncestor(window.PopupSurface).Transform(new Point());
            Point actions = window.TranslationActions.TransformToAncestor(window.PopupSurface).Transform(new Point());
            Assert.InRange(actions.Y - content.Y - window.ResultViewport.ActualHeight, 0, 14);
            Capture(window, "top");

            window.ResultScroll.ScrollToVerticalOffset(window.ResultScroll.ScrollableHeight / 2);
            if (SystemParameters.ClientAreaAnimation)
                await WpfFrameWaiter.UntilAsync(() => window.ResultTopFade.Opacity is > .01 and < .95, "top fade animates in", ct);
            await WpfFrameWaiter.UntilAsync(() => window.ResultTopFade.Opacity > .99 && window.ResultBottomFade.Opacity > .99, "middle has both fades", ct);
            Capture(window, "middle");
            window.ResultScroll.ScrollToEnd();
            await WpfFrameWaiter.UntilAsync(() => window.ResultTopFade.Opacity > .99 && window.ResultBottomFade.Opacity < .01, "bottom boundary clears lower fade", ct);
            Capture(window, "bottom");
            window.ResultScroll.ScrollToTop();
            await WpfFrameWaiter.UntilAsync(() => window.ResultTopFade.Opacity < .01 && window.ResultBottomFade.Opacity > .99, "top boundary clears upper fade", ct);

            AnimationConfig.SetReduceMotion(true);
            window.ShowResult(new("Bản dịch ngắn.", "en", "vi", true));
            await WpfFrameWaiter.UntilAsync(() => window.ResultScroll.ScrollableHeight < .5 && window.ResultTopFade.Opacity == 0 && window.ResultBottomFade.Opacity == 0, "short text has no fades", ct);
            window.SetStatus("translation.working");
            await WpfFrameWaiter.UntilAsync(() => window.TranslationStatusRow.Visibility == Visibility.Visible, "working status restores its row", ct);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static void Capture(TranslationWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("TRANSLATION_SCROLL_CAPTURE_DIR") is not string directory) return;
        Directory.CreateDirectory(directory);
        var surface = window.PopupSurface;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
