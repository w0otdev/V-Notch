using System.Reflection;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CalendarInteractionSequenceTests
{
    [Theory]
    [InlineData(120, 4)]
    [InlineData(-120, 6)]
    [InlineData(1200, 0)]
    [InlineData(-1200, 10)]
    public void ScrollMovesHighlightAndResetReturnsToToday(int delta, int expectedIndex) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.ExpandedWidget = "calendar"; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        fixture.Window.InitializeCalendarPresenter();
        var presenter = (CalendarPresenter)typeof(MainWindow).GetField("_calendarPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
        presenter.UpdateCalendarInfo();
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent };
        presenter.HandleMouseWheel(args);
        Assert.True(args.Handled);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(fixture.Window.CalendarStripTranslate.X - CalendarScrollMath.GetStripXForIndex(expectedIndex)) < .01,
            "calendar scroll settles", ct);
        Assert.Equal(CalendarScrollMath.GetHighlightXForIndex(expectedIndex), fixture.Window.CalendarHighlightTranslate.X, 2);
        presenter.HandleMouseEnter();
        await WpfFrameWaiter.UntilAsync(() => fixture.Window.CalendarWidgetScale.ScaleX > 1.03, "calendar hover", ct);
        presenter.HandleMouseLeave();
        presenter.ResetCalendarScroll();
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(fixture.Window.CalendarStripTranslate.X - CalendarScrollMath.GetStripXForIndex(5)) < .01,
            "calendar reset", ct);
        presenter.ResetHoverFocusVisualState();
        Assert.Equal(1, fixture.Window.CalendarWidgetScale.ScaleX);
        Assert.Equal(0, fixture.Window.CalendarWidgetTranslate.Y);
        Assert.Equal(1, fixture.Window.BatterySection.Opacity);
        presenter.ResetCalendarScroll();
    });
}
