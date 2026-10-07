using System;
using System.Windows;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{



    public void ApplyClockPageStyle()
    {
        if (ClockViewClock == null) return;

        string style = (_settings.ClockPageStyle ?? "analog").ToLowerInvariant();

        switch (style)
        {
            case "digital":
                ClockViewClock.Visibility = Visibility.Collapsed;
                if (ClockViewWordClock != null) ClockViewWordClock.Visibility = Visibility.Collapsed;
                if (ClockViewDigitalClock != null)
                {
                    ClockViewDigitalClock.Visibility = Visibility.Visible;
                }
                break;

            case "wordclock":
                ClockViewClock.Visibility = Visibility.Collapsed;
                if (ClockViewDigitalClock != null) ClockViewDigitalClock.Visibility = Visibility.Collapsed;
                if (ClockViewWordClock != null)
                {
                    ClockViewWordClock.Visibility = Visibility.Visible;
                    ClockViewWordClock.RefreshLocalization();
                }
                break;

            default:
                if (ClockViewDigitalClock != null) ClockViewDigitalClock.Visibility = Visibility.Collapsed;
                if (ClockViewWordClock != null) ClockViewWordClock.Visibility = Visibility.Collapsed;
                ClockViewClock.Visibility = Visibility.Visible;
                break;
        }
    }


}
