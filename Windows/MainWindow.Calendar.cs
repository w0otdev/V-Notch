using System;
using System.Windows;
using System.Windows.Controls;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private CalendarPresenter? _calendarPresenter;

    internal void InitializeCalendarPresenter()
    {
        if (_calendarPresenter != null) return;

        var refs = new CalendarViewRefs
        {
            WeekDaysPanel = WeekDaysPanel,
            WeekNumbers = WeekNumbers,
            CalendarStripTranslate = CalendarStripTranslate,
            CalendarHighlightTranslate = CalendarHighlightTranslate,
            CalendarHighlightScale = CalendarHighlightScale,
            MonthText = MonthText,
            MonthTextScale = MonthTextScale,
            MonthTextTranslate = MonthTextTranslate,
            EventText = EventText,
            BatterySection = BatterySection,
            SettingsButton = SettingsButton,
            GreetingSection = GreetingSection,
            CalendarGreetingContextBlur = CalendarGreetingContextBlur,
            CalendarWidgetScale = CalendarWidgetScale,
            CalendarWidgetTranslate = CalendarWidgetTranslate,
            SmallTextStyle = (Style)FindResource("SmallText"),
            TitleTextStyle = (Style)FindResource("TitleText"),
            BrushBlack = _brushBlack,
            BrushWhite = VNotch.Services.UiPalette.PrimaryBrush,
            BrushTransparent = _brushTransparent,
            SettingsProvider = () => _settings,
            IsNonCalendarWidgetMode = () => IsNonCalendarWidgetMode,
            OnCalendarTick = now =>
            {
                if (_clockViewCalendarBuilt)
                {
                    UpdateClockViewCalendar(now);
                }
            }
        };

        _calendarPresenter = new CalendarPresenter(_calendarModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeCalendarPresenter()
    {
        _calendarPresenter?.Dispose();
        _calendarPresenter = null;
    }

    private void CalendarWidget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EnsureCalendarFeatureLoaded();
        _calendarPresenter?.HandleMouseEnter();
    }

    private void CalendarWidget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EnsureCalendarFeatureLoaded();
        _calendarPresenter?.HandleMouseLeave();
    }

    private void CalendarWidget_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        EnsureCalendarFeatureLoaded();
        _calendarPresenter?.HandleMouseWheel(e);
    }

    private void UpdateCalendarInfo()
    {
        EnsureCalendarFeatureLoaded();
        EnsureActiveExpandedWidgetFeatureLoaded();
        _viewModel.UpdateCalendarInfo();
        _calendarPresenter?.UpdateCalendarInfo();
    }

    public void ResetCalendarScroll() => _calendarPresenter?.ResetCalendarScroll();

    private void ResetCalendarHoverFocusVisualState() => _calendarPresenter?.ResetHoverFocusVisualState();
}
