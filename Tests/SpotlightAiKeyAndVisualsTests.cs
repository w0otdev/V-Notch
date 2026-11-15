using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightAiKeyAndVisualsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Fact]
    public void IsAiToggle_RequiresUnmodifiedTabAndSearchBoxFocus()
    {
        Assert.True(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.None));
        Assert.True(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.None, searchBoxFocused: true));
        Assert.False(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.None, searchBoxFocused: false));
        Assert.False(SpotlightWindow.IsAiToggle(Key.Tab, ModifierKeys.Shift, searchBoxFocused: true));
    }

    [Fact]
    public void AiMode_TabWhenSearchBoxNotFocused_CyclesToolbarControlsWithoutExitingAiMode() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(Settings(), saveChatHistory: true);
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            Assert.True(Field<bool>(window, "_aiMode"));

            FocusManager.SetFocusedElement(window, window.AiBackButton);
            window.AiBackButton.Focus();

            // The provider/model picker is a keyboard-accessible toolbar action
            // between Back and History, matching the visible control order.
            var tabToProvider = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            Invoke(window, "Window_PreviewKeyDown", window, tabToProvider);
            Assert.True(tabToProvider.Handled);
            Assert.True(Field<bool>(window, "_aiMode"));
            Assert.Same(window.AiProviderButton, FocusManager.GetFocusedElement(window));

            var tabToHistory = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            Invoke(window, "Window_PreviewKeyDown", window, tabToHistory);
            Assert.True(tabToHistory.Handled);
            Assert.True(Field<bool>(window, "_aiMode"));
            Assert.Same(window.AiHistoryButton, FocusManager.GetFocusedElement(window));

            var tabToNewChat = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            Invoke(window, "Window_PreviewKeyDown", window, tabToNewChat);
            Assert.True(tabToNewChat.Handled);
            Assert.True(Field<bool>(window, "_aiMode"));
            Assert.Same(window.AiNewChatButton, FocusManager.GetFocusedElement(window));
        }
        finally
        {
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Fact]
    public void AiEdges_FadeOnDefaultSkinAndStayHiddenOverGlassAndHistory() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            using var fixture = new SpotlightWindowFixture(Settings(), saveChatHistory: true);
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");

            Assert.Null(window.AiTopEdge.Child);
            Assert.Null(window.AiBottomEdge.Child);
            Assert.Null(window.AiTopEdge.OpacityMask);
            Assert.Null(window.AiBottomEdge.OpacityMask);
            Assert.Null(window.AiTopEdge.Effect);
            Assert.Null(window.AiBottomEdge.Effect);
            var topFade = Assert.IsType<LinearGradientBrush>(window.AiTopEdge.Background);
            var bottomFade = Assert.IsType<LinearGradientBrush>(window.AiBottomEdge.Background);
            Assert.True(topFade.GradientStops[0].Color.A > topFade.GradientStops[1].Color.A);
            Assert.Equal(0, topFade.GradientStops[1].Color.A);
            Assert.Equal(0, bottomFade.GradientStops[0].Color.A);
            Assert.True(bottomFade.GradientStops[1].Color.A > bottomFade.GradientStops[0].Color.A);

            Invoke(window, "ToggleAiHistoryPage");
            Assert.Equal(Visibility.Visible, window.AiHistoryPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiBottomEdge.Visibility);

            Invoke(window, "ToggleAiHistoryPage");
            Assert.Equal(Visibility.Collapsed, window.AiHistoryPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Visible, window.AiBottomEdge.Visibility);

            // Overflow both ends, then change skin while the conversation is
            // open. Further streaming/layout events must not restore the bars.
            var content = new System.Windows.Controls.Border { Height = 1000 };
            window.AiTranscript.Children.Add(content);
            window.UpdateLayout();
            window.AiTranscriptScroll.ScrollToVerticalOffset(200);
            window.UpdateLayout();
            window.UpdateAiScrollEdges();
            Assert.True(window.AiTranscriptScroll.ScrollableHeight > 200);
            Assert.Equal(1, window.AiTopEdge.Opacity);
            Assert.Equal(1, window.AiBottomEdge.Opacity);

            var settings = Field<NotchSettings>(window, "_settings");
            settings.NotchStyle = "liquidglass";
            settings.LiquidGlass.UseGpuRefraction = false;
            window.ApplyLiquidGlassSkin();
            Assert.Equal(Visibility.Collapsed, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiBottomEdge.Visibility);
            content.Height = 1200;
            window.AiTranscriptScroll.ScrollToVerticalOffset(300);
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiBottomEdge.Visibility);

            settings.NotchStyle = "default";
            window.ApplyLiquidGlassSkin();
            Assert.Equal(Visibility.Visible, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Visible, window.AiBottomEdge.Visibility);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    private static NotchSettings Settings()
    {
        var settings = new NotchSettings { SpotlightAiProvider = "OpenAI" };
        SpotlightAiService.Configure(settings, "OpenAI", "test-secret", "test-model");
        return settings;
    }

    private static T Field<T>(SpotlightWindow window, string name) =>
        (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;

    private static object? Invoke(SpotlightWindow window, string name, params object?[] args) =>
        typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new ContainerVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
