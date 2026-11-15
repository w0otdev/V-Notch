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
    public void KeyboardReachesTranscriptCopyAndOpensHistoryWithoutSendingDraft() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { HasSeenSpotlightIntro = true });
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            Assert.False(window.AiCopyButton.IsEnabled);
            Assert.Same(SpotlightChatPalette.Secondary, window.PlaceholderText.Foreground);
            Assert.Equal(FontWeights.Bold, window.SearchBox.FontWeight);
            Assert.Equal(FontWeights.Bold, window.PlaceholderText.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiModeLabel.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiMetadata.FontWeight);
            Assert.Equal(FontWeights.Bold, window.AiUsageText.FontWeight);
            AssertBoldTypography(window.AiPanel);
            Assert.True(Contrast(((SolidColorBrush)window.SearchBox.SelectionTextBrush).Color,
                ((SolidColorBrush)window.SearchBox.SelectionBrush).Color) >= 4.5);
            Capture(window, "welcome");

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
                AssertBoldTypography(view.Document);
            // Streaming and final Markdown must agree, including code, tables and quotes.
            const string markdown = "# Heading\n\nBody **bold** and `inline code`.\n\n```csharp\nvar x = 1;\n```\n\n> Quote\n\n- List item\n\n| Name | Value |\n| --- | --- |\n| A | B |";
            var renderer = new AiMarkdown.StreamRenderer(uniformBold: true);
            while (!renderer.Advance(markdown, 3, 0)) AssertBoldTypography(renderer.Document);
            AssertBoldTypography(renderer.Document);
            AssertBoldTypography(AiMarkdown.Render(markdown, uniformBold: true));
            var copy = Descendants<Button>(window.AiTranscript).Single();
            Assert.Equal(Loc.Get("spotlight.ai.copy"), AutomationProperties.GetName(copy));
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
            Field<List<SpotlightSavedChat>>(window, "_savedChats").Add(chat);
            window.ShowAiHistoryPanel();
            window.UpdateLayout();
            Capture(window, "history");
            AssertBoldTypography(window.AiHistoryItems);
            var open = Descendants<Button>(window.AiHistoryItems).Single(button =>
                AutomationProperties.GetName(button) == chat.Title);
            FocusManager.SetFocusedElement(window, window.AiNewChatButton);
            RouteKey(window, Key.Tab);
            Assert.Same(open, FocusManager.GetFocusedElement(window));
            window.SearchBox.Text = "Do not send this draft";
            var enter = RouteKey(window, Key.Enter);
            Assert.False(enter.Handled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
