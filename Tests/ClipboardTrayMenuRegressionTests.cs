using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayMenuRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Fact]
    public void RotatingMoreDoesNotPaintOutsideTheActionArea() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var card = new ClipboardCardViewModel(new ClipboardEntry { Title = "Render probe", Kind = ClipboardKind.Text });
        var cards = (ObservableCollection<ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", Private)!.GetValue(tray)!;
        cards.Add(card);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(600);
            var list = (ListBox)tray.FindName("Cards");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
            var button = Descendants(item).OfType<Button>().Single(b => Equals(b.Tag, "more"));
            var origin = button.TranslatePoint(new Point(), item);
            var allowed = new Rect(origin.X - 8, origin.Y - 8, button.ActualWidth + 16, button.ActualHeight + 16);
            byte[] Render()
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(item.ActualWidth), (int)Math.Ceiling(item.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(item);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return pixels;
            }
            var before = Render();
            card.IsMenuOpen = true;
            await Task.Delay(300);
            var after = Render();
            int width = (int)Math.Ceiling(item.ActualWidth);
            for (int i = 0; i < before.Length; i += 4)
            {
                if (allowed.Contains(new Point(i / 4 % width, i / 4 / width))) continue;
                for (int channel = 0; channel < 4; channel++) Assert.Equal(before[i + channel], after[i + channel]);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OpeningTheMenuRotatesOnlyTheMoreIconAndResetsItWhenClosed(bool pinned, bool reducedMotion) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        using var tray = new ClipboardTray();
        var card = new ClipboardCardViewModel(new ClipboardEntry { Title = "A file", Kind = ClipboardKind.File, IsPinned = pinned });
        var cards = (ObservableCollection<ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", Private)!.GetValue(tray)!;
        cards.Add(card);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            var list = (ListBox)tray.FindName("Cards");
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
            var buttons = Descendants(container).OfType<Button>().Where(button => button.Tag is "pin" or "more").ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.All(buttons, button => Assert.Equal(0, Angle(button), 3));

            foreach (bool open in new[] { true, false, true, false })
            {
                card.IsMenuOpen = open;
                await Task.Delay(300);
                for (int index = 0; index < buttons.Length; index++)
                    Assert.Equal(open && Equals(buttons[index].Tag, "more") ? 90 : 0, Angle(buttons[index]), 3);
            }
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previousReducedMotion); }
    });

    [Fact]
    public void MoreMenuOpensOnClickAndStaysOpenAfterPointerLeaves() => SharedStaTestRunner.RunAsync(async () =>
    {
        var previousInputDevice = InputManager.Current.MostRecentInputDevice;
        SetInputDevice(Mouse.PrimaryDevice);
        string root = Path.Combine(Path.GetTempPath(), "vnotch-click-menu-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        ContextMenu? menu = null;
        try
        {
            await store.InitializeAsync();
            await store.ImportAsync(new ClipboardCapture { Text = "Click menu test" });
            tray.Attach(controller);
            window.Show();
            await (Task)typeof(ClipboardTray).GetMethod("RefreshAsync", Private)!.Invoke(tray, null)!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = Descendants(tray).OfType<Button>().Single(button => Equals(button.Tag, "more"));
            var visual = (FrameworkElement)typeof(ClipboardTray).GetMethod("FindCardVisual", Private)!.Invoke(null, [button])!;
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.False(visual.ContextMenu?.IsOpen == true);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu = visual.ContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            Assert.False(menu.StaysOpen);
            Assert.NotEmpty(menu.Items);
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            await Task.Delay(300);
            Assert.True(menu.IsOpen);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(menu.IsOpen);
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            window.Close();
            SetInputDevice(previousInputDevice);
        }
    });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void MenuAndSubmenuStayAtFullSizeAfterOpening(bool submenu, bool reducedMotion, bool keyboard) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        var previousInputDevice = InputManager.Current.MostRecentInputDevice;
        AnimationConfig.SetReduceMotion(reducedMotion);
        var target = new Button { Width = 24, Height = 24 };
        var surface = new Border { Width = 220, Height = 240, Background = Brushes.Black };
        var host = new StackPanel { Children = { target, surface } };
        var window = new BackgroundWindow { Content = host, Width = 300, Height = 320 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SetInputDevice(keyboard ? Keyboard.PrimaryDevice : Mouse.PrimaryDevice);
            Assert.Equal(keyboard, InputManager.Current.MostRecentInputDevice is KeyboardDevice);

            foreach (bool fresh in new[] { true, false })
            {
                typeof(ClipboardTray).GetMethod("AnimateMenuSurface", Private)!.Invoke(null, [surface, target, fresh, submenu]);
                await Task.Delay(300);
                var bounds = surface.TransformToAncestor(host).TransformBounds(new Rect(surface.RenderSize));
                Assert.Equal(surface.ActualWidth, bounds.Width, 3);
                Assert.Equal(surface.ActualHeight, bounds.Height, 3);
                Assert.Equal(1, surface.Opacity, 3);

                // Removing a completed clock must keep the final dimensions, too.
                if (surface.RenderTransform is TransformGroup motion)
                {
                    var scale = motion.Children.OfType<ScaleTransform>().Single();
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    Assert.Equal(1, scale.ScaleX);
                    Assert.Equal(1, scale.ScaleY);
                }
            }
        }
        finally
        {
            SetInputDevice(previousInputDevice);
            window.Close();
            AnimationConfig.SetReduceMotion(previousReducedMotion);
        }
    });

    [Fact]
    public void MenuEntranceAndRetargetStayMonotonicWithoutBouncingPastFullSize() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        var previousInputDevice = InputManager.Current.MostRecentInputDevice;
        AnimationConfig.SetReduceMotion(false);
        SetInputDevice(Mouse.PrimaryDevice);
        var surface = new Border { Width = 220, Height = 240 };
        var window = new BackgroundWindow { Content = surface, Width = 300, Height = 320 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var animate = typeof(ClipboardTray).GetMethod("AnimateMenuSurface", Private)!;
            animate.Invoke(null, [surface, null, true, false]);
            double previousScale = 0.96, previousOpacity = 0;
            for (int frame = 0; frame < 24; frame++)
            {
                await WpfFrameWaiter.NextAsync();
                double scale = surface.RenderTransform.Value.M11;
                Assert.InRange(scale, previousScale - 0.00001, 1.00001);
                Assert.InRange(surface.Opacity, previousOpacity - 0.00001, 1);
                previousScale = scale;
                previousOpacity = surface.Opacity;
                if (frame == 1) animate.Invoke(null, [surface, null, false, false]);
            }
            await WpfFrameWaiter.UntilAsync(() => surface.RenderTransform.Value.M11 == 1 && surface.Opacity == 1, "menu entrance settles");
            Assert.Equal(1, surface.RenderTransform.Value.M11, 3);
            Assert.Equal(1, surface.Opacity, 3);
        }
        finally
        {
            window.Close();
            SetInputDevice(previousInputDevice);
            AnimationConfig.SetReduceMotion(previousReducedMotion);
        }
    });

    [Fact]
    public void CancelledQueuedEntranceDoesNotRestartAndNewRequestStillOpens() => SharedStaTestRunner.RunAsync(async () =>
    {
        var previousInputDevice = InputManager.Current.MostRecentInputDevice;
        SetInputDevice(Mouse.PrimaryDevice);
        using var tray = new ClipboardTray();
        var surface = new Border { Width = 220, Height = 240 };
        var window = new BackgroundWindow { Content = surface, Width = 300, Height = 320 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var queue = typeof(ClipboardTray).GetMethod("QueueMenuEntrance", Private)!;
            var cancel = typeof(ClipboardTray).GetMethod("CancelMenuEntrance", Private)!;
            Func<bool> open = () => true;
            queue.Invoke(tray, [surface, null, open, true, false]);
            cancel.Invoke(tray, [surface]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(Transform.Identity, surface.RenderTransform);
            Assert.Equal(1, surface.Opacity);

            queue.Invoke(tray, [surface, null, open, true, false]);
            cancel.Invoke(tray, [surface]);
            queue.Invoke(tray, [surface, null, open, true, false]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            Assert.Equal(1, surface.RenderTransform.Value.M11, 3);
            Assert.Equal(1, surface.Opacity, 3);
            var pending = (System.Collections.IDictionary)typeof(ClipboardTray).GetField("_pendingMenuEntrances", Private)!.GetValue(tray)!;
            Assert.Empty(pending);
        }
        finally { window.Close(); SetInputDevice(previousInputDevice); }
    });

    [Fact]
    public void MenuItemBrightensSmoothlyAndSettlesAfterRapidHighlightChanges() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var item = new MenuItem { Header = "Copy", Style = (Style)tray.FindResource(typeof(MenuItem)) };
        var window = new BackgroundWindow { Content = item, Width = 240, Height = 60 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            item.ApplyTemplate();
            var content = (Grid)item.Template.FindName("MenuContent", item);
            var row = (Border)item.Template.FindName("MenuRow", item);
            double idleOpacity = content.Opacity;
            Assert.InRange(idleOpacity, 0.6, 0.85);
            var highlighted = typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!;
            highlighted.SetValue(item, true);
            Assert.True(content.HasAnimatedProperties);
            Assert.True(((SolidColorBrush)row.Background).HasAnimatedProperties);
            await Task.Delay(50);
            Assert.InRange(content.Opacity, idleOpacity + 0.01, 0.999);
            highlighted.SetValue(item, false);
            await Task.Delay(30);
            highlighted.SetValue(item, true);
            await Task.Delay(220);
            Assert.Equal(1, content.Opacity, 3);
            highlighted.SetValue(item, false);
            await Task.Delay(220);
            Assert.Equal(idleOpacity, content.Opacity, 3);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previousReducedMotion); }
    });

    private static void SetInputDevice(InputDevice inputDevice)
    {
        // Set WPF's per-dispatcher input state without activating a test window
        // or sending keyboard/mouse input to the user's desktop.
        typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!
            .SetValue(InputManager.Current, inputDevice);
    }

    private static double Angle(Button button)
    {
        var visual = (Grid)button.Template.FindName("RootGrid", button);
        var matrix = visual.RenderTransform.Value;
        return Math.Atan2(matrix.M12, matrix.M11) * 180 / Math.PI;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
