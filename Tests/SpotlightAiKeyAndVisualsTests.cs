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
    public void AiEdges_UseLightweightSolidBrushAndCollapseOverHistoryPanel() => SharedStaTestRunner.Run(() =>
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
            Assert.IsType<SolidColorBrush>(window.AiTopEdge.Background);
            Assert.IsType<SolidColorBrush>(window.AiBottomEdge.Background);

            Invoke(window, "ToggleAiHistoryPage");
            Assert.Equal(Visibility.Visible, window.AiHistoryPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiTopEdge.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AiBottomEdge.Visibility);

            Invoke(window, "ToggleAiHistoryPage");
            Assert.Equal(Visibility.Collapsed, window.AiHistoryPanel.Visibility);
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
