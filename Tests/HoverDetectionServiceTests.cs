using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class HoverDetectionServiceTests
{
    [Fact]
    public void HandledWpfMouseEventsPublishScreenCoordinatesOnlyWhileStarted() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        using var service = new HoverDetectionService(inputWindow: window);
        try
        {
            window.Show();
            service.Start();
            Point? received = null;
            service.MousePositionChanged += (_, point) => received = point;
            var expected = window.PointToScreen(Mouse.GetPosition(window));
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.MouseMoveEvent,
                Handled = true
            });
            Assert.Equal(expected, received);

            service.Stop();
            received = null;
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Null(received);

            service.Dispose();
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Null(received);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HoverTransitionsKeepEnterAndLeaveDelays() => SharedStaTestRunner.Run(() =>
    {
        using var service = new HoverDetectionService();
        service.UpdateNotchBounds(-200, -100, 100, 30);
        service.Start();
        service.ResetHoverState();
        int enters = 0, leaves = 0;
        service.HoverEnter += (_, _) => enters++;
        service.HoverLeave += (_, _) => leaves++;
        var inside = new Point(-150, -90);
        var outside = new Point(300, 300);
        long start = Stopwatch.GetTimestamp();

        service.ProcessMousePosition(inside, At(start, 0));
        service.ProcessMousePosition(inside, At(start, 149));
        Assert.False(service.IsHovering);
        service.ProcessMousePosition(inside, At(start, 151));
        Assert.True(service.IsHovering);

        service.ProcessMousePosition(outside, At(start, 200));
        service.ProcessMousePosition(outside, At(start, 599));
        Assert.True(service.IsHovering);
        service.ProcessMousePosition(outside, At(start, 601));
        Assert.False(service.IsHovering);
        Assert.Equal(1, enters);
        Assert.Equal(1, leaves);
    });

    [Fact]
    public void CrossingBackCancelsPendingTransitions() => SharedStaTestRunner.Run(() =>
    {
        using var service = new HoverDetectionService();
        service.UpdateNotchBounds(100, 0, 100, 30);
        service.Start();
        service.ResetHoverState();
        var inside = new Point(150, 10);
        var outside = new Point(400, 400);
        long start = Stopwatch.GetTimestamp();

        service.ProcessMousePosition(inside, At(start, 0));
        service.ProcessMousePosition(outside, At(start, 100));
        service.ProcessMousePosition(inside, At(start, 200));
        service.ProcessMousePosition(inside, At(start, 349));
        Assert.False(service.IsHovering);
        service.ProcessMousePosition(inside, At(start, 351));
        Assert.True(service.IsHovering);

        service.ProcessMousePosition(outside, At(start, 400));
        service.ProcessMousePosition(inside, At(start, 799));
        service.ProcessMousePosition(outside, At(start, 1000));
        service.ProcessMousePosition(outside, At(start, 1399));
        Assert.True(service.IsHovering);
        service.ProcessMousePosition(outside, At(start, 1401));
        Assert.False(service.IsHovering);
    });

    [Fact]
    public void StoppedAndDisposedServicesIgnoreMouseUpdates() => SharedStaTestRunner.Run(() =>
    {
        using var service = new HoverDetectionService();
        service.UpdateNotchBounds(100, 0, 100, 30);
        service.Start();
        service.ResetHoverState();
        int updates = 0;
        service.MousePositionChanged += (_, _) => updates++;
        long start = Stopwatch.GetTimestamp();
        var inside = new Point(150, 10);

        service.ProcessMousePosition(inside, start);
        service.Stop();
        service.ProcessMousePosition(inside, At(start, 500));
        Assert.False(service.IsHovering);
        Assert.Equal(1, updates);

        service.Dispose();
        service.Start();
        service.ProcessMousePosition(inside, At(start, 1000));
        Assert.Equal(1, updates);
    });

    private static long At(long start, int milliseconds) =>
        start + (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
}
