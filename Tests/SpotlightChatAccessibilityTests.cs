using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightChatAccessibilityTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ChatTextClearsContrastOnBlackAndBrightestSettledGlass()
    {
        // A white desktop under the existing 64% black readability scrim.
        var brightestGlass = Color.FromRgb(92, 92, 92);
        foreach (var background in new[] { Colors.Black, brightestGlass, SpotlightChatPalette.Surface.Color,
                     SpotlightChatPalette.Hover.Color, SpotlightChatPalette.Pressed.Color })
        foreach (var brush in new[] { SpotlightChatPalette.Primary, SpotlightChatPalette.Secondary,
                     SpotlightChatPalette.Link })
            Assert.True(Contrast(brush.Color, background) >= 4.5,
                $"{brush.Color} on {background}: {Contrast(brush.Color, background):F2}:1");
        Assert.True(Contrast(SpotlightChatPalette.Error.Color, SpotlightChatPalette.Surface.Color) >= 4.5);
    }

    [Fact]
    public void KeyboardReachesTranscriptCopyAndOpensHistoryWithoutSendingDraft() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { HasSeenSpotlightIntro = true }, saveChatHistory: true);
            var window = fixture.Window;
            window.ShowSpotlight();
            var searchSelectionBrush = window.SearchBox.SelectionBrush;
            var searchSelectionText = window.SearchBox.SelectionTextBrush;
            double searchSelectionOpacity = window.SearchBox.SelectionOpacity;
            Invoke(window, "ToggleAiMode");
            Assert.False(window.AiCopyButton.IsEnabled);
            Assert.Same(SpotlightChatPalette.Secondary, window.PlaceholderText.Foreground);
            Assert.Equal(FontWeights.Bold, window.SearchBox.FontWeight);
            Assert.Equal(FontWeights.Bold, window.PlaceholderText.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiModeLabel.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiMetadata.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiUsageText.FontWeight);
            AssertBoldTypography(window.AiPanel);
            Assert.Same(searchSelectionBrush, window.SearchBox.SelectionBrush);
            Assert.Same(searchSelectionText, window.SearchBox.SelectionTextBrush);
            Assert.Equal(searchSelectionOpacity, window.SearchBox.SelectionOpacity);
            Assert.InRange(window.SearchBox.SelectionOpacity, 0.2, 0.4);
            window.SearchBox.Text = "Select all in AI chat";
            window.SearchBox.SelectAll();
            Assert.Equal(window.SearchBox.Text.Length, window.SearchBox.SelectionLength);
            window.SearchBox.Clear();
            Capture(window, "welcome");
            Invoke(window, "SetAiStatus", "spotlight.ai.configure", Array.Empty<object>());
            Assert.Same(SpotlightChatPalette.Error, window.AiStatus.Foreground);
            Capture(window, "configuration-warning");
            Invoke(window, "SetAiStatus", "spotlight.ai.hint", Array.Empty<object>());

            var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
            history.Add(new("user", "How can I make a small workspace feel calmer and more focused?"));
            history.Add(new("assistant", "Start with **one clear surface** and keep only the tools you use every day.\n\n" +
                "- Give your screen a little breathing room.\n- Use a soft, indirect light.\n- Keep a small place for notes.\n\n" +
                "> A calmer space makes the next step easier to see.\n\nTry `one task at a time`."));
            Invoke(window, "RenderAiHistory");
            Invoke(window, "RefreshAiPanel");
            window.UpdateLayout();
            window.AiTranscriptScroll.ScrollToTop();
            Capture(window, "conversation");
            AssertBoldTypography(window.AiPanel);
            foreach (var view in Descendants<RichTextBox>(window.AiTranscript))
            {
                AssertBoldTypography(view.Document);
                Assert.Equal(searchSelectionOpacity, view.SelectionOpacity);
                Assert.Same(searchSelectionBrush, view.SelectionBrush);
            }
            // Streaming and final Markdown must agree, including code, tables and quotes.
            const string markdown = "# Heading\n\nBody **bold** and `inline code`.\n\n```csharp\nvar x = 1;\n```\n\n> Quote\n\n- List item\n\n| Name | Value |\n| --- | --- |\n| A | B |";
            var renderer = new AiMarkdown.StreamRenderer(uniformBold: true);
            while (!renderer.Advance(markdown, 3, 0)) AssertBoldTypography(renderer.Document);
            AssertBoldTypography(renderer.Document);
            AssertBoldTypography(AiMarkdown.Render(markdown, uniformBold: true));
            var copy = Descendants<Button>(window.AiTranscript).Single();
            Assert.Equal(Loc.Get("spotlight.ai.copy"), AutomationProperties.GetName(copy));
            // The background test window must not inherit keyboard focus from an earlier fixture.
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(window, window.AiNewChatButton);
            RouteKey(window, Key.Tab);
            Assert.Same(copy, FocusManager.GetFocusedElement(window));
            Assert.False(RouteKey(window, Key.Enter).Handled);
            Assert.Equal(2, history.Count);

            var chat = new SpotlightSavedChat
            {
                Provider = "OpenAI", Model = "gpt-4.1", Draft = "A saved follow-up",
                Messages = [new("user", "A calmer workspace"), new("assistant", "Keep the essentials close.")]
            };
            await fixture.ChatStore.SaveChatAsync(chat);
            window.ShowAiHistoryPanel();
            await Field<Task>(window, "_historyPageTask");
            window.UpdateLayout();
            Assert.True(window.AiHistoryItems.Items.Count > 0, "Repository page must be published before realizing cards.");
            Assert.True(window.AiPanel.IsVisible, $"AI panel is hidden; mode={Field<bool>(window, "_aiMode")}, panel={window.AiPanel.Visibility}");
            Assert.True(window.AiHistoryItems.IsVisible, $"History list is hidden; history={window.AiHistoryPanel.Visibility}, size={window.AiHistoryItems.ActualWidth}x{window.AiHistoryItems.ActualHeight}");
            await WpfFrameWaiter.UntilAsync(() => window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(0) != null, "first history card realized", ct);
            Capture(window, "history");
            AssertBoldTypography(window.AiHistoryItems);
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(window, window.AiNewChatButton);
            for (int i = 0; i < window.AiHistoryItems.Items.Count * 2; i++)
            {
                RouteKey(window, Key.Tab);
                if (FocusManager.GetFocusedElement(window) is Button focused && AutomationProperties.GetName(focused) == chat.Title) break;
            }
            var open = Assert.IsType<Button>(FocusManager.GetFocusedElement(window));
            Assert.Equal(chat.Title, AutomationProperties.GetName(open));
            window.SearchBox.Text = "Do not send this draft";
            var enter = RouteKey(window, Key.Enter);
            Assert.False(enter.Handled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => window.AiHistoryPanel.Visibility == Visibility.Collapsed, "chat loaded", ct);
            Assert.Equal(chat.Id, Field<string>(window, "_chatId"));
            Assert.Equal(chat.Draft, window.SearchBox.Text);
            Assert.Equal(Visibility.Collapsed, window.AiHistoryPanel.Visibility);

            window.Shell.Background = new SolidColorBrush(Color.FromRgb(92, 92, 92));
            Invoke(window, "SetAiStatus", "spotlight.ai.networkError", Array.Empty<object>());
            Capture(window, "bright-backdrop-error");
            Assert.Same(SpotlightChatPalette.Error, window.AiStatus.Foreground);
            Assert.Same(SpotlightChatPalette.Surface, window.AiStatus.Background);
            Assert.True(Contrast(((SolidColorBrush)window.AiStatus.Foreground).Color,
                ((SolidColorBrush)window.AiStatus.Background).Color) >= 4.5);
            Invoke(window, "SetAiStatus", "spotlight.ai.hint", Array.Empty<object>());
            Assert.Null(window.AiStatus.Background);
            Assert.Equal(new Thickness(0), window.AiStatus.Padding);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void UserBubblesFollowLiveAccentAndKeepWrappedTextReadable() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        using var fixture = new SpotlightWindowFixture(new NotchSettings { HasSeenSpotlightIntro = true });
        var window = fixture.Window;
        string[] accentKeys = ["AccentBrush", "AccentBrushStrong", "AccentChatBubbleBrush", "AccentForegroundBrush",
            "AccentHoverBrush", "AccentSubtleBrush", "AccentNavHoverBrush", "AccentNavHoverBorderBrush"];
        var originalBrushes = accentKeys.ToDictionary(key => key, key => Application.Current.TryFindResource(key));
        try
        {
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
            history.Add(new("user", "Help me turn this idea into a clear plan, with a few practical steps I can start today. Keep each step focused and easy to follow."));
            history.Add(new("assistant", "Start with **one small outcome**. Write down your next step, set aside 20 minutes, and review what you learned."));
            history.Add(new("user", "Thanks!"));
            Invoke(window, "RenderAiHistory");
            Invoke(window, "RefreshAiPanel");
            window.UpdateLayout();
            var bubble = Assert.IsType<Border>(window.AiTranscript.Children[0]);
            var shortBubble = Assert.IsType<Border>(window.AiTranscript.Children[2]);
            var text = Assert.IsType<TextBlock>(bubble.Child);
            Assert.Equal(HorizontalAlignment.Right, bubble.HorizontalAlignment);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.True(bubble.ActualWidth <= 520);
            Assert.True(bubble.ActualWidth > shortBubble.ActualWidth);
            Assert.True(bubble.ActualHeight > shortBubble.ActualHeight);
            Assert.Contains(Loc.Get("spotlight.ai.you"), AutomationProperties.GetName(text));

            foreach (var swatch in AccentTheme.Swatches)
            {
                // Existing bubbles must follow live theme changes without rebuilding the transcript.
                AccentTheme.Apply(swatch.Id);
                window.UpdateLayout();
                var fill = Assert.IsType<SolidColorBrush>(bubble.Background);
                Assert.Same(Application.Current.Resources["AccentChatBubbleBrush"], fill);
                Assert.Same(fill, shortBubble.Background);
                Assert.Equal((byte)255, fill.Color.A);
                Assert.NotEqual(swatch.Color, fill.Color);
                Assert.True(Contrast(((SolidColorBrush)text.Foreground).Color, fill.Color) >= 4.5,
                    $"Unreadable {swatch.Id} user bubble");
                Capture(window, "bubble-" + swatch.Id);
            }
        }
        finally
        {
            foreach (var (key, brush) in originalBrushes)
            {
                if (brush != null) Application.Current.Resources[key] = brush;
                else Application.Current.Resources.Remove(key);
            }
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuggestionsAnimateWithoutDelayingEditingOrLeavingStaleMotion(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            using var fixture = new SpotlightWindowFixture(new NotchSettings { HasSeenSpotlightIntro = true });
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            await WpfFrameWaiter.UntilAsync(() => window.AiPromptOne.IsLoaded && window.AiPromptOne.IsVisible &&
                window.ContentRegion.Opacity > .99 && !Field<bool>(window, "_entranceActive") &&
                !window.ContentRegion.ClipToBounds && window.ContentRegion.ActualHeight >= window.AiPanel.ActualHeight,
                "suggestions presented", ct);
            window.UpdateLayout();
            var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var writeFlag = typeof(UIElement).GetMethod("WriteFlag", Private)!;
            object mouseOverFlag = Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsMouseOverCache");
            void SetHover(UIElement target, bool value)
            {
                // Match WPF's cached CLR getter as well as its dependency property,
                // without taking over the user's physical pointer.
                writeFlag.Invoke(target, [mouseOverFlag, value]);
                target.SetValue(hoverKey, value);
                Assert.Equal(value, target.IsMouseOver);
            }

            foreach (var button in new[] { window.AiPromptOne, window.AiPromptTwo })
            {
                button.ApplyTemplate();
                var surface = (Grid)button.Template.FindName("MotionSurface", button);
                var hover = (Border)button.Template.FindName("HoverOverlay", button);
                var press = (Border)button.Template.FindName("PressedOverlay", button);
                var transforms = Assert.IsType<TransformGroup>(surface.RenderTransform);
                var scale = transforms.Children.OfType<ScaleTransform>().Single();
                var lift = transforms.Children.OfType<TranslateTransform>().Single();
                double width = button.ActualWidth;
                SetHover(button, true);
                Assert.Equal(1d, hover.GetAnimationBaseValue(UIElement.OpacityProperty));
                await WpfFrameWaiter.NextAsync(ct);
                SetHover(button, false);
                SetHover(button, true);
                await WpfFrameWaiter.UntilAsync(() => hover.Opacity == 1, "hover reversal settles", ct);
                Assert.Equal(width, button.ActualWidth);
                Assert.True(reducedMotion ? scale.ScaleX == 1 && lift.Y == 0 : scale.ScaleX > 1 && lift.Y < 0);
                Capture(window, button.Name + "-hover-" + reducedMotion);
                typeof(Button).GetProperty(nameof(Button.IsPressed))!.SetValue(button, true);
                await WpfFrameWaiter.UntilAsync(() => press.Opacity == 1, "press feedback settles", ct);
                Assert.True(reducedMotion ? scale.ScaleX == 1 : scale.ScaleX < 1);
                typeof(Button).GetProperty(nameof(Button.IsPressed))!.SetValue(button, false);
                SetHover(button, false);
                await WpfFrameWaiter.UntilAsync(() => hover.Opacity == 0 && press.Opacity == 0 && scale.ScaleX == 1,
                    "suggestion returns to rest", ct);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(button.Content, window.SearchBox.Text);
                Assert.Equal(window.SearchBox.Text.Length, window.SearchBox.CaretIndex);
                LinearGradientBrush? reveal = null;
                if (reducedMotion) Assert.Null(window.SearchBox.OpacityMask);
                else
                {
                    reveal = Assert.IsType<LinearGradientBrush>(window.SearchBox.OpacityMask);
                    Assert.All(reveal.GradientStops, stop => Assert.True(stop.HasAnimatedProperties));
                    await WpfFrameWaiter.UntilAsync(() => reveal.GradientStops[0].Offset > 0.12 ||
                        window.SearchBox.OpacityMask == null, "ink sweep reaches the text", ct);
                }
                Assert.True(window.SearchBox.RenderTransform.Value.IsIdentity);
                Capture(window, button.Name + "-text-reveal-" + reducedMotion);
                if (button == window.AiPromptTwo)
                {
                    window.SearchBox.SelectAll();
                    Assert.Equal(window.SearchBox.Text.Length, window.SearchBox.SelectionLength);
                    Assert.Null(window.SearchBox.OpacityMask);
                }
                window.SearchBox.Text += " with my own details";
                Assert.Equal(1, window.SearchBox.Opacity);
                Assert.Null(window.SearchBox.OpacityMask);
                if (reveal != null) Assert.All(reveal.GradientStops, stop => Assert.False(stop.HasAnimatedProperties));
            }

            window.AiPromptOne.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.AiPromptTwo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(window.AiPromptTwo.Content, window.SearchBox.Text);
            await WpfFrameWaiter.UntilAsync(() => window.SearchBox.OpacityMask == null, "ink sweep completes and releases its mask", ct);
            Assert.Equal(window.AiPromptTwo.Content, window.SearchBox.Text);
            Capture(window, "text-reveal-complete-" + reducedMotion);
            window.AiPromptOne.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AnimationConfig.SetReduceMotion(true);
            Assert.Equal(1, window.SearchBox.Opacity);
            Assert.Null(window.SearchBox.OpacityMask);
            SetHover(window.AiPromptTwo, true);
            window.HideSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "suggestion window dismissed", ct);
            var hiddenSurface = (Grid)window.AiPromptTwo.Template.FindName("MotionSurface", window.AiPromptTwo);
            Assert.All(((TransformGroup)hiddenSurface.RenderTransform).Children, transform => Assert.False(transform.HasAnimatedProperties));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static KeyEventArgs RouteKey(SpotlightWindow window, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Invoke(window, "Window_PreviewKeyDown", window, e);
        return e;
    }

    private static void AssertBoldTypography(DependencyObject root)
    {
        var font = (FontFamily)Application.Current.FindResource("SFProDisplay");
        foreach (var text in Descendants<TextBlock>(root))
        {
            Assert.Equal(FontWeights.Bold, text.FontWeight);
            Assert.Equal(font.Source, text.FontFamily.Source);
        }
        foreach (var text in Descendants<TextElement>(root))
        {
            Assert.Equal(FontWeights.Bold, text.FontWeight);
            Assert.Equal(font.Source, text.FontFamily.Source);
        }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte value) => value / 255d <= 0.04045
            ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Capture(SpotlightWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_CHAT_PREVIEW") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var size = new Size(window.Shell.ActualWidth, window.Shell.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(window.Shell), null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 1.5),
            (int)Math.Ceiling(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static T Field<T>(SpotlightWindow window, string name) =>
        (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(SpotlightWindow window, string name, params object[] args) =>
        typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is ItemsControl items)
        {
            for (int i = 0; i < items.Items.Count; i++)
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is ContentControl { Content: DependencyObject content })
                    foreach (var child in Descendants<T>(content)) yield return child;
            yield break;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match) yield return match;
            if (child is DependencyObject nested)
                foreach (var descendant in Descendants<T>(nested)) yield return descendant;
        }
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new ContainerVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
