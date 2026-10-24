using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

public sealed record LocaleMetadata(string Code, string Name, string Culture);

public static partial class Loc
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_localization_resolve_default_culture(
        ReadOnlySpan<char> lang,
        int langLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_localization_extract_resource_code(
        ReadOnlySpan<char> resourceName,
        int resourceNameLen,
        out int outStart,
        out int outLen,
        out byte outMatched);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_localization_should_store_entry(
        ReadOnlySpan<char> propName,
        int propNameLen,
        ReadOnlySpan<char> text,
        int textLen,
        byte keyExists,
        out byte outShouldStore);

    private static string _currentLanguage = "en";
    private static readonly Dictionary<string, Dictionary<string, string>> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocaleMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultLanguageOrder = { "en", "vi", "zh", "pt", "ru", "ar", "ko", "es", "fr", "de", "ja", "hi", "it", "tr", "pl", "nl", "id" };
    private static readonly Dictionary<string, IReadOnlyList<string>> TranslationsByText = InitializeTranslations();

    public static string CurrentLanguage => _currentLanguage;
    public static event Action? LanguageChanged;

    public static void SetLanguage(string language)
    {
        string next = _strings.ContainsKey(language) ? language : "en";
        if (string.Equals(next, _currentLanguage, StringComparison.OrdinalIgnoreCase)) return;
        _currentLanguage = next;
        LanguageChanged?.Invoke();
    }

    public static CultureInfo GetCulture()
    {
        if (_metadata.TryGetValue(_currentLanguage, out var meta) && !string.IsNullOrWhiteSpace(meta.Culture))
        {
            try { return CultureInfo.GetCultureInfo(meta.Culture); }
            catch { /* fallback to switch */ }
        }

        Span<char> cultureBuf = stackalloc char[16];
        ReadOnlySpan<char> langSpan = _currentLanguage.AsSpan();
        if (vn_localization_resolve_default_culture(langSpan, langSpan.Length, cultureBuf, cultureBuf.Length, out int cultureLen) == 0 && cultureLen > 0)
        {
            return CultureInfo.GetCultureInfo(new string(cultureBuf[..cultureLen]));
        }

        return CultureInfo.GetCultureInfo("en-US");
    }

    internal static IReadOnlyCollection<string> GetKeys(string language) =>
        _strings.TryGetValue(language, out var strings)
            ? strings.Keys
            : Array.Empty<string>();

    public static List<(string Code, string Name)> GetAvailableLanguages()
    {
        var result = new List<(string Code, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, add default languages in preferred order
        foreach (string code in DefaultLanguageOrder)
        {
            if (_strings.ContainsKey(code) && seen.Add(code))
            {
                string name = _metadata.TryGetValue(code, out var meta) && !string.IsNullOrWhiteSpace(meta.Name)
                    ? meta.Name
                    : code;
                result.Add((code, name));
            }
        }

        // Next, add any dynamically discovered community languages
        foreach (string code in _strings.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(code))
            {
                string name = _metadata.TryGetValue(code, out var meta) && !string.IsNullOrWhiteSpace(meta.Name)
                    ? meta.Name
                    : code;
                result.Add((code, name));
            }
        }

        return result;
    }

    public static string Get(string key)
    {
        if (_strings.TryGetValue(_currentLanguage, out var langDict) && langDict.TryGetValue(key, out var value))
            return value;

        if (_currentLanguage != "en" && _strings.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enValue))
            return enValue;

        return key;
    }

    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    public static List<string> GetAllTranslations(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return TranslationsByText.TryGetValue(text, out var translations)
            ? new List<string>(translations)
            : new List<string> { text };
    }

    private static Dictionary<string, IReadOnlyList<string>> InitializeTranslations()
    {
        LoadEmbeddedLocales();
        LoadFileSystemLocales();
        return BuildTranslationLookup();
    }

    private static void LoadEmbeddedLocales()
    {
        var assembly = typeof(Loc).Assembly;

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            ReadOnlySpan<char> resSpan = resourceName.AsSpan();
            if (vn_localization_extract_resource_code(resSpan, resSpan.Length, out int start, out int len, out byte matched) != 0 || matched == 0)
            {
                continue;
            }

            string code = resourceName.Substring(start, len);
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                LoadLocaleFromStream(code, stream);
            }
        }
    }

    private static void LoadFileSystemLocales()
    {
        var candidateDirectories = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Locales"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch", "Locales")
        };

        foreach (var dir in candidateDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    try
                    {
                        using var stream = File.OpenRead(file);
                        LoadLocaleFromStream(code, stream);
                    }
                    catch
                    {
                        // Ignore corrupted community files and keep fallback
                    }
                }
            }
            catch
            {
                // Ignore directory enumeration failures
            }
        }
    }

    private static void LoadLocaleFromStream(string code, Stream stream)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!_strings.TryGetValue(code, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                _strings[code] = dict;
            }

            string? name = null;
            string? culture = null;

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("_metadata") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        name = nameProp.GetString();
                    if (prop.Value.TryGetProperty("culture", out var cultureProp) && cultureProp.ValueKind == JsonValueKind.String)
                        culture = cultureProp.GetString();
                }
                else if (!prop.Name.StartsWith('_') && prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? text = prop.Value.GetString();
                    ReadOnlySpan<char> propSpan = prop.Name.AsSpan();
                    ReadOnlySpan<char> textSpan = text is not null ? text.AsSpan() : ReadOnlySpan<char>.Empty;
                    bool keyExists = dict.ContainsKey(prop.Name);
                    bool shouldStore = vn_localization_should_store_entry(
                        propSpan, propSpan.Length, textSpan, textSpan.Length, keyExists ? (byte)1 : (byte)0, out byte outStore) == 0
                        ? outStore != 0
                        : (text != prop.Name && (!string.IsNullOrWhiteSpace(text) || !keyExists));
                    if (shouldStore)
                        dict[prop.Name] = text ?? string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(culture))
            {
                _metadata[code] = new LocaleMetadata(code, name ?? code, culture ?? "en-US");
            }
        }
        catch
        {
            // Keep existing loaded strings if parse fails
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildTranslationLookup()
    {
        var keysByText = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in _strings.Keys)
        {
            foreach (var (key, value) in _strings[language])
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (!keysByText.TryGetValue(value, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    keysByText[value] = keys;
                }

                keys.Add(key);
            }
        }

        var lookup = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceText, matchingKeys) in keysByText)
        {
            var translations = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in matchingKeys.OrderBy(key => key, StringComparer.Ordinal))
            {
                foreach (string language in DefaultLanguageOrder.Concat(_strings.Keys))
                {
                    if (_strings.TryGetValue(language, out var langDict)
                        && langDict.TryGetValue(key, out string? translation)
                        && !string.IsNullOrWhiteSpace(translation)
                        && seen.Add(translation))
                    {
                        translations.Add(translation);
                    }
                }
            }

            lookup[sourceText] = translations;
        }

        return lookup;
    }
}
