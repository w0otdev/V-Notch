using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

public partial class WordClock : TextBlock
{
    private const string OClockLocKey = "wordClock.oclock";

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_word_clock_format_time(
        ReadOnlySpan<char> lang,
        int langLen,
        int hour24,
        int minute,
        ReadOnlySpan<char> prefix,
        int prefixLen,
        ReadOnlySpan<char> oclock,
        int oclockLen,
        Span<char> outBuf,
        int cap,
        out int outLen);

    private readonly UiScheduledTimer _timer;
    private bool _isRunning;
    private int _lastRenderedMinuteKey = -1;
    private string _lastRenderedLanguage = "";

    public WordClock()
    {
        _timer = new UiScheduledTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => UpdateText();

        Loaded += (_, _) => UpdateRunningState();
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) => UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        if (IsVisible && IsLoaded)
            StartTimer();
        else
            StopTimer();
    }

    private void StartTimer()
    {
        if (_isRunning) return;
        _isRunning = true;
        _timer.Start();
        _lastRenderedMinuteKey = -1;
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
        if (minuteKey == _lastRenderedMinuteKey && _lastRenderedLanguage == Loc.CurrentLanguage) return;
        _lastRenderedMinuteKey = minuteKey;
        _lastRenderedLanguage = Loc.CurrentLanguage;

        Text = FormatLocalizedTime(now);
    }

    public void RefreshLocalization()
    {
        _lastRenderedMinuteKey = -1;
        UpdateText();
    }

    internal static string FormatLocalizedTime(DateTime now)
    {
        string lang = Loc.CurrentLanguage;
        string prefix = Loc.Get("wordClock.prefix");
        string oclock = now.Minute == 0 ? Loc.Get(OClockLocKey) : string.Empty;

        Span<char> buffer = stackalloc char[256];
        int rc = vn_word_clock_format_time(
            lang, lang.Length,
            now.Hour, now.Minute,
            prefix, prefix.Length,
            oclock, oclock.Length,
            buffer, buffer.Length,
            out int written);
        if (rc == 0 && written >= 0)
            return new string(buffer[..written]);

        char[] fallback = new char[Math.Max(512, prefix.Length + oclock.Length + 128)];
        rc = vn_word_clock_format_time(
            lang, lang.Length,
            now.Hour, now.Minute,
            prefix, prefix.Length,
            oclock, oclock.Length,
            fallback, fallback.Length,
            out written);
        return rc == 0 && written >= 0 ? new string(fallback, 0, written) : string.Empty;
    }
}
