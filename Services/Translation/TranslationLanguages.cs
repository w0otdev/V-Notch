using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    internal static readonly Language[] All = Codes.Select(code => new Language(code, FormatName(code))).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal static bool IsSupported(string? code) => code != null && SupportedCodes.Contains(code);
    internal static string Normalize(string? code, bool source = false)
    {
        if (IsSupported(code)) return code!;
        return source ? "auto" : "vi";
    }
    private static string FormatName(string code)
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
    private static readonly SemaphoreSlim PassthroughGate = new(1, 1);

    internal static string? Detect(string text)
    {
        text = text.Normalize(NormalizationForm.FormC);
        if (TranslationPhraseGlossary.Find(text) is { } phrase) return phrase.Source;
        string? script = DetectUnambiguousScript(text);
        if (script != null) return script;
        if (!HasAtLeastLetters(text, 12)) return null;
        string? code = Detector.Value.Detect(text);
        return FromDetectorCode(code);
    }

    internal static string? DetectForPassthrough(string text)
    {
        text = text.Normalize(NormalizationForm.FormC);
        if (!HasAtLeastLetters(text, 40)) return null;
        return DetectForPassthroughCore(text, default);
    }

    internal static async ValueTask<string?> DetectForPassthroughAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        text = text.Normalize(NormalizationForm.FormC);
        if (!HasAtLeastLetters(text, 40)) return null;
        await PassthroughGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DetectForPassthroughCore(text, ct), ct).ConfigureAwait(false);
        }
        finally { PassthroughGate.Release(); }
    }

    private static string? DetectForPassthroughCore(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? language = ConfidentLanguage(text);
        ct.ThrowIfCancellationRequested();
        if (language == null) return null;
        foreach (string sentence in PassthroughSentences().Split(text))
        {
            ct.ThrowIfCancellationRequested();
            if (sentence.Length == text.Length) continue;
            if (HasAtLeastLetters(sentence, 12) && ConfidentLanguage(sentence) != language)
                return null;
        }
        return language;
    }

    [GeneratedRegex(@"[\r\n。！？]+|(?<=[.!?])\s+")]
    private static partial Regex PassthroughSentences();

    private static unsafe bool HasAtLeastLetters(string text, int minimum)
    {
        if (string.IsNullOrEmpty(text)) return minimum <= 0;
        fixed (char* ptr = text)
        {
            vn_translation_lang_has_at_least_letters(ptr, text.Length, minimum, out byte has);
            return has != 0;
        }
    }

    private static string? ConfidentLanguage(string text)
    {
        var best = Detector.Value.DetectAll(text).MaxBy(x => x.Probability);
        return best?.Probability >= 0.995 ? FromDetectorCode(best.Language) : null;
    }

    private static string? FromDetectorCode(string? code) =>
        code != null && DetectorCodeToSupported.TryGetValue(code, out string? supported) ? supported : null;

    internal static unsafe string? DetectUnambiguousScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        Span<char> buf = stackalloc char[8];
        fixed (char* ptr = text)
        fixed (char* pOut = buf)
        {
            int rc = vn_translation_lang_detect_unambiguous_script(ptr, text.Length, pOut, buf.Length, out int len);
            return rc == 0 && len > 0 ? new string(buf[..len]) : null;
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_lang_has_at_least_letters(
        char* text_ptr, int text_len, int minimum, out byte out_has);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_translation_lang_detect_unambiguous_script(
        char* text_ptr, int text_len,
        char* out_buf, int out_cap, out int out_len);
}
