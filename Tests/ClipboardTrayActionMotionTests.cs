using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
public sealed class ClipboardTrayActionMotionTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("pin")]
    [InlineData("more")]
    public void PressingAnActionDoesNotFlashItsContents(string tag) => WithCard(async (tray, card, container) =>
    {
        var button = Descendants(container).OfType<Button>().Single(button => Equals(button.Tag, tag));
        var surface = (Border)button.Template.FindName("Surface", button);
        SetMouseOver(button, true);
        await WaitForHoverScaleAsync(button, "hover scale settles");
        double restingOpacity = surface.Opacity;
        SetPressed(button, true);
        Assert.Equal(restingOpacity, surface.Opacity, 3);
        // Hold the press for ~100 ms and verify the contents never flash on any frame.
        await TestWait.HoldsForAsync(() => Assert.Equal(restingOpacity, surface.Opacity, 3), TimeSpan.FromMilliseconds(100), "press holds without flashing");
        SetPressed(button, false);
        Assert.Equal(restingOpacity, surface.Opacity, 3);
    });

    [Theory]
    [InlineData("pin", false)]
    [InlineData("more", false)]
    [InlineData("pin", true)]
    [InlineData("more", true)]
    public void RepeatedPressesReturnToTheCurrentHoverState(string tag, bool reducedMotion) => WithCard(async (tray, card, container) =>
    {
        var button = Descendants(container).OfType<Button>().Single(button => Equals(button.Tag, tag));
        SetMouseOver(button, true);
        var scale = (ScaleTransform)button.Template.FindName("HoverScale", button);
        double hoveredScale = reducedMotion ? 1 : 1.15;
        await TestWait.UntilStableAsync(() => TestWait.Near(hoveredScale, scale.ScaleX), "hover scale settles");
        Assert.Equal(hoveredScale, scale.ScaleX, 3);
        for (int press = 0; press < 3; press++)
        {
            SetPressed(button, true);
            // Release mid-flight: wait until the press squish is visibly under way.
            if (reducedMotion) { await WpfFrameWaiter.NextAsync(); await WpfFrameWaiter.NextAsync(); }
            else await WpfFrameWaiter.UntilAsync(() => scale.ScaleX < hoveredScale - 0.01, "press squish under way");
            double pressedScale = scale.ScaleX;
            SetPressed(button, false);
            Assert.InRange(Math.Abs(scale.ScaleX - pressedScale), 0, 0.001);
            await TestWait.UntilStableAsync(() => TestWait.Near(hoveredScale, scale.ScaleX) && TestWait.Near(hoveredScale, scale.ScaleY), "release returns to hover scale");
            Assert.Equal(hoveredScale, scale.ScaleX, 3);
            Assert.Equal(hoveredScale, scale.ScaleY, 3);
        }
        SetMouseOver(button, false);
        await TestWait.UntilStableAsync(() => TestWait.Near(1, scale.ScaleX), "hover exit restores unit scale");
        Assert.Equal(1, scale.ScaleX, 3);
    }, reducedMotion);

    [Fact]
    public void PinAndMenuChangesKeepTheActionPillVisibleDuringInteraction() => WithCard(async (tray, card, container) =>
    {
        var actions = Descendants(container).OfType<Border>().Single(border => border.Name == "CardActions");
        SetMouseOver(container, true);
        await TestWait.UntilStableAsync(() => actions.Opacity > 0.999, "action pill fades in on hover");
        Assert.True(actions.Opacity > 0.999, $"Initial pill opacity={actions.Opacity}, target={TrayMotion.GetOpacity(actions)}, hover={container.IsMouseOver}, pinned={card.Entry.IsPinned}");
        for (int action = 0; action < 3; action++)
        {
            card.UpdateEntry(card.Entry with { IsPinned = !card.Entry.IsPinned });
            card.IsMenuOpen = true;
            SetMouseOver(container, false);
            for (int frame = 0; frame < 6; frame++)
            {
                await WpfFrameWaiter.NextAsync();
                Assert.True(actions.Opacity > 0.999, $"Action {action}, frame {frame}: pill opacity={actions.Opacity}, target={TrayMotion.GetOpacity(actions)}, hover={container.IsMouseOver}, pinned={card.Entry.IsPinned}, menu={card.IsMenuOpen}");
                Assert.True(actions.IsHitTestVisible);
            }
            SetMouseOver(container, true);
            card.IsMenuOpen = false;
            await WpfFrameWaiter.NextAsync();
            Assert.Equal(1, actions.Opacity, 3);
        }
    });

    [Fact]
    public void PopupFocusTransferKeepsTheActionsVisible() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-action-focus-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        ContextMenu? menu = null;
        try
        {
            await store.InitializeAsync();
            await store.ImportAsync(new ClipboardCapture { Text = "Popup focus transfer" });
            tray.Attach(controller);
            window.Show();
            await (Task)typeof(ClipboardTray).GetMethod("RefreshAsync", Private)!.Invoke(tray, null)!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)tray.FindName("Cards");
            var card = Assert.IsType<ClipboardCardViewModel>(list.Items[0]);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
            var actions = Descendants(container).OfType<Border>().Single(border => border.Name == "CardActions");
            var more = Descendants(container).OfType<Button>().Single(button => Equals(button.Tag, "more"));
            SetMouseOver(container, true);
            await TestWait.UntilStableAsync(() => TestWait.Near(1, actions.Opacity), "action pill fades in on hover");

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu = Descendants(container).OfType<Border>().Single(border => Math.Abs(border.Width - 210) < 0.1).ContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            // Popup Opened arrives later; the click must already hold the pill.
            Assert.True(card.IsMenuOpen);
            SetMouseOver(container, false);
            for (int frame = 0; frame < 8; frame++)
            {
                await WpfFrameWaiter.NextAsync();
                Assert.Equal(1, actions.Opacity, 3);
            }
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(menu.IsOpen);
            await WpfFrameWaiter.UntilAsync(() => !card.IsMenuOpen && actions.Opacity == 0, "action pill dismissal");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            window.Close();
        }
    });

    [Fact]
    public void LeavingWhilePressedAndReturningDuringReleaseDoesNotOvershoot() => WithCard(async (tray, card, container) =>
    {
        var button = Descendants(container).OfType<Button>().Single(button => Equals(button.Tag, "pin"));
        var scale = (ScaleTransform)button.Template.FindName("HoverScale", button);
        SetMouseOver(button, true);
        await WaitForHoverScaleAsync(button, "hover scale settles");
        SetPressed(button, true);
        // Interrupt the press mid-flight (~35 ms in): wait until the squish is visibly under way.
        await WpfFrameWaiter.UntilAsync(() => scale.ScaleX < 1.14, "press squish under way");
        SetMouseOver(button, false);
        SetPressed(button, false);
        // Return during the release (~35 ms, two frames) so hover retargets an in-flight clock.
        await WpfFrameWaiter.NextAsync();
        await WpfFrameWaiter.NextAsync();
        SetMouseOver(button, true);
        for (int frame = 0; frame < 12; frame++)
        {
            await WpfFrameWaiter.NextAsync();
            Assert.InRange(scale.ScaleX, 0.88, 1.15);
            Assert.Equal(scale.ScaleX, scale.ScaleY, 3);
        }
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(scale.ScaleX - 1.15) < 0.0001, "hover after interrupted release");
    });

    private static void WithCard(Func<ClipboardTray, ClipboardCardViewModel, ListBoxItem, Task> action, bool reducedMotion = false)
        => SharedStaTestRunner.RunAsync(async () =>
        {
            bool previousReducedMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(reducedMotion);
            using var tray = new ClipboardTray();
            var card = new ClipboardCardViewModel(new ClipboardEntry { Title = "Action motion", Preview = "Example", Kind = ClipboardKind.Text, IsPinned = true });
            var cards = (ObservableCollection<ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", Private)!.GetValue(tray)!;
            cards.Add(card);
            var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var list = (ListBox)tray.FindName("Cards");
                var container = await WaitForCardAsync(list, card);
                await action(tray, card, container);
            }
            finally { window.Close(); AnimationConfig.SetReduceMotion(previousReducedMotion); }
        });

    // Realized container whose action buttons have templates and whose pinned action pill has settled.
    private static async Task<ListBoxItem> WaitForCardAsync(ListBox list, ClipboardCardViewModel card)
    {
        ListBoxItem? container = null;
        await WpfFrameWaiter.UntilAsync(() =>
        {
            container = list.ItemContainerGenerator.ContainerFromItem(card) as ListBoxItem;
            return container is { ActualWidth: > 0 }
                && Descendants(container).OfType<Button>().Count(button => button.Tag is "pin" or "more") == 2
                && Descendants(container).OfType<Border>().Any(border => border.Name == "CardActions");
        }, "card container realized");
        var actions = Descendants(container!).OfType<Border>().Single(border => border.Name == "CardActions");
        await TestWait.UntilStableAsync(() => TestWait.Near(TrayMotion.GetOpacity(actions), actions.Opacity), "action pill settles");
        return container!;
    }

    // Hover scale lands on the template's TrayMotion target (1 under reduced motion).
    private static Task WaitForHoverScaleAsync(Button button, string description)
    {
        var root = (FrameworkElement)button.Template.FindName("RootGrid", button);
        var scale = (ScaleTransform)button.Template.FindName("HoverScale", button);
        return TestWait.UntilStableAsync(() =>
        {
            // TrayMotion snaps to 1 under reduced motion; otherwise wait for the hover target (1.15).
            double target = AnimationConfig.ReduceMotion ? 1 : TrayMotion.GetScale(root);
            return (AnimationConfig.ReduceMotion || target > 1) && TestWait.Near(target, scale.ScaleX);
        }, description);
    }

    private static void SetPressed(Button button, bool value)
        => typeof(Button).GetProperty(nameof(Button.IsPressed))!.SetValue(button, value);

    private static void SetMouseOver(UIElement element, bool value)
    {
        // Exercise WPF's template states without moving the user's pointer.
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", Private)!.GetValue(null)!;
        element.SetValue(key, value);
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
