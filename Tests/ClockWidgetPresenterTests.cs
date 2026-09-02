using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Presenters;
using Xunit;

namespace VNotch.Tests;

public sealed class ClockWidgetPresenterTests
{
    private sealed class FakeClockHost : IClockWidgetHost
    {
        public NotchSettings Settings { get; set; } = new();
        public void SaveSettings() { }

        public bool IsAnimating { get; set; }
        public bool IsSecondaryView { get; set; }
        public bool IsExpanded { get; set; } = true;
        public bool IsTimerView { get; set; }
        public bool IsLyricsActive { get; set; }
        public int TransitionGeneration { get; set; } = 1;

        public Brush TransparentBrush => Brushes.Transparent;
        public Brush WhiteBrush => Brushes.White;

        public void SwitchToTimerView() { }
        public void CollapseNotch() { }

        public IntPtr Hwnd => IntPtr.Zero;
        public int FixedX => 0;
        public int FixedY => 0;
        public int WindowWidth => 600;
        public int WindowHeight { get; set; } = 300;
        public double ExpandedHeight => 240;

        public Window Window { get; set; } = null!;
    }

    [Fact]
    public void ApplyExpandedWidgetMode_DigitalClock_ShowsGreetingSectionWithCenteredText()
    {
        RunSta(() =>
        {
            var host = new FakeClockHost();
            host.Settings.ExpandedWidget = "digitalclock";
            host.IsLyricsActive = false;

            var window = new Window();
            host.Window = window;

            var clockWidget = new Grid();
            var digitalClockWidget = new Grid();
            var calendarStrip = new Grid();
            var greetingSection = new Grid { Opacity = 0 };
            var eventText = new TextBlock();
            var notchBorder = new Border();

            var refs = new ClockWidgetViewRefs
            {
                ClockWidget = clockWidget,
                DigitalClockWidget = digitalClockWidget,
                CalendarStripContainer = calendarStrip,
                GreetingSection = greetingSection,
                EventText = eventText,
                CalendarWidget = new Border(),
                NotchBorder = notchBorder,
                Window = window
            };

            using var presenter = new ClockWidgetPresenter(host, refs);
            presenter.ApplyExpandedWidgetMode();

            Assert.Equal(Visibility.Visible, digitalClockWidget.Visibility);
            Assert.Equal(Visibility.Collapsed, clockWidget.Visibility);
            Assert.Equal(Visibility.Collapsed, calendarStrip.Visibility);
            Assert.Equal(Visibility.Visible, greetingSection.Visibility);
            Assert.Equal(1.0, greetingSection.Opacity);
            Assert.Equal(HorizontalAlignment.Center, eventText.HorizontalAlignment);
        });
    }

    [Fact]
    public void ApplyExpandedWidgetMode_DigitalClock_SuppressesGreetingWhenLyricsActive()
    {
        RunSta(() =>
        {
            var host = new FakeClockHost();
            host.Settings.ExpandedWidget = "digitalclock";
            host.IsLyricsActive = true;

            var window = new Window();
            host.Window = window;

            var greetingSection = new Grid();
            var refs = new ClockWidgetViewRefs
            {
                ClockWidget = new Grid(),
                DigitalClockWidget = new Grid(),
                CalendarStripContainer = new Grid(),
                GreetingSection = greetingSection,
                CalendarWidget = new Border(),
                NotchBorder = new Border(),
                Window = window
            };

            using var presenter = new ClockWidgetPresenter(host, refs);
            presenter.ApplyExpandedWidgetMode();

            Assert.Equal(Visibility.Collapsed, greetingSection.Visibility);
        });
    }

    [Fact]
    public void ApplyExpandedWidgetMode_Calendar_ShowsGreetingSection()
    {
        RunSta(() =>
        {
            var host = new FakeClockHost();
            host.Settings.ExpandedWidget = "calendar";
            host.IsLyricsActive = false;

            var window = new Window();
            host.Window = window;

            var greetingSection = new Grid();
            var eventText = new TextBlock();
            var refs = new ClockWidgetViewRefs
            {
                ClockWidget = new Grid(),
                CalendarStripContainer = new Grid(),
                GreetingSection = greetingSection,
                EventText = eventText,
                CalendarWidget = new Border(),
                NotchBorder = new Border(),
                Window = window
            };

            using var presenter = new ClockWidgetPresenter(host, refs);
            presenter.ApplyExpandedWidgetMode();

            Assert.Equal(Visibility.Visible, greetingSection.Visibility);
            Assert.Equal(HorizontalAlignment.Left, eventText.HorizontalAlignment);
        });
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("wordclock")]
    [InlineData("weather")]
    [InlineData("sysmon")]
    public void ApplyExpandedWidgetMode_NonGreetingWidgets_HideGreetingSection(string widgetMode)
    {
        RunSta(() =>
        {
            var host = new FakeClockHost();
            host.Settings.ExpandedWidget = widgetMode;
            host.IsLyricsActive = false;

            var window = new Window();
            host.Window = window;

            var greetingSection = new Grid();
            var refs = new ClockWidgetViewRefs
            {
                ClockWidget = new Grid(),
                WordClockWidget = new Grid(),
                WeatherWidgetContent = new Grid(),
                SystemMonitorWidgetContent = new Grid(),
                CalendarStripContainer = new Grid(),
                GreetingSection = greetingSection,
                CalendarWidget = new Border(),
                NotchBorder = new Border(),
                Window = window
            };

            using var presenter = new ClockWidgetPresenter(host, refs);
            presenter.ApplyExpandedWidgetMode();

            Assert.Equal(Visibility.Collapsed, greetingSection.Visibility);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (failure != null) throw failure;
    }
}
