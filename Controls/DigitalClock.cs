using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

public class DigitalClock : TextBlock
{
    private readonly UiScheduledTimer _timer;
    private bool _isRunning;
    private int _lastRenderedMinute = -1;

    public static readonly DependencyProperty IsStackedProperty =
        DependencyProperty.Register(
            nameof(IsStacked),
            typeof(bool),
            typeof(DigitalClock),
            new PropertyMetadata(false, OnIsStackedChanged));

    public bool IsStacked
    {
        get => (bool)GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }

    private static void OnIsStackedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DigitalClock clock)
        {
            clock._lastRenderedMinute = -1;
            clock.UpdateText();
        }
    }

    public DigitalClock()
    {
        TextAlignment = TextAlignment.Center;
        _timer = new UiScheduledTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => UpdateText();

        Loaded += (_, _) => UpdateRunningState();
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) => UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        if (IsVisible)
            StartTimer();
        else
            StopTimer();
    }

    private void StartTimer()
    {
        if (_isRunning) return;
        _isRunning = true;
        _timer.Start();
        _lastRenderedMinute = -1;
        UpdateText();
    }

    private void StopTimer()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer.Stop();
    }

    private void UpdateText()
    {
        DateTime now = DateTime.Now;
        int minuteKey = now.Hour * 60 + now.Minute;
        if (minuteKey == _lastRenderedMinute) return;
        _lastRenderedMinute = minuteKey;

        if (IsStacked)
        {
            Text = $"{now:HH}\n{now:mm}";
        }
        else
        {
            Text = now.ToString("HH:mm");
        }
    }
}
