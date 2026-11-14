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
    [Fact]
    public void EmptySearchHidesItsHintOnFocusAndRestoresItOnBlur() => SharedStaTestRunner.RunAsync(async () =>
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
            other.Focus();
            Assert.Equal(Visibility.Visible, hint.Visibility);

            search.Focus();
            Assert.True(search.IsKeyboardFocusWithin);
            Assert.Equal(Visibility.Collapsed, hint.Visibility);

            search.Text = "report";
            other.Focus();
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            search.Clear();
            Assert.Equal(Visibility.Visible, hint.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TypingAndClearingKeepTheInputSteadyAndEscapeClearsTheQuery() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)tray.FindName("SearchBox");
            var textSurface = (Grid)tray.FindName("SearchTextSurface");
            search.Focus();
            Matrix before = textSurface.RenderTransform.Value;
            search.Text = "report";
            Assert.Equal(before, textSurface.RenderTransform.Value);
            Assert.Equal(1, textSurface.Opacity);

            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.Empty(search.Text);
            Assert.True(search.IsKeyboardFocusWithin);
            Assert.Equal(before, textSurface.RenderTransform.Value);
            Assert.Equal(1, textSurface.Opacity);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)tray.FindName("SearchHint")).Visibility);

            search.Text = "second query";
            ((Button)tray.FindName("ClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(search.Text);
            Assert.True(search.IsKeyboardFocusWithin);
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
            var surface = (Border)tray.FindName("SearchSurface");
            search.Focus();
            foreach (string accent in new[] { "blue", "pink", "green", "default" })
            {
                AccentTheme.Apply(accent);
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                var expected = AccentTheme.GetSwatch(accent).Color;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(surface.BorderBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(search.CaretBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(search.SelectionBrush).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(FindIcon(surface).Foreground).Color);
                Assert.Equal(30, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
            }
        }
        finally
        {
            window.Close();
            AccentTheme.Apply("default");
        }
    });

    private static TrayIcon FindIcon(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TrayIcon icon) return icon;
            if (child is Panel)
            {
                var match = FindIcon(child);
                if (match != null) return match;
            }
        }
        return null!;
    }
}
