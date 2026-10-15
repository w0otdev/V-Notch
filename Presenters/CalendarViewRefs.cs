using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Models;

namespace VNotch.Presenters;

public sealed class CalendarViewRefs
{
    public required StackPanel WeekDaysPanel { get; init; }
    public required StackPanel WeekNumbers { get; init; }
    public required TranslateTransform CalendarStripTranslate { get; init; }
    public required TranslateTransform CalendarHighlightTranslate { get; init; }
    public required ScaleTransform CalendarHighlightScale { get; init; }

    public required TextBlock MonthText { get; init; }
    public TextBlock? DayText { get; init; }
    public required ScaleTransform MonthTextScale { get; init; }
    public required TranslateTransform MonthTextTranslate { get; init; }

    public required TextBlock EventText { get; init; }
    public required UIElement BatterySection { get; init; }
    public required UIElement SettingsButton { get; init; }
    public required UIElement GreetingSection { get; init; }
    public required BlurEffect CalendarGreetingContextBlur { get; init; }

    public required ScaleTransform CalendarWidgetScale { get; init; }
    public required TranslateTransform CalendarWidgetTranslate { get; init; }

    public required Style SmallTextStyle { get; init; }
    public required Style TitleTextStyle { get; init; }

    public required Brush BrushBlack { get; init; }
    public required Brush BrushWhite { get; init; }
    public required Brush BrushTransparent { get; init; }

    public required Func<NotchSettings> SettingsProvider { get; init; }
    public required Func<bool> IsNonCalendarWidgetMode { get; init; }

    public required Action<DateTime> OnCalendarTick { get; init; }
}
