using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationLoadingMotionTests
{
    [Fact]
    public void SolvingOrbAnimatesWithoutLayoutAndStopsOnResultHideAndReducedMotion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? previousClientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.ClientAreaAnimationOverride = true;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("Hello, a translation is on its way.");
            await window.WaitForEntranceAsync(ct);
            window.SetStatus("translation.working");
            var indicator = window.LoadingIndicator;
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating && indicator.ActualWidth > 0, "translation loading begins", ct);
            Assert.Equal(ThinkingOrbState.Solving, indicator.State);
            Assert.Equal(Loc.Get("translation.working"), window.StatusText.Text);
            Assert.Equal(Loc.Get("translation.working"), System.Windows.Automation.AutomationProperties.GetName(indicator));
            var size = indicator.RenderSize;
            byte[] initial = Pixels(indicator);
            await WpfFrameWaiter.UntilAsync(() => !Pixels(indicator).SequenceEqual(initial), "orb dots rotate in the rendered frame", ct);
            Assert.Equal(size, indicator.RenderSize);
            byte[] initialText = Pixels(window.StatusText);
            var textSize = window.StatusText.RenderSize;
            await WpfFrameWaiter.UntilAsync(() => !Pixels(window.StatusText).SequenceEqual(initialText),
                "shimmer changes the rendered label without moving it", ct);
            Assert.Equal(textSize, window.StatusText.RenderSize);
            TranslationTextRevealTests.Capture(window.PopupSurface, "translating");
            if (Environment.GetEnvironmentVariable("TRANSLATION_ORB_CAPTURE") == "1")
            {
                for (int frame = 0; frame < 75; frame++)
                {
                    TranslationTextRevealTests.Capture(window.PopupSurface, $"orb-{frame:000}");
                    await Task.Delay(40, ct);
                }
            }

            // Compare the actual text at both ends of a loop. A bright padded
            // endpoint used to turn the entire label white, then flash on reset.
            var shimmer = Assert.IsType<LinearGradientBrush>(window.StatusText.OpacityMask);
            var sweep = Assert.IsType<TranslateTransform>(shimmer.RelativeTransform);
            sweep.BeginAnimation(TranslateTransform.XProperty, null);
            sweep.X = -1;
            await WpfFrameWaiter.NextAsync(ct);
            byte[] loopStart = Pixels(window.StatusText);
            Assert.Contains(loopStart, value => value > 0);
            sweep.X = 1;
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(loopStart, Pixels(window.StatusText));

            AnimationConfig.SetReduceMotion(true);
            Assert.False(indicator.IsAnimating);
            Assert.Null(window.StatusText.OpacityMask);
            await WpfFrameWaiter.NextAsync(ct);
            byte[] paused = Pixels(indicator);
            await Task.Delay(100, ct);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(paused, Pixels(indicator));
            window.ShowResult(new("Xin chào", "en", "vi", true) { IsCached = true });
            Assert.Equal(1, window.ResultText.Opacity);
            Assert.Null(window.ResultText.OpacityMask);
            AnimationConfig.SetReduceMotion(false);
            Assert.False(indicator.IsAnimating);

            window.SetStatus("translation.working");
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating, "next translation resumes", ct);
            window.Hide();
            Assert.False(indicator.IsAnimating);
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating, "show resumes active work", ct);
            window.ShowResult(new("Bản dịch hoàn tất", "en", "vi", true));
            Assert.False(indicator.IsAnimating);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);

            window.SetStatus("translation.working");
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating, "loading restarts", ct);
            window.Dismiss();
            Assert.False(indicator.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "dismiss finishes", ct);
            Assert.False(indicator.IsAnimating);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previous);
            AnimationConfig.ClientAreaAnimationOverride = previousClientAnimation;
        }
    });

    internal static byte[] Pixels(FrameworkElement surface)
    {
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(surface), null, new Rect(surface.RenderSize));
        bitmap.Render(visual);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
