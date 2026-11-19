using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CalendarInteractionSequenceTests
{
    [Fact]
    public void AiWidgetHoverKeepsItsIconsFixedAndClearsPreviousCalendarMotion() => SharedStaTestRunner.RunAsync(async () =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.ExpandedWidget = "aiusage"; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var presenter = fixture.Window.InitializeCalendarPresenter();
        var scale = fixture.Window.CalendarWidgetScale;
        var offset = fixture.Window.CalendarWidgetTranslate;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.035, TimeSpan.FromMilliseconds(350)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.035, TimeSpan.FromMilliseconds(350)));
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(350)));
        presenter.HandleMouseEnter();
        Assert.False(scale.HasAnimatedProperties);
        Assert.False(offset.HasAnimatedProperties);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
        Assert.Equal(0, offset.Y);
        presenter.HandleMouseLeave();
        Assert.False(scale.HasAnimatedProperties);
        Assert.False(offset.HasAnimatedProperties);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(0, offset.Y);
    });

    [Fact]
    public void RenderedCellsAndHighlightMatchScrollGeometry() => SharedStaTestRunner.RunAsync(async () =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.ExpandedWidget = "calendar"; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.InitializeCalendarPresenter().UpdateCalendarInfo();
        Assert.Equal(CalendarScrollMath.TotalDays, window.WeekNumbers.Children.Count);
        Assert.Equal(CalendarScrollMath.TotalDays, window.WeekDaysPanel.Children.Count);
        Assert.Equal(CalendarScrollMath.HighlightDiameter, window.CalendarMainHighlight.Width);
        Assert.Equal(CalendarScrollMath.HighlightDiameter, window.CalendarMainHighlight.Height);
        for (int i = 0; i < CalendarScrollMath.TotalDays; i++)
        {
            var day = Assert.IsType<TextBlock>(window.WeekDaysPanel.Children[i]);
            var cell = Assert.IsType<Border>(window.WeekNumbers.Children[i]);
            Assert.False(string.IsNullOrWhiteSpace(day.Text));
            Assert.Equal(day.Width, cell.Width + cell.Margin.Left + cell.Margin.Right);
            Assert.Equal(i * day.Width + cell.Margin.Left, CalendarScrollMath.GetHighlightXForIndex(i));
            Assert.Equal(i, CalendarScrollMath.GetCenterIndexFromStripX(CalendarScrollMath.GetStripXForIndex(i)));
        }
        Assert.Equal(CalendarScrollMath.TodayIndex,
            CalendarScrollMath.GetCenterIndexFromStripX(window.CalendarStripTranslate.X));
    });

    [Theory]
    [InlineData(120, 4)]
    [InlineData(-120, 6)]
    [InlineData(1200, 0)]
    [InlineData(-1200, 10)]
    public void ScrollMovesHighlightAndResetReturnsToToday(int delta, int expectedIndex) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.ExpandedWidget = "calendar"; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var presenter = fixture.Window.InitializeCalendarPresenter();
        presenter.UpdateCalendarInfo();
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent };
        presenter.HandleMouseWheel(args);
        Assert.True(args.Handled);
        await WpfFrameWaiter.UntilAsync(() =>
            Math.Abs(fixture.Window.CalendarStripTranslate.X - CalendarScrollMath.GetStripXForIndex(expectedIndex)) < .01 &&
            Math.Abs(fixture.Window.CalendarHighlightTranslate.X - CalendarScrollMath.GetHighlightXForIndex(expectedIndex)) < .02,
            "calendar scroll settles", ct);
        Assert.Equal(CalendarScrollMath.GetHighlightXForIndex(expectedIndex), fixture.Window.CalendarHighlightTranslate.X, 1);
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
