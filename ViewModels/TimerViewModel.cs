using System;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VNotch.ViewModels;

public partial class TimerViewModel : ObservableObject
{
    private static readonly TimeSpan Maximum = TimeSpan.FromDays(7);

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

    public double Progress
    {
        get
        {
            vn_timer_compute_progress(Duration.Ticks, Remaining.Ticks, out double progress);
            return progress;
        }
    }

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
        vn_timer_compute_adjust_ticks(Duration.Ticks, direction, out long nextTicks, out byte changed);
        if (changed == 0) return false;
        var next = TimeSpan.FromTicks(nextTicks);
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

#pragma warning disable S2325
    public unsafe bool TryParseCustomTime(string? input, out TimeSpan result)
#pragma warning restore S2325
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        fixed (char* ptr = input)
        {
            vn_timer_parse_custom_time(ptr, input.Length, out long ticks, out byte valid);
            if (valid == 0 || ticks <= 0) return false;
            result = TimeSpan.FromTicks(ticks);
            return true;
        }
    }

    private static unsafe string Format(TimeSpan value)
    {
        char* buf = stackalloc char[32];
        vn_timer_format_remaining(value.Ticks, buf, 32, out int len);
        return len > 0 ? new string(buf, 0, len) : "00:00";
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_timer_parse_custom_time(
        char* input, int input_len, out long out_ticks, out byte out_valid);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_timer_format_remaining(
        long remaining_ticks, char* out_buf, int buf_cap, out int out_len);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timer_compute_adjust_ticks(
        long current_duration_ticks, int direction, out long out_next_ticks, out byte out_changed);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timer_compute_progress(
        long duration_ticks, long remaining_ticks, out double out_progress);
}
