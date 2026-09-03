using System;
using System.Windows;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    public void ApplyShelfWidgetMode()
    {
        if (SecondaryContent == null || SecondaryLeftCol == null || SecondaryRightCol == null) return;

        string mode = (_settings.ShelfWidget ?? "camera").ToLowerInvariant();

        switch (mode)
        {
            case "sysmon":
                SecondaryLeftCol.Width = new GridLength(1, GridUnitType.Star);
                SecondaryRightCol.Width = new GridLength(3, GridUnitType.Star);
                if (CameraSection != null) CameraSection.Visibility = Visibility.Collapsed;
                if (ShelfWeatherSection != null) ShelfWeatherSection.Visibility = Visibility.Collapsed;
                if (ShelfClockSection != null) ShelfClockSection.Visibility = Visibility.Collapsed;
                if (ShelfSysMonSection != null) ShelfSysMonSection.Visibility = Visibility.Visible;
                RefreshShelfSysMonData();
                break;

            case "weather":
                SecondaryLeftCol.Width = new GridLength(1, GridUnitType.Star);
                SecondaryRightCol.Width = new GridLength(3, GridUnitType.Star);
                if (CameraSection != null) CameraSection.Visibility = Visibility.Collapsed;
                if (ShelfSysMonSection != null) ShelfSysMonSection.Visibility = Visibility.Collapsed;
                if (ShelfClockSection != null) ShelfClockSection.Visibility = Visibility.Collapsed;
                if (ShelfWeatherSection != null) ShelfWeatherSection.Visibility = Visibility.Visible;
                RefreshShelfWeatherData();
                break;

            case "clock":
                SecondaryLeftCol.Width = new GridLength(1, GridUnitType.Star);
                SecondaryRightCol.Width = new GridLength(3, GridUnitType.Star);
                if (CameraSection != null) CameraSection.Visibility = Visibility.Collapsed;
                if (ShelfSysMonSection != null) ShelfSysMonSection.Visibility = Visibility.Collapsed;
                if (ShelfWeatherSection != null) ShelfWeatherSection.Visibility = Visibility.Collapsed;
                if (ShelfClockSection != null) ShelfClockSection.Visibility = Visibility.Visible;
                RefreshShelfClockData();
                break;

            case "none":
                SecondaryLeftCol.Width = new GridLength(0);
                SecondaryRightCol.Width = new GridLength(1, GridUnitType.Star);
                if (CameraSection != null) CameraSection.Visibility = Visibility.Collapsed;
                if (ShelfSysMonSection != null) ShelfSysMonSection.Visibility = Visibility.Collapsed;
                if (ShelfWeatherSection != null) ShelfWeatherSection.Visibility = Visibility.Collapsed;
                if (ShelfClockSection != null) ShelfClockSection.Visibility = Visibility.Collapsed;
                break;

            case "camera":
            default:
                SecondaryLeftCol.Width = new GridLength(1, GridUnitType.Star);
                SecondaryRightCol.Width = new GridLength(3, GridUnitType.Star);
                if (ShelfSysMonSection != null) ShelfSysMonSection.Visibility = Visibility.Collapsed;
                if (ShelfWeatherSection != null) ShelfWeatherSection.Visibility = Visibility.Collapsed;
                if (ShelfClockSection != null) ShelfClockSection.Visibility = Visibility.Collapsed;
                if (CameraSection != null) CameraSection.Visibility = Visibility.Visible;
                break;
        }
    }

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

            case "analog":
            default:
                if (ClockViewDigitalClock != null) ClockViewDigitalClock.Visibility = Visibility.Collapsed;
                if (ClockViewWordClock != null) ClockViewWordClock.Visibility = Visibility.Collapsed;
                ClockViewClock.Visibility = Visibility.Visible;
                break;
        }
    }

    private void RefreshShelfSysMonData()
    {
        if (ShelfSysMonSection == null || ShelfSysMonSection.Visibility != Visibility.Visible) return;

        // Populate from existing stats if SysMonCpuValueText has value
        if (SysMonCpuValueText != null && ShelfSysMonCpuText != null)
        {
            ShelfSysMonCpuText.Text = SysMonCpuValueText.Text;
        }
        if (SysMonRamValueText != null && ShelfSysMonRamText != null)
        {
            ShelfSysMonRamText.Text = SysMonRamValueText.Text;
        }
        if (SysMonNetDownText != null && ShelfSysMonNetDownText != null)
        {
            ShelfSysMonNetDownText.Text = $"↓ {SysMonNetDownText.Text}";
        }
        if (SysMonNetUpText != null && ShelfSysMonNetUpText != null)
        {
            ShelfSysMonNetUpText.Text = $"↑ {SysMonNetUpText.Text}";
        }
    }

    private void RefreshShelfWeatherData()
    {
        if (ShelfWeatherSection == null || ShelfWeatherSection.Visibility != Visibility.Visible) return;

        if (WeatherTempText != null && ShelfWeatherTempText != null)
        {
            ShelfWeatherTempText.Text = string.IsNullOrWhiteSpace(WeatherTempText.Text) ? "--°" : WeatherTempText.Text;
        }
        if (WeatherConditionText != null && ShelfWeatherDescText != null)
        {
            ShelfWeatherDescText.Text = string.IsNullOrWhiteSpace(WeatherConditionText.Text) ? "Weather" : WeatherConditionText.Text;
        }
        if (ShelfWeatherCityText != null)
        {
            ShelfWeatherCityText.Text = string.IsNullOrWhiteSpace(_settings.ManualCity) ? "Local" : _settings.ManualCity;
        }
    }

    private void RefreshShelfClockData()
    {
        if (ShelfClockSection == null || ShelfClockSection.Visibility != Visibility.Visible) return;

        var now = DateTime.Now;
        if (ShelfClockTimeText != null)
        {
            ShelfClockTimeText.Text = now.ToString("HH:mm");
        }
        if (ShelfClockDateText != null)
        {
            ShelfClockDateText.Text = now.ToString("ddd, MMM d");
        }
    }
}
