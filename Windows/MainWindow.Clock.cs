using System;
using System.Windows.Input;

namespace VNotch;

public partial class MainWindow
{

    #region Expanded Widget mode predicates (read by other partials)

    private bool IsClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "clock", StringComparison.OrdinalIgnoreCase);

    private bool IsWordClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "wordclock", StringComparison.OrdinalIgnoreCase);

    private bool IsDigitalClockWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "digitalclock", StringComparison.OrdinalIgnoreCase);

    private bool IsWeatherWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "weather", StringComparison.OrdinalIgnoreCase);

    private bool IsSystemMonitorWidgetMode =>
        string.Equals(_settings.ExpandedWidget, "sysmon", StringComparison.OrdinalIgnoreCase);

    private bool IsAnyClockWidgetMode => IsClockWidgetMode || IsWordClockWidgetMode || IsDigitalClockWidgetMode;

    private bool IsNonCalendarWidgetMode => IsAnyClockWidgetMode || IsWeatherWidgetMode || IsSystemMonitorWidgetMode;

    private void ApplyExpandedWidgetMode()
    {
        if (_clockWidgetPresenter == null) InitializeClockWidgetPresenter();
        _clockWidgetPresenter?.ApplyExpandedWidgetMode();
        EnsureActiveExpandedWidgetFeatureLoaded();
    }

    #endregion

    #region Drag-to-switch widget (XAML-bound handlers → presenter)

    private void CalendarWidget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _clockWidgetPresenter?.OnCalendarWidgetMouseLeftButtonDown(e);

    private void CalendarWidget_MouseMoveDrag(object sender, MouseEventArgs e)
    {
        _clockWidgetPresenter?.OnCalendarWidgetMouseMoveDrag(e);
        EnsureActiveExpandedWidgetFeatureLoaded();
    }

    private void CalendarWidget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _clockWidgetPresenter?.OnCalendarWidgetMouseLeftButtonUp(e);
        EnsureActiveExpandedWidgetFeatureLoaded();
    }

    #endregion
}
