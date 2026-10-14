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
    internal static bool IsSupported(string? code) => code != null && Array.IndexOf(Codes, code) >= 0;
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
        foreach (string sentence in Regex.Split(text, @"[.!?。！？\r\n]+"))
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

    private static string? FromDetectorCode(string? code)
    {
        return Codes.FirstOrDefault(candidate =>
        {
            try { return CultureInfo.GetCultureInfo(candidate).ThreeLetterISOLanguageName == code; }
            catch (CultureNotFoundException) { return candidate == code; }
        });
    }

    // This classifier supplies optional integrity hints. When uncertain, leave
    // recognition to the translation model instead of blocking short selections.
    internal static string? DetectUnambiguousScript(string text)
    {
        if (Regex.IsMatch(text, "[\u3040-\u30ff]")) return "ja";
        if (Regex.IsMatch(text, "[\uac00-\ud7af]")) return "ko";
        // Han characters also occur in Japanese. Use the detector for longer passages;
        // short Han-only selections are recognized by the translation model.
        if (Regex.IsMatch(text, "[\u0e00-\u0e7f]")) return "th";
        if (Regex.IsMatch(text, "[\u0e80-\u0eff]")) return "lo";
        if (Regex.IsMatch(text, "[\u0370-\u03ff]")) return "el";
        if (Regex.IsMatch(text, "[\u0530-\u058f]")) return "hy";
        if (Regex.IsMatch(text, "[\u10a0-\u10ff]")) return "ka";
        return null;
    }
}
