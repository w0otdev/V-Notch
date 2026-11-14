using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardSearchPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoverReversalsSettleAndKeyboardFocusKeepsTheRingVisible(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var surface = (Border)tray.FindName("SearchSurface");
            var ring = (Border)tray.FindName("SearchFeedbackRing");
            var icon = (TrayIcon)tray.FindName("SearchGlyph");
            var search = (TextBox)tray.FindName("SearchBox");
            var other = (Button)tray.FindName("AddLabelButton");
            var mouseOver = (DependencyPropertyKey)typeof(UIElement)
                .GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            SetSearchFocusState(tray, false);
            surface.SetValue(mouseOver, true);
            surface.SetValue(mouseOver, false);
            surface.SetValue(mouseOver, true);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(ring.Opacity - .55) < .001
                && Math.Abs(icon.RenderTransform.Value.M11 - (reducedMotion ? 1 : 1.06)) < .001,
                "search hover settles to the latest pointer state", ct);

            SetSearchFocusState(tray, true);
            surface.SetValue(mouseOver, false);
            await WpfFrameWaiter.UntilAsync(() => ring.Opacity == 1
                && Math.Abs(icon.RenderTransform.Value.M11 - (reducedMotion ? 1 : 1.08)) < .001,
                "keyboard focus stays visible after pointer leaves", ct);
            SetSearchFocusState(tray, false);
            await WpfFrameWaiter.UntilAsync(() => ring.Opacity == 0
                && Math.Abs(icon.RenderTransform.Value.M11 - 1) < .001,
                "search feedback returns to rest", ct);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previousMotion);
        }
    });

    [Fact]
    public void EmptySearchFadesItsHintOnFocusAndRestoresItOnBlur() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)tray.FindName("SearchBox");
            var hint = (TextBlock)tray.FindName("SearchHint");
            var other = (Button)tray.FindName("AddLabelButton");
            SetSearchFocusState(tray, false);
            await WpfFrameWaiter.UntilAsync(() => hint.Opacity == 1, "empty search hint is visible", ct);

            SetSearchFocusState(tray, true);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            await WpfFrameWaiter.UntilAsync(() => hint.Opacity == 0, "search hint fades on focus", ct);

            search.Text = "report";
            SetSearchFocusState(tray, false);
            Assert.Equal(0, hint.Opacity);
            search.Clear();
            await WpfFrameWaiter.UntilAsync(() => hint.Opacity == 1, "empty unfocused hint fades back in", ct);

            // A rapid reversal must leave the latest focus state visible.
            SetSearchFocusState(tray, true);
            await WpfFrameWaiter.NextAsync(ct);
            SetSearchFocusState(tray, false);
            await WpfFrameWaiter.UntilAsync(() => hint.Opacity == 1, "reversed hint fade settles", ct);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TypingAndClearingKeepTheInputSteadyAndEscapeClearsTheQuery() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)tray.FindName("SearchBox");
            var textSurface = (Grid)tray.FindName("SearchTextSurface");
            SetSearchFocusState(tray, true);
            Matrix before = textSurface.RenderTransform.Value;
            search.Text = "report";
            Assert.Equal(before, textSurface.RenderTransform.Value);
            Assert.Equal(1, textSurface.Opacity);

            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.Empty(search.Text);
            Assert.Same(search, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(search)));
            Assert.Equal(before, textSurface.RenderTransform.Value);
            Assert.Equal(1, textSurface.Opacity);
            await WpfFrameWaiter.UntilAsync(() => ((TextBlock)tray.FindName("SearchHint")).Opacity == 0,
                "clearing a focused search keeps its hint hidden", ct);

            search.Text = "second query";
            ((Button)tray.FindName("ClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(search.Text);
            Assert.Same(search, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(search)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FocusedSearchFollowsLiveAccentChanges() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)tray.FindName("SearchBox");
            var ring = (Border)tray.FindName("SearchFeedbackRing");
            var fill = (Border)tray.FindName("SearchFeedbackFill");
            var icon = (TrayIcon)tray.FindName("SearchGlyph");
            SetSearchFocusState(tray, true);
            foreach (string accent in new[] { "blue", "pink", "green", "default" })
            {
                AccentTheme.Apply(accent);
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                var expected = AccentTheme.GetSwatch(accent).Color;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(ring.BorderBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(search.CaretBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(search.SelectionBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(icon.Foreground).Color);
                Assert.Equal(30, Assert.IsType<SolidColorBrush>(fill.Background).Color.A);
            }
        }
        finally
        {
            window.Close();
            AccentTheme.Apply("default");
        }
    });

    private static void SetSearchFocusState(ClipboardTray tray, bool focused)
    {
        var search = (TextBox)tray.FindName("SearchBox");
        var other = (Button)tray.FindName("AddLabelButton");
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(search), focused ? search : other);
        // Background test windows cannot take native keyboard focus. Exercise the
        // same dependency-property state that drives the production focus visuals.
        var key = (DependencyPropertyKey)typeof(UIElement)
            .GetField("IsKeyboardFocusWithinPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        search.SetValue(key, focused);
        ((Border)tray.FindName("SearchSurface")).SetValue(key, focused);
    }
}
