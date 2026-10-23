using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VNotch.ViewModels;

public partial class TimerViewModel : ObservableObject
{
    private static readonly TimeSpan Minimum = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Maximum = TimeSpan.FromDays(7);

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*([hmsd])", RegexOptions.IgnoreCase)]
    private static partial Regex UnitTimeRegex();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private TimeSpan _duration = TimeSpan.FromMinutes(25);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private TimeSpan _remaining = TimeSpan.FromMinutes(25);

    [ObservableProperty]
    private bool _isRunning;

    private string _displayText = Format(TimeSpan.FromMinutes(25));
    public string DisplayText => _displayText;

    public double Progress => Duration <= TimeSpan.Zero ? 0 :
        1 - Math.Clamp(Remaining.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);

    partial void OnDurationChanged(TimeSpan value) => UpdateDisplayText();

    partial void OnRemainingChanged(TimeSpan value) => UpdateDisplayText();

    private void UpdateDisplayText()
    {
        string formatted = Format(Remaining);
        if (_displayText != formatted)
        {
            _displayText = formatted;
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public void Start()
    {
        if (Remaining <= TimeSpan.Zero) Remaining = Duration;
        IsRunning = true;
    }

    public void Pause() => IsRunning = false;

    public void Reset()
    {
        IsRunning = false;
        Remaining = Duration;
    }

    public bool Tick(TimeSpan elapsed)
    {
        if (!IsRunning || elapsed <= TimeSpan.Zero) return false;
        Remaining -= elapsed;
        if (Remaining > TimeSpan.Zero) return false;
        Remaining = TimeSpan.Zero;
        IsRunning = false;
        return true;
    }

    public bool Adjust(int direction)
    {
        if (IsRunning || direction == 0) return false;
        var step = Duration.TotalDays >= 1 ? TimeSpan.FromHours(1) :
            Duration.TotalHours >= 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
        var next = Duration + (direction > 0 ? step : -step);
        next = TimeSpan.FromTicks(Math.Clamp(next.Ticks, Minimum.Ticks, Maximum.Ticks));
        if (next == Duration) return false;
        Duration = next;
        Remaining = next;
        return true;
    }

    public bool SetCustomDuration(TimeSpan time)
    {
        var clamped = TimeSpan.FromTicks(Math.Clamp(time.Ticks, TimeSpan.FromSeconds(5).Ticks, Maximum.Ticks));
        IsRunning = false;
        Duration = clamped;
        Remaining = clamped;
        return true;
    }

    public bool TryParseCustomTime(string? input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();

        if (s.Contains(':'))
        {
            string[] parts = s.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int mins) && int.TryParse(parts[1], out int secs))
            {
                if (mins >= 0 && secs >= 0 && secs < 60)
                {
                    return TryFromSeconds((double)mins * 60 + secs, out result);
                }
            }
            else if (parts.Length == 3 && int.TryParse(parts[0], out int hrs) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int sec))
            {
                if (hrs >= 0 && m >= 0 && m < 60 && sec >= 0 && sec < 60)
                {
                    return TryFromSeconds((double)hrs * 3600 + (double)m * 60 + sec, out result);
                }
            }

            if (TimeSpan.TryParse(s, out TimeSpan ts) && ts > TimeSpan.Zero)
            {
                result = ts;
                return true;
            }
        }

        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
        {
            return TryFromSeconds(num * 60, out result);
        }

        try
        {
            double totalSeconds = 0;
            var matches = UnitTimeRegex().Matches(s);
            if (matches.Count > 0)
            {
                foreach (Match match in matches)
                {
                    double val = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    char unit = char.ToLowerInvariant(match.Groups[2].Value[0]);
                    switch (unit)
                    {
                        case 's': totalSeconds += val; break;
                        case 'm': totalSeconds += val * 60; break;
                        case 'h': totalSeconds += val * 3600; break;
                        case 'd': totalSeconds += val * 86400; break;
                    }
                }
                return TryFromSeconds(totalSeconds, out result);
            }
        }
        catch { }

        return false;
    }

    // Rejects NaN, infinities, non-positive values and anything TimeSpan cannot represent.
    private static bool TryFromSeconds(double totalSeconds, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0) return false;
        if (totalSeconds >= TimeSpan.MaxValue.TotalSeconds) return false;
        try
        {
            result = TimeSpan.FromSeconds(totalSeconds);
        }
        catch (OverflowException)
        {
            result = TimeSpan.Zero;
            return false;
        }
        return result > TimeSpan.Zero;
    }

    private static string Format(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours:D2}h";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        return $"{(int)value.TotalMinutes:D2}:{value.Seconds:D2}";
    }
}
