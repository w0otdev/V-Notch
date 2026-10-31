using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace VNotch.Services;

public sealed record LocaleMetadata(string Code, string Name, string Culture);

public static partial class Loc
{
    private const string NativeDll = "VNotch.Native.dll";

    internal const long MaxExternalLocaleFileBytes = 2 * 1024 * 1024;
    private static readonly JsonDocumentOptions LocaleJsonOptions = new() { MaxDepth = 8 };

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

    // Where one locale's strings come from. A code can have several sources (embedded resource, locale folder next
    // to the app, community folder in AppData); they are merged in discovery order.
    private sealed record LocaleSource(string? ResourceName, string? FilePath);

    private const string FallbackLanguage = "en";

    private static string _currentLanguage = FallbackLanguage;
    private static readonly object LoadLock = new();
    // Discovered once at startup and never mutated afterwards, so it can be read without locking.
    private static readonly Dictionary<string, List<LocaleSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocaleMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    // Only the fallback and the active language are kept in memory. Published as an immutable snapshot
    // (copy on write) so readers on any thread never observe a dictionary that is being modified.
    private static volatile Dictionary<string, Dictionary<string, string>> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultLanguageOrder = { "en", "vi", "zh", "pt", "ru", "ar", "ko", "es", "fr", "de", "ja", "hi", "it", "tr", "pl", "nl", "id" };
    // Cross-language reverse lookup used only by the settings search. It is built on first use (or prewarmed by
    // the settings window) and released when that window closes, so it does not occupy memory the rest of the time.
    private static Lazy<Dictionary<string, IReadOnlyList<string>>> _translationsByText = NewTranslationIndex();

    private static Lazy<Dictionary<string, IReadOnlyList<string>>> NewTranslationIndex() =>
        new(BuildTranslationLookup, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts building the cross-language search index on a worker thread.</summary>
    public static void PrewarmTranslationIndex()
    {
        ThreadPool.QueueUserWorkItem(static index =>
        {
            try { _ = ((Lazy<Dictionary<string, IReadOnlyList<string>>>)index!).Value; }
            catch { /* the index is rebuilt on demand if this fails */ }
        }, _translationsByText);
    }

    /// <summary>Drops the cross-language search index. It is rebuilt on the next lookup.</summary>
    public static void ReleaseTranslationIndex() =>
        Interlocked.Exchange(ref _translationsByText, NewTranslationIndex());

    static Loc()
    {
        DiscoverLocales();
        GetOrLoadStrings(FallbackLanguage);
    }

    public static string CurrentLanguage => _currentLanguage;
    public static event Action? LanguageChanged;

    public static void SetLanguage(string language)
    {
        string next = _sources.ContainsKey(language) ? language : FallbackLanguage;
        if (string.Equals(next, _currentLanguage, StringComparison.OrdinalIgnoreCase)) return;
        GetOrLoadStrings(next);
        _currentLanguage = next;
        EvictInactiveLanguages(next);
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
        GetOrLoadStrings(language)?.Keys ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    public static List<(string Code, string Name)> GetAvailableLanguages()
    {
        var result = new List<(string Code, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, add default languages in preferred order
        foreach (string code in DefaultLanguageOrder)
        {
            if (_sources.ContainsKey(code) && seen.Add(code))
            {
                string name = _metadata.TryGetValue(code, out var meta) && !string.IsNullOrWhiteSpace(meta.Name)
                    ? meta.Name
                    : code;
                result.Add((code, name));
            }
        }

        // Next, add any dynamically discovered community languages
        foreach (string code in _sources.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
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
        var strings = _strings;
        if (strings.TryGetValue(_currentLanguage, out var langDict) && langDict.TryGetValue(key, out var value))
            return value;

        if (_currentLanguage != FallbackLanguage && strings.TryGetValue(FallbackLanguage, out var enDict) && enDict.TryGetValue(key, out var enValue))
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

        return _translationsByText.Value.TryGetValue(text, out var translations)
            ? new List<string>(translations)
            : new List<string> { text };
    }

    internal static bool IsValidExternalLocaleFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists && info.Length > 0 && info.Length <= MaxExternalLocaleFileBytes;
        }
        catch
        {
            return false;
        }
    }

    // Registers every available locale and its metadata. Strings are not kept: each file is parsed once here
    // only to validate it and read the _metadata block.
    private static void DiscoverLocales()
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
                RegisterSource(code, new LocaleSource(resourceName, null), stream);
            }
        }

        var candidateDirectories = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Locales"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch", "Locales")
        };

        foreach (var dir in candidateDirectories)
        {
            DiscoverExternalDirectory(dir);
        }
    }

    internal static void DiscoverExternalDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(code)) continue;
                if (!IsValidExternalLocaleFile(file)) continue;

                try
                {
                    using var stream = File.OpenRead(file);
                    RegisterSource(code, new LocaleSource(null, file), stream);
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

    private static void RegisterSource(string code, LocaleSource source, Stream stream)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream, LocaleJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string? name = null;
            string? culture = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.NameEquals("_metadata") || prop.Value.ValueKind != JsonValueKind.Object) continue;

                if (prop.Value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    name = nameProp.GetString();
                if (prop.Value.TryGetProperty("culture", out var cultureProp) && cultureProp.ValueKind == JsonValueKind.String)
                    culture = cultureProp.GetString();
            }

            if (!_sources.TryGetValue(code, out var sources))
            {
                sources = new List<LocaleSource>();
                _sources[code] = sources;
            }
            sources.Add(source);

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

    // Returns the strings for a locale, loading and publishing them on first use. Null when the locale is unknown.
    private static Dictionary<string, string>? GetOrLoadStrings(string code)
    {
        if (_strings.TryGetValue(code, out var loaded)) return loaded;
        if (!_sources.ContainsKey(code)) return null;

        lock (LoadLock)
        {
            if (_strings.TryGetValue(code, out loaded)) return loaded;

            var dict = LoadLocaleStrings(code);
            var next = new Dictionary<string, Dictionary<string, string>>(_strings, StringComparer.OrdinalIgnoreCase)
            {
                [code] = dict
            };
            _strings = next;
            return dict;
        }
    }

    // Parses every source of a locale into a fresh dictionary, in discovery order.
    private static Dictionary<string, string> LoadLocaleStrings(string code)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyBuiltInDefaults(code, dict);
        if (!_sources.TryGetValue(code, out var sources)) return dict;

        var assembly = typeof(Loc).Assembly;
        foreach (var source in sources)
        {
            try
            {
                using Stream? stream = source.ResourceName is not null
                    ? assembly.GetManifestResourceStream(source.ResourceName)
                    : (source.FilePath is not null && IsValidExternalLocaleFile(source.FilePath)
                        ? File.OpenRead(source.FilePath)
                        : null);
                if (stream != null)
                {
                    LoadLocaleFromStream(dict, stream);
                }
            }
            catch
            {
                // Ignore unreadable sources and keep what was loaded
            }
        }

        return dict;
    }

    // Drops every loaded language except the fallback and the active one so a switch does not accumulate memory.
    private static void EvictInactiveLanguages(string active)
    {
        lock (LoadLock)
        {
            var kept = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, dict) in _strings)
            {
                if (string.Equals(code, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, active, StringComparison.OrdinalIgnoreCase))
                {
                    kept[code] = dict;
                }
            }

            _strings = kept;
        }
    }

    private static void LoadLocaleFromStream(Dictionary<string, string> dict, Stream stream)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream, LocaleJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.Name.StartsWith('_') && prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? text = prop.Value.GetString();
                    ReadOnlySpan<char> propSpan = prop.Name.AsSpan();
                    ReadOnlySpan<char> textSpan = text is not null ? text.AsSpan() : ReadOnlySpan<char>.Empty;
                    bool keyExists = dict.ContainsKey(prop.Name);
                    byte existsFlag = keyExists ? (byte)1 : (byte)0;
                    bool shouldStore = vn_localization_should_store_entry(
                        propSpan, propSpan.Length, textSpan, textSpan.Length, existsFlag, out byte outStore) == 0
                        ? outStore != 0
                        : (text != prop.Name && (!string.IsNullOrWhiteSpace(text) || !keyExists));
                    if (shouldStore)
                        dict[prop.Name] = text ?? string.Empty;
                }
            }
        }
        catch
        {
            // Keep existing loaded strings if parse fails
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildTranslationLookup()
    {
        // Every language is needed here. Languages that are not resident are loaded into a local map and released
        // once the lookup is built, so only the lookup itself stays alive.
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var resident = _strings;
        foreach (string code in _sources.Keys)
        {
            all[code] = resident.TryGetValue(code, out var dict) ? dict : LoadLocaleStrings(code);
        }

        var keysByText = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in all.Keys)
        {
            foreach (var (key, value) in all[language])
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
                foreach (string language in DefaultLanguageOrder.Concat(all.Keys))
                {
                    if (all.TryGetValue(language, out var langDict)
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

    private static void ApplyBuiltInDefaults(string code, Dictionary<string, string> dict)
    {
        switch (code.ToLowerInvariant())
        {
            case "vi":
                SeedBuiltInDefaults(dict, "MỚI", "Đóng", "Thư mục cài đặt không hợp lệ để gỡ cài đặt.", "Tiến trình tải mô hình", "Đang chuẩn bị tải xuống...", "Đang phát", "Không có nhạc đang phát", "Nghệ sĩ không xác định", "Phương tiện", "Hẹn giờ", "Âm thanh");
                break;
            case "es":
                SeedBuiltInDefaults(dict, "NUEVO", "Cerrar", "Directorio de instalación no válido para desinstalar.", "Progreso de descarga del modelo", "Preparando descarga...", "Reproduciendo ahora", "No hay multimedia en reproducción", "Artista desconocido", "Multimedia", "Temporizador", "Audio");
                break;
            case "fr":
                SeedBuiltInDefaults(dict, "NOUVEAU", "Fermer", "Répertoire d'installation non valide pour la désinstallation.", "Progression du téléchargement du modèle", "Préparation du téléchargement...", "Lecture en cours", "Aucun média en cours de lecture", "Artiste inconnu", "Médias", "Minuteur", "Audio");
                break;
            case "de":
                SeedBuiltInDefaults(dict, "NEU", "Schließen", "Ungültiges Installationsverzeichnis für die Deinstallation.", "Fortschritt beim Modell-Download", "Download wird vorbereitet...", "Aktuelle Wiedergabe", "Keine Medienwiedergabe", "Unbekannter Künstler", "Medien", "Timer", "Audio");
                break;
            case "ja":
                SeedBuiltInDefaults(dict, "新着", "閉じる", "アンインストール用のインストール先フォルダーが無効です。", "モデルのダウンロード進行状況", "ダウンロードを準備しています...", "再生中", "再生中のメディアはありません", "不明なアーティスト", "メディア", "タイマー", "オーディオ");
                break;
            case "zh":
                SeedBuiltInDefaults(dict, "新", "关闭", "用于卸载的安装目录无效。", "模型下载进度", "正在准备下载...", "正在播放", "当前无媒体播放", "未知艺术家", "媒体", "计时器", "音频");
                break;
            case "ko":
                SeedBuiltInDefaults(dict, "신규", "닫기", "제거할 설치 디렉터리가 올바르지 않습니다.", "모델 다운로드 진행률", "다운로드 준비 중...", "지금 재생 중", "재생 중인 미디어 없음", "알 수 없는 아티스트", "미디어", "타이머", "오디오");
                break;
            case "pt":
                SeedBuiltInDefaults(dict, "NOVO", "Fechar", "Diretório de instalação inválido para desinstalação.", "Progresso do download do modelo", "Preparando download...", "Reproduzindo agora", "Nenhuma mídia em reprodução", "Artista desconhecido", "Mídia", "Temporizador", "Áudio");
                break;
            case "ru":
                SeedBuiltInDefaults(dict, "НОВОЕ", "Закрыть", "Недопустимый каталог установки для удаления.", "Ход загрузки модели", "Подготовка к загрузке...", "Сейчас играет", "Нет воспроизводимого медиа", "Неизвестный исполнитель", "Медиа", "Таймер", "Аудио");
                break;
            case "ar":
                SeedBuiltInDefaults(dict, "جديد", "إغلاق", "مسار التثبيت غير صالح لإلغاء التثبيت.", "تقدم تنزيل النموذج", "جاري تحضير التنزيل...", "قيد التشغيل الآن", "لا توجد وسائط قيد التشغيل", "فنان غير معروف", "الوسائط", "المؤقت", "الصوت");
                break;
            case "hi":
                SeedBuiltInDefaults(dict, "नया", "बंद करें", "अनइंस्टॉल के लिए अमान्य इंस्टॉलेशन निर्देशिका।", "मॉडल डाउनलोड प्रगति", "डाउनलोड की तैयारी हो रही है...", "अभी चल रहा है", "कोई मीडिया नहीं चल रहा है", "अज्ञात कलाकार", "मीडिया", "टाइमर", "ऑडियो");
                break;
            case "it":
                SeedBuiltInDefaults(dict, "NUOVO", "Chiudi", "Directory di installazione non valida per la disinstallazione.", "Avanzamento download del modello", "Preparazione del download...", "In riproduzione", "Nessun contenuto in riproduzione", "Artista sconosciuto", "Multimedia", "Timer", "Audio");
                break;
            case "tr":
                SeedBuiltInDefaults(dict, "YENİ", "Kapat", "Kaldırma işlemi için geçersiz kurulum dizini.", "Model indirme ilerlemesi", "İndirme hazırlanıyor...", "Şimdi Oynatılıyor", "Oynatılan medya yok", "Bilinmeyen Sanatçı", "Medya", "Zamanlayıcı", "Ses");
                break;
            case "pl":
                SeedBuiltInDefaults(dict, "NOWOŚĆ", "Zamknij", "Nieprawidłowy katalog instalacyjny do odinstalowania.", "Postęp pobierania modelu", "Przygotowywanie pobierania...", "Teraz odtwarzane", "Brak odtwarzanych multimediów", "Nieznany wykonawca", "Multimedia", "Minutnik", "Dźwięk");
                break;
            case "nl":
                SeedBuiltInDefaults(dict, "NIEUW", "Sluiten", "Ongeldige installatiemap voor verwijderen.", "Voortgang modeldownload", "Download voorbereiden...", "Nu afspelend", "Geen media wordt afgespeeld", "Onbekende artiest", "Media", "Timer", "Audio");
                break;
            case "id":
                SeedBuiltInDefaults(dict, "BARU", "Tutup", "Direktori instalasi tidak valid untuk pencopotan.", "Progres unduhan model", "Menyiapkan unduhan...", "Sedang Diputar", "Tidak ada media yang diputar", "Artis Tidak Dikenal", "Media", "Pengatur Waktu", "Audio");
                break;
            default:
                SeedBuiltInDefaults(dict, "NEW", "Close", "Invalid installation directory for uninstall.", "Model download progress", "Preparing download...", "Now Playing", "No media playing", "Unknown Artist", "Media", "Timer", "Audio");
                break;
        }
    }

    private static void SeedBuiltInDefaults(
        Dictionary<string, string> dict,
        string commonNew,
        string commonClose,
        string uninstallInvalidDir,
        string modelDownloadProgress,
        string preparingDownload,
        string nowPlaying,
        string noMedia,
        string unknownArtist,
        string navMedia,
        string navTimer,
        string navAudio)
    {
        dict["common.new"] = commonNew;
        dict["common.close"] = commonClose;
        dict["setup.uninstall.invalidDir"] = uninstallInvalidDir;
        dict["settings.translation.modelDownloadProgress"] = modelDownloadProgress;
        dict["update.preparingDownload"] = preparingDownload;
        dict["media.nowPlaying"] = nowPlaying;
        dict["media.noMedia"] = noMedia;
        dict["media.unknownArtist"] = unknownArtist;
        dict["nav.media"] = navMedia;
        dict["nav.timer"] = navTimer;
        dict["nav.audio"] = navAudio;
    }
}

internal static class LocalizationService
{
    internal const long MaxExternalLocaleFileBytes = Loc.MaxExternalLocaleFileBytes;
    internal static bool IsValidExternalLocaleFile(string filePath) => Loc.IsValidExternalLocaleFile(filePath);
    internal static void DiscoverExternalDirectory(string dir) => Loc.DiscoverExternalDirectory(dir);
}
