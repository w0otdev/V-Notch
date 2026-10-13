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
        await Task.Delay(250);
        double restingOpacity = surface.Opacity;
        SetPressed(button, true);
        Assert.Equal(restingOpacity, surface.Opacity, 3);
        await Task.Delay(100);
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
        await Task.Delay(250);
        var scale = (ScaleTransform)button.Template.FindName("HoverScale", button);
        double hoveredScale = reducedMotion ? 1 : 1.15;
        Assert.Equal(hoveredScale, scale.ScaleX, 3);
        for (int press = 0; press < 3; press++)
        {
            SetPressed(button, true);
            await Task.Delay(90);
            double pressedScale = scale.ScaleX;
            SetPressed(button, false);
            Assert.InRange(Math.Abs(scale.ScaleX - pressedScale), 0, 0.001);
            await Task.Delay(300);
            Assert.Equal(hoveredScale, scale.ScaleX, 3);
            Assert.Equal(hoveredScale, scale.ScaleY, 3);
        }
        SetMouseOver(button, false);
        await Task.Delay(250);
        Assert.Equal(1, scale.ScaleX, 3);
    }, reducedMotion);

    [Fact]
    public void PinAndMenuChangesKeepTheActionPillVisibleDuringInteraction() => WithCard(async (tray, card, container) =>
    {
        var actions = Descendants(container).OfType<Border>().Single(border => border.Name == "CardActions");
        SetMouseOver(container, true);
        await Task.Delay(250);
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
            await Task.Delay(250);

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu = Descendants(container).OfType<Border>().Single(border => border.Width == 210).ContextMenu;
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
        await Task.Delay(250);
        SetPressed(button, true);
        await Task.Delay(35);
        SetMouseOver(button, false);
        SetPressed(button, false);
        await Task.Delay(35);
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
                await Task.Delay(300);
                var list = (ListBox)tray.FindName("Cards");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
                await action(tray, card, container);
            }
            finally { window.Close(); AnimationConfig.SetReduceMotion(previousReducedMotion); }
        });

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
