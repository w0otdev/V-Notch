using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using LanguageDetection;

namespace VNotch.Services.Translation;

internal static partial class TranslationLanguages
{
    internal sealed record Language(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    internal static readonly string[] Codes = ("af am ar az ba be bg bn br bs ca ceb cs cy da de el en es et fa ff fi fr fy ga gd gl gu ha he hi hr ht hu hy id ig ilo is it ja jv ka kk km kn ko lb lg ln lo lt lv mg mk ml mn mr ms my ne nl no ns oc or pa pl ps pt ro ru sd si sk sl so sq sr ss su sv sw ta th tl tn tr uk ur uz vi wo xh yi yo zh zu").Split(' ');
    private static readonly FrozenSet<string> SupportedCodes = Codes.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string> DetectorCodeToSupported = BuildDetectorCodeMap();

    private static FrozenDictionary<string, string> BuildDetectorCodeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string candidate in Codes)
        {
            string key;
            try { key = CultureInfo.GetCultureInfo(candidate).ThreeLetterISOLanguageName; }
            catch (CultureNotFoundException) { key = candidate; }
            map.TryAdd(key, candidate);
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static string ModelTarget(string code) => code switch { "ns" => "nso", "tl" => "fil", _ => code };
    internal static string EnglishName(string code) => code switch
    {
        "ns" => "Northern Sotho",
        "tl" => "Filipino",
        "ceb" => "Cebuano",
        "ilo" => "Ilocano",
        _ => CultureInfo.GetCultureInfo(code).EnglishName
    };
    internal static readonly Language[] All = Codes.Select(code => new Language(code, Name(code))).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal static bool IsSupported(string? code) => code != null && SupportedCodes.Contains(code);
    internal static string Normalize(string? code, bool source = false) => IsSupported(code) ? code! : source ? "auto" : "vi";
    private static string Name(string code)
    {
        try { var culture = CultureInfo.GetCultureInfo(code); return $"{culture.NativeName} ({code})"; }
        catch (CultureNotFoundException) { return code; }
    }

    private static readonly Lazy<LanguageDetector> Detector = new(() =>
    {
        var detector = new LanguageDetector();
        detector.AddAllLanguages();
        return detector;
    });

    internal static string? Detect(string text)
    {
        string? script = DetectUnambiguousScript(text);
        if (script != null) return script;
        if (!HasAtLeastLetters(text, 12)) return null;
        string? code = Detector.Value.Detect(text);
        return FromDetectorCode(code);
    }

    // Passing through text skips the model entirely, so require stronger evidence
    // than the optional language hints used in prompts. Short/uncertain selections
    // and passages with sentences in different languages still reach the model.
    internal static string? DetectForPassthrough(string text)
    {
        if (!HasAtLeastLetters(text, 40)) return null;
        string? language = ConfidentLanguage(text);
        if (language == null) return null;
        foreach (string sentence in Regex.Split(text, @"[\r\n。！？]+|(?<=[.!?])\s+"))
        {
            if (HasAtLeastLetters(sentence, 12) && ConfidentLanguage(sentence) != language)
                return null;
        }
        return language;
    }

    private static bool HasAtLeastLetters(string text, int minimum)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c) && ++count >= minimum)
                return true;
        }
        return false;
    }

    private static string? ConfidentLanguage(string text)
    {
        var best = Detector.Value.DetectAll(text).MaxBy(x => x.Probability);
        return best?.Probability >= 0.995 ? FromDetectorCode(best.Language) : null;
    }

    private static string? FromDetectorCode(string? code) =>
        code != null && DetectorCodeToSupported.TryGetValue(code, out string? supported) ? supported : null;

    // This classifier supplies optional integrity hints. When uncertain, leave
    // recognition to the translation model instead of blocking short selections.
    internal static string? DetectUnambiguousScript(string text)
    {
        int totalLetters = 0, kana = 0, han = 0, ko = 0, th = 0, lo = 0, el = 0, hy = 0, ka = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            totalLetters++;
            if (c is >= '\u3040' and <= '\u30ff') kana++;
            else if (c is >= '\u4e00' and <= '\u9fff' or >= '\u3400' and <= '\u4dbf') han++;
            else if (c is >= '\uac00' and <= '\ud7af') ko++;
            else if (c is >= '\u0e00' and <= '\u0e7f') th++;
            else if (c is >= '\u0e80' and <= '\u0eff') lo++;
            else if (c is >= '\u0370' and <= '\u03ff') el++;
            else if (c is >= '\u0530' and <= '\u058f') hy++;
            else if (c is >= '\u10a0' and <= '\u10ff') ka++;
        }
        if (totalLetters == 0) return null;
        // Han characters also occur in Japanese. Require at least one Kana character
        // and a majority of Japanese (Kana + Han) characters.
        int ja = kana > 0 ? kana + han : 0;
        if (IsDominantScript(ja, totalLetters)) return "ja";
        if (IsDominantScript(ko, totalLetters)) return "ko";
        if (IsDominantScript(th, totalLetters)) return "th";
        if (IsDominantScript(lo, totalLetters)) return "lo";
        if (IsDominantScript(el, totalLetters)) return "el";
        if (IsDominantScript(hy, totalLetters)) return "hy";
        if (IsDominantScript(ka, totalLetters)) return "ka";
        return null;
    }

    private static bool IsDominantScript(int scriptCount, int totalLetters) =>
        scriptCount > 0 && ((totalLetters < 12 && scriptCount == totalLetters) || scriptCount * 2 >= totalLetters);
}
