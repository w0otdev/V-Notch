using System;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

public class WordClock : TextBlock
{
    private static readonly string[] Ones =
    {
        "Twelve", "One", "Two", "Three", "Four", "Five", "Six",
        "Seven", "Eight", "Nine", "Ten", "Eleven"
    };

    private static readonly string[] Teens =
    {
        "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty"
    };

    private const string OClockLocKey = "wordClock.oclock";

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
        int hour = now.Hour % 12;
        if (hour == 0) hour = 12;
        int minute = now.Minute;
        string prefix = Loc.Get("wordClock.prefix");

        return Loc.CurrentLanguage switch
        {
            "vi" => FormatVietnameseTime(prefix, hour, minute),
            "es" => FormatSpanishTime(prefix, hour, minute),
            "fr" => FormatFrenchTime(prefix, hour, minute),
            "de" => FormatGermanTime(prefix, hour, minute),
            "ja" => FormatJapaneseTime(prefix, hour, minute),
            "hi" => FormatHindiTime(prefix, hour, minute),
            _ => $"{prefix}\n{SpellHour(now.Hour)}\n{SpellMinute(minute)}"
        };
    }

    private static string FormatVietnameseTime(string prefix, int hour, int minute)
    {
        if (minute == 0)
        {
            return $"{prefix}\n{SpellVietnamese(hour)} giờ";
        }

        string minutePrefix = minute < 10 ? "lẻ " : "";
        return $"{prefix}\n{SpellVietnamese(hour)} giờ\n{minutePrefix}{SpellVietnamese(minute)}";
    }

    private static string FormatSpanishTime(string prefix, int hour, int minute)
    {
        string naturalPrefix = hour == 1 ? "Es la" : prefix;
        string hourText = hour == 1 ? "una" : SpellSpanish(hour);
        return minute == 0
            ? $"{naturalPrefix}\n{hourText}\n{Loc.Get(OClockLocKey)}"
            : $"{naturalPrefix}\n{hourText}\ny {SpellSpanish(minute)}";
    }

    private static string FormatFrenchTime(string prefix, int hour, int minute)
    {
        string hourText = hour == 1 ? "une heure" : $"{SpellFrench(hour)} heures";
        return minute == 0
            ? $"{prefix}\n{hourText}\n{Loc.Get(OClockLocKey)}"
            : $"{prefix}\n{hourText}\n{SpellFrenchMinute(minute)}";
    }

    private static string FormatGermanTime(string prefix, int hour, int minute)
    {
        string hourText = hour == 1 ? "ein" : SpellGerman(hour);
        return minute == 0
            ? $"{prefix}\n{hourText} Uhr"
            : $"{prefix}\n{hourText} Uhr\n{SpellGerman(minute)}";
    }

    private static string FormatJapaneseTime(string prefix, int hour, int minute) =>
        minute == 0
            ? $"{prefix}\n{Loc.Get(OClockLocKey)}{SpellJapanese(hour)}時"
            : $"{prefix}\n{SpellJapanese(hour)}時\n{SpellJapanese(minute)}分";

    private static string FormatHindiTime(string prefix, int hour, int minute) =>
        minute == 0
            ? $"{prefix}\n{SpellHindi(hour)} {Loc.Get(OClockLocKey)}"
            : $"{prefix}\n{SpellHindi(hour)} बजकर\n{SpellHindi(minute)} मिनट";

    private static string SpellHour(int hour24)
    {
        int h = hour24 % 12;
        return Ones[h];
    }

    private static string SpellMinute(int minute)
    {
        if (minute == 0) return Loc.Get(OClockLocKey);
        if (minute < 10) return "Oh " + Ones[minute];
        if (minute < 20) return Teens[minute - 10];

        string tens = Tens[minute / 10];
        int unit = minute % 10;
        return unit == 0 ? tens : tens + " " + Ones[unit];
    }

    private static readonly string[] VietnameseUnits =
    {
        "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"
    };

    private static readonly string[] SpanishSmall =
    {
        "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve",
        "diez", "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve",
        "veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve"
    };

    private static readonly string[] SpanishTens =
    {
        "", "", "veinte", "treinta", "cuarenta", "cincuenta"
    };

    private static readonly string[] FrenchSmall =
    {
        "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf",
        "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf"
    };

    private static readonly string[] FrenchTens =
    {
        "", "", "vingt", "trente", "quarante", "cinquante"
    };

    private static readonly string[] GermanSmall =
    {
        "null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun",
        "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"
    };

    private static readonly string[] GermanTens =
    {
        "", "", "zwanzig", "dreißig", "vierzig", "fünfzig"
    };

    private static readonly string[] JapaneseDigits =
    {
        "零", "一", "二", "三", "四", "五", "六", "七", "八", "九"
    };

    private static readonly string[] HindiNumbers =
    {
        "शून्य", "एक", "दो", "तीन", "चार", "पाँच", "छह", "सात", "आठ", "नौ",
        "दस", "ग्यारह", "बारह", "तेरह", "चौदह", "पंद्रह", "सोलह", "सत्रह", "अठारह", "उन्नीस",
        "बीस", "इक्कीस", "बाईस", "तेईस", "चौबीस", "पच्चीस", "छब्बीस", "सत्ताईस", "अट्ठाईस", "उनतीस",
        "तीस", "इकतीस", "बत्तीस", "तैंतीस", "चौंतीस", "पैंतीस", "छत्तीस", "सैंतीस", "अड़तीस", "उनतालीस",
        "चालीस", "इकतालीस", "बयालीस", "तैंतालीस", "चवालीस", "पैंतालीस", "छियालीस", "सैंतालीस", "अड़तालीस", "उनचास",
        "पचास", "इक्यावन", "बावन", "तिरपन", "चौवन", "पचपन", "छप्पन", "सत्तावन", "अट्ठावन", "उनसठ"
    };

    private static string SpellVietnamese(int value)
    {
        if (value < 10) return VietnameseUnits[value];
        if (value < 20)
        {
            if (value == 10) return "mười";
            string unitSuffix = value == 15 ? "lăm" : VietnameseUnits[value - 10];
            return "mười " + unitSuffix;
        }

        int tens = value / 10;
        int unit = value % 10;
        if (unit == 0) return VietnameseUnits[tens] + " mươi";
        string unitText = unit switch
        {
            1 => "mốt",
            5 => "lăm",
            _ => VietnameseUnits[unit]
        };
        return VietnameseUnits[tens] + " mươi " + unitText;
    }

    private static string SpellSpanish(int value)
    {
        if (value < SpanishSmall.Length) return SpanishSmall[value];
        int unit = value % 10;
        return unit == 0 ? SpanishTens[value / 10] : $"{SpanishTens[value / 10]} y {SpanishSmall[unit]}";
    }

    private static string SpellFrench(int value)
    {
        if (value < FrenchSmall.Length) return FrenchSmall[value];
        int unit = value % 10;
        if (unit == 0) return FrenchTens[value / 10];
        return unit == 1 ? $"{FrenchTens[value / 10]}-et-un" : $"{FrenchTens[value / 10]}-{FrenchSmall[unit]}";
    }

    private static string SpellFrenchMinute(int value)
    {
        string number = SpellFrench(value);
        if (value % 10 != 1) return number;
        return number.EndsWith("un", StringComparison.Ordinal)
            ? number[..^2] + "une"
            : number;
    }

    private static string SpellGerman(int value)
    {
        if (value < GermanSmall.Length) return GermanSmall[value];
        int unit = value % 10;
        if (unit == 0) return GermanTens[value / 10];
        string unitText = unit == 1 ? "ein" : GermanSmall[unit];
        return unitText + "und" + GermanTens[value / 10];
    }

    private static string SpellJapanese(int value)
    {
        if (value < 10) return JapaneseDigits[value];
        int tens = value / 10;
        int unit = value % 10;
        string result = tens == 1 ? "十" : JapaneseDigits[tens] + "十";
        return unit == 0 ? result : result + JapaneseDigits[unit];
    }

    private static string SpellHindi(int value)
    {
        return HindiNumbers[Math.Clamp(value, 0, HindiNumbers.Length - 1)];
    }
}
