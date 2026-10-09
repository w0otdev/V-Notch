using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationMenuTests
{
    [Fact]
    public void ReversingAnExitKeepsTheRenderedPositionAndTheOldExitCannotHideThePopup() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var bounds = new Rect(200, 200, 100, 20);
            window.ShowSelection(new(1, "first", bounds, IntPtr.Zero, false), true);
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "entrance settles", ct);
            window.Dismiss();
            if (SystemParameters.ClientAreaAnimation)
                await WpfFrameWaiter.UntilAsync(() => window.PopupSurface.Opacity is > .2 and < .8, "exit reaches its middle", ct);
            double opacity = window.PopupSurface.Opacity, x = window.PopupOffset.X;
            window.ShowSelection(new(2, "second", bounds, IntPtr.Zero, false), true);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(opacity, window.PopupSurface.Opacity, 3);
                Assert.Equal(x, window.PopupOffset.X, 3);
            }
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "reversed entrance settles", ct);
            Assert.True(window.IsVisible);
            Assert.Equal("second", window.OriginalText.Text);
            Assert.Equal(1, window.PopupSurface.Opacity, 3);
            Assert.Equal(0, window.PopupOffset.X, 3);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void NewSelectionAtTheSamePositionStillAnimatesAndAnImmediateResultKeepsTheEntrance() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var bounds = new Rect(200, 200, 100, 20);
            window.ShowSelection(new(1, "first", bounds, IntPtr.Zero, false), true);
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating && window.PopupSurface.Opacity >= .99, "first entrance settles", ct);
            window.ShowSelection(new(2, "second", bounds, IntPtr.Zero, false), true);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(0, window.PopupSurface.Opacity);
                Assert.Equal(-24, window.PopupOffset.X);
            }
            // A cache hit can deliver the result before the next layout/render pass.
            window.ShowResult(new("Thứ hai", "en", "vi", true));
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating && window.PopupSurface.Opacity >= .99 && Math.Abs(window.PopupOffset.X) < .01, "immediate result preserves entrance", ct);
            Assert.Equal("Thứ hai", window.ResultText.Text);
            Assert.False(window.LoadingIndicator.IsAnimating);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void DismissPreservesLayoutAndReopeningBeforeTheFirstFrameUsesTheNewestSelection() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "first", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            window.Dismiss();
            if (SystemParameters.ClientAreaAnimation)
                Assert.Equal(Visibility.Visible, window.LoadingIndicator.Visibility);
            window.ShowSelection(new(2, "second", new Rect(400, 300, 100, 20), IntPtr.Zero, false), true);
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating && window.PopupSurface.Opacity >= .99 && Math.Abs(window.PopupOffset.X) < .01, "newest queued entrance settles", ct);
            Assert.True(window.IsVisible);
            Assert.Equal("second", window.OriginalText.Text);
            Assert.True(window.PopupSurface.IsHitTestVisible);
            Assert.Equal(Visibility.Visible, window.LoadingIndicator.Visibility);
            double height = window.ActualHeight;
            window.Dismiss();
            Assert.False(window.LoadingIndicator.IsAnimating);
            if (SystemParameters.ClientAreaAnimation)
                Assert.Equal(height, window.ActualHeight);
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "exit finishes before content is cleared", ct);
            Assert.Empty(window.OriginalText.Text);
            Assert.Null(window.PopupSurface.CacheMode);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdOpenAndReopenSlideHorizontallyWithoutAVisibleFinalFrame(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        var window = new TranslationWindow("auto", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        bool animated = !reduced && SystemParameters.ClientAreaAnimation;
        int shows = 0;
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible) return;
            shows++;
            Assert.Equal(animated ? 0 : 1, window.PopupSurface.Opacity);
            Assert.Equal(animated ? -24 : 0, window.PopupOffset.X);
            Assert.Equal(0, window.PopupOffset.Y);
            Assert.Equal(Visibility.Visible, window.CancelButton.Visibility);
        };
        try
        {
            foreach (var bounds in new[] { new Rect(80, 80, 100, 20), new Rect(400, 300, 100, 20) })
            {
                window.ShowSelection(new(shows + 1, "selected", bounds, IntPtr.Zero, false), true);
                Assert.IsType<TranslateTransform>(window.PopupSurface.RenderTransform);
                Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M11);
                Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M22);
                Assert.Equal(0, window.PopupOffset.Y);
                await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.PopupOffset.X) < .01 && window.PopupSurface.Opacity >= .99, "horizontal entrance settles", ct);
                window.Dismiss();
                Assert.Equal(0, window.PopupOffset.Y);
                await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "horizontal exit settles", ct);
            }
            Assert.Equal(2, shows);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanguageMenuReopensScrollsAndEscapeClosesOnlyTheDropdown(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        var window = new TranslationWindow("auto", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "Hello world", new Rect(80, 80, 100, 20), IntPtr.Zero, false), true);
            var combo = window.SourceLanguageCombo;
            combo.ApplyTemplate();
            combo.IsDropDownOpen = true;
            combo.IsDropDownOpen = false;
            combo.IsDropDownOpen = true;
            var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            var surface = Assert.IsType<Border>(combo.Template.FindName("DropdownBorder", combo));
            await WpfFrameWaiter.UntilAsync(() => surface.IsVisible && surface.Opacity >= .99 && Math.Abs(surface.RenderTransform.Value.M11 - 1) < .001, "language menu settles after reopen", ct);
            var scroll = Assert.IsType<ScrollViewer>(surface.Child);
            scroll.ApplyTemplate();
            var bar = Assert.IsType<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
            Assert.Equal(8, bar.Width);
            Assert.Equal(FontWeights.Bold, combo.FontWeight);
            Assert.Contains("SF Pro Display", combo.FontFamily.Source);
            Assert.Equal(System.Windows.Media.Color.FromRgb(15, 15, 18), Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.InRange(surface.ActualHeight, 100, 320);

            window.OriginalExpander.IsExpanded = true;
            window.OriginalExpander.IsExpanded = false;
            window.OriginalExpander.IsExpanded = true;
            var originalToggle = Assert.IsType<ToggleButton>(window.OriginalExpander.Template.FindName("OriginalToggle", window.OriginalExpander));
            var chevron = Assert.IsType<System.Windows.Shapes.Path>(originalToggle.Template.FindName("OriginalChevron", originalToggle));
            var rotation = Assert.IsType<RotateTransform>(chevron.RenderTransform);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(rotation.Angle) < .01, "original chevron settles after rapid toggles", ct);

            if (!reduced && Environment.GetEnvironmentVariable("TRANSLATION_MENU_CAPTURE") is string capture)
            {
                Capture(surface, capture + "-dropdown.png");
                Capture(window.PopupSurface, capture + "-panel.png");
                combo.IsDropDownOpen = false;
                window.ShowActivity(TranslationStage.LoadingModel, 4);
                window.UpdateLayout();
                Capture(Assert.IsType<Grid>(window.Content), capture + "-shadow.png");
                combo.IsDropDownOpen = true;
            }
            scroll.ScrollToEnd();
            await WpfFrameWaiter.UntilAsync(() => scroll.VerticalOffset > 0, "custom scroll thumb scrolls language list", ct);
            combo.SelectedValue = "zh";
            Assert.Equal("zh", window.SourceLanguage);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(TranslationWindow).GetMethod("Window_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, escape]);
            Assert.False(combo.IsDropDownOpen);
            Assert.True(window.IsVisible);
            combo.IsDropDownOpen = true;
            window.Dismiss();
            Assert.False(combo.IsDropDownOpen);
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "dismiss closes translation and its dropdown", ct);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static void Capture(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * 2), (int)Math.Ceiling(visual.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
