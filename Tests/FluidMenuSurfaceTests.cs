using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class FluidMenuSurfaceTests
{
    [Fact]
    public void MainMenuAndSubmenuMorphIndependentlyAndReleaseTheirRenderHandlers() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        bool? systemMotion = AnimationConfig.ClientAreaAnimationOverride;
        var input = InputManager.Current.MostRecentInputDevice;
        AnimationConfig.SetReduceMotion(false);
        AnimationConfig.ClientAreaAnimationOverride = true;
        SetInputDevice(Mouse.PrimaryDevice);
        using var tray = new ClipboardTray();
        var style = (Style)tray.FindResource(typeof(MenuItem));
        MenuItem Item(string label) => new() { Header = label, Style = style };
        var copy = Item("Copy");
        var pin = Item("Pin");
        var groups = Item("Categories");
        var ideas = Item("Ideas");
        var email = Item("Email");
        groups.Items.Add(ideas);
        groups.Items.Add(email);
        var delete = Item("Delete");
        delete.Tag = "danger";
        var menu = new ContextMenu { Style = (Style)tray.FindResource(typeof(ContextMenu)), PlacementTarget = tray, StaysOpen = true };
        foreach (var item in new[] { copy, pin, groups, delete }) menu.Items.Add(item);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        FluidMenuSurface? child = null;
        try
        {
            window.Show();
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var root = Assert.IsType<FluidMenuSurface>(menu.Template.FindName("MenuHighlightHost", menu));
            var copyRow = Row(copy);
            Highlight(copy, true);
            await WpfFrameWaiter.UntilAsync(() => !root.IsAnimating && root.Highlight.Opacity == 1,
                "main menu reveals its shared highlight", ct);
            Assert.Same(copyRow, root.ActiveRow);
            var before = Bounds(root);
            Highlight(copy, false);
            Highlight(pin, true);
            Assert.Equal(before.Y, Bounds(root).Y, 6);
            Assert.Equal(before.Height, Bounds(root).Height, 6);
            Assert.Equal(1, root.Highlight.Opacity);
            root.AdvanceMotion(.03);
            Assert.True(root.Highlight.RenderTransform.Value.M22 > 1.01);
            object shape = typeof(FluidMenuSurface).GetField("_shape", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
            Highlight(pin, false);
            Highlight(copy, true);
            Assert.Equal(shape, typeof(FluidMenuSurface).GetField("_shape", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root));

            Highlight(copy, false);
            Highlight(groups, true);
            groups.IsSubmenuOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            child = Assert.IsType<FluidMenuSurface>(groups.Template.FindName("SubmenuHighlightHost", groups));
            await WpfFrameWaiter.UntilAsync(() => child.IsVisible && !root.IsAnimating,
                "parent Categories highlight stays selected while the submenu opens", ct);
            root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Same(Row(groups), root.ActiveRow);
            Assert.Equal(1, root.Highlight.Opacity);
            Highlight(ideas, true);
            await WpfFrameWaiter.UntilAsync(() => !child.IsAnimating && child.Highlight.Opacity == 1,
                "submenu has its own shared highlight", ct);
            Highlight(ideas, false);
            Highlight(email, true);
            child.AdvanceMotion(.04);
            Assert.True(child.Highlight.RenderTransform.Value.M22 > 1.01);
            Assert.Equal(1, root.Highlight.RenderTransform.Value.M22);
            Assert.Same(Row(groups), root.ActiveRow);

            if (Environment.GetEnvironmentVariable("VNOTCH_FLUID_MENU_FRAMES") is string frames)
            {
                System.IO.Directory.CreateDirectory(frames);
                var mainSurface = Assert.IsType<Border>(menu.Template.FindName("MenuSurface", menu));
                var submenuSurface = Assert.IsType<Border>(groups.Template.FindName("SubmenuSurface", groups));
                var destinations = new[] { ideas, email, ideas, email };
                for (int frame = 0; frame < 80; frame++)
                {
                    if (frame % 20 == 0)
                    {
                        Highlight(ideas, false);
                        Highlight(email, false);
                        Highlight(destinations[frame / 20], true);
                    }
                    await WpfFrameWaiter.NextAsync(ct);
                    Capture(mainSurface, submenuSurface, System.IO.Path.Combine(frames, $"frame-{frame:000}.png"));
                }
            }

            groups.IsSubmenuOpen = false;
            Highlight(groups, false);
            Highlight(delete, true);
            await WpfFrameWaiter.UntilAsync(() => !root.IsAnimating, "destructive menu highlight settles", ct);
            var danger = Assert.IsType<SolidColorBrush>(root.Highlight.Background).Color;
            Assert.True(danger.R > danger.G && danger.R > danger.B);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(Row(delete).Background).Color);
            menu.IsOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(root.IsAnimating);
            Assert.False(child.IsAnimating);
            Assert.Equal(0, root.Highlight.Opacity);
            Assert.Equal(0, child.Highlight.Opacity);
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
            AnimationConfig.SetReduceMotion(reduced);
            AnimationConfig.ClientAreaAnimationOverride = systemMotion;
            SetInputDevice(input);
        }
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void KeyboardAndReducedMotionPlaceTheHighlightImmediately(bool reduced, bool keyboard) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        bool? systemMotion = AnimationConfig.ClientAreaAnimationOverride;
        var input = InputManager.Current.MostRecentInputDevice;
        AnimationConfig.SetReduceMotion(reduced);
        AnimationConfig.ClientAreaAnimationOverride = true;
        SetInputDevice(keyboard ? Keyboard.PrimaryDevice : Mouse.PrimaryDevice);
        var first = new Border { Height = 32, Margin = new Thickness(0, 1, 0, 1) };
        var second = new Border { Height = 44, Margin = new Thickness(0, 1, 0, 1) };
        var panel = new StackPanel { Children = { first, second } };
        var surface = new FluidMenuSurface { Children = { panel } };
        var window = new BackgroundWindow { Content = surface, Width = 240, Height = 120 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FluidMenuSurface.SetIsRowActive(first, true);
            FluidMenuSurface.SetIsRowActive(second, true);
            window.UpdateLayout();
            Assert.False(surface.IsAnimating);
            Assert.Same(second, surface.ActiveRow);
            Assert.Equal(second.TranslatePoint(new Point(), surface).Y, Bounds(surface).Y, 3);
            Assert.Equal(second.ActualHeight, Bounds(surface).Height, 3);
            Assert.Equal(1, surface.Highlight.Opacity);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previous);
            AnimationConfig.ClientAreaAnimationOverride = systemMotion;
            SetInputDevice(input);
        }
    });

    private static Border Row(MenuItem item) => Assert.IsType<Border>(item.Template.FindName("MenuRow", item));
    private static void Highlight(MenuItem item, bool value) => typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(item, value);
    private static Rect Bounds(FluidMenuSurface surface) => surface.Highlight.TransformToAncestor(surface).TransformBounds(new Rect(surface.Highlight.RenderSize));
    private static void SetInputDevice(InputDevice input) => typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!.SetValue(InputManager.Current, input);

    private static void Capture(FrameworkElement root, FrameworkElement child, string path)
    {
        double width = root.ActualWidth + child.ActualWidth + 34;
        double height = Math.Max(root.ActualHeight + 24, child.ActualHeight + 80);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(39, 47, 60)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(root), null, new Rect(12, 12, root.ActualWidth, root.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(child), null, new Rect(root.ActualWidth + 18, 64, child.ActualWidth, child.ActualHeight));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width * 1.5),
            (int)Math.Ceiling(height * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }
}
