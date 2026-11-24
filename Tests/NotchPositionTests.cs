using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchPositionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void DraggingWithoutDebugModeMovesTheWindowAndHoverZoneAndSavesOnRelease() =>
        SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false, settings =>
        {
            settings.EnableLocalOnlyMode = true;
            settings.AllowNotchDragging = true;
        });
        var window = fixture.Window;
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.ActualWidth > 0, "notch layout", ct);
        var hwnd = new WindowInteropHelper(window).Handle;
        Assert.True(Win32Interop.GetWindowRect(hwnd, out var before));
        // Supply physical motion coordinates without moving the user's real pointer.
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        window.NotchWrapper.RaiseEvent(press);
        Assert.True(press.Handled);
        var start = window.NotchWrapper.PointToScreen(press.GetPosition(window.NotchWrapper));
        window.UpdateNotchDrag(new Point(start.X + 2, start.Y));
        Assert.True(Win32Interop.GetWindowRect(hwnd, out var withinThreshold));
        Assert.Equal(before.Left, withinThreshold.Left);
        window.UpdateNotchDrag(new Point(start.X + 60, start.Y + 90));
        window.NotchWrapper.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.False(window.NotchWrapper.IsMouseCaptured);
        Assert.True(Win32Interop.GetWindowRect(hwnd, out var moved));
        Assert.Equal(before.Left + 60, moved.Left);
        Assert.Equal(before.Top + 90, moved.Top);
        int center = moved.Left + (moved.Right - moved.Left) / 2;
        var manager = (NotchManager)typeof(MainWindow).GetField("_notchManager", Private)!.GetValue(window)!;
        Assert.True(manager.HoverService.IsPointOverNotch(new Point(center, moved.Top + 1)));
        Assert.False(manager.HoverService.IsPointOverNotch(new Point(center, before.Top + 1)));
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var expected = NotchPosition.FromAnchor(screen.Bounds, new System.Drawing.Point(center, moved.Top));
        var store = (ISettingsService)typeof(MainWindow).GetField("_settingsService", Private)!.GetValue(window)!;
        await WpfFrameWaiter.UntilAsync(() => store.Load().CustomNotchPosition == expected, "drop saved", ct);
    });

    [Fact]
    public void LockedPlacementRestoresAtStartupAndTrayResetClearsIt() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var position = new NotchPosition(0.25, 0.45);
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false, settings =>
        {
            settings.EnableLocalOnlyMode = true;
            settings.AllowNotchDragging = false;
            settings.CustomNotchPosition = position;
        });
        var window = fixture.Window;
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.ActualWidth > 0, "notch layout", ct);

        var hwnd = new WindowInteropHelper(window).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        Assert.True(Win32Interop.GetWindowRect(hwnd, out var placed));
        Assert.InRange(placed.Left + (placed.Right - placed.Left) / 2,
            screen.Left + screen.Width / 4 - 1, screen.Left + screen.Width / 4 + 1);
        Assert.Equal(screen.Top + (int)Math.Round(screen.Height * 0.45), placed.Top);

        var trayReset = typeof(MainWindow).GetMethod("ResetPosition_Click", Private)!;
        trayReset.Invoke(window, [window, new RoutedEventArgs()]);
        Assert.True(Win32Interop.GetWindowRect(hwnd, out var reset));
        Assert.Equal(screen.Top, reset.Top);
        Assert.InRange(reset.Left + (reset.Right - reset.Left) / 2,
            screen.Left + screen.Width / 2 - 1, screen.Left + screen.Width / 2 + 1);
        var store = (ISettingsService)typeof(MainWindow).GetField("_settingsService", Private)!.GetValue(window)!;
        await WpfFrameWaiter.UntilAsync(() => store.Load().CustomNotchPosition == null, "reset saved", ct);
    });
}
