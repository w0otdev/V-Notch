using System.Buffers;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace VNotch.Services;

public sealed record LocaleMetadata(string Code, string Name, string Culture);

public static partial class Loc
{
    private const string NativeDll = "VNotch.Native.dll";

    internal const long MaxExternalLocaleFileBytes = 2 * 1024 * 1024;
    private static readonly JsonReaderOptions LocaleReaderOptions = new() { MaxDepth = 8 };

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

    // Where one locale's strings come from. A code can have several sources (embedded resource, locale folder next
    // to the app, community folder in AppData); they are merged in discovery order.
    private sealed record LocaleSource(string? ResourceName, string? FilePath);

    private const string FallbackLanguage = "en";

    private static string _currentLanguage = FallbackLanguage;
    private static readonly object LoadLock = new();
    // Discovered once at startup and never mutated afterwards, so it can be read without locking.
    private static readonly Dictionary<string, List<LocaleSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LocaleMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    // Only the fallback and the active language are kept in memory. Published as an immutable FrozenDictionary
    // snapshot so concurrent readers never race with language switches or eviction.
    private static volatile FrozenDictionary<string, FrozenDictionary<string, string>> _strings =
        new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultLanguageOrder = { "en", "vi", "zh", "pt", "ru", "ar", "ko", "es", "fr", "de", "ja", "hi", "it", "tr", "pl", "nl", "id" };
    // Cross-language reverse lookup used only by the settings search. Built on first use (or prewarmed by
    // the settings window) and released when that window closes.
    private static readonly object TranslationLock = new();
    private static int _translationGeneration;
    private static volatile FrozenDictionary<string, string[]>? _translationsByText;

    /// <summary>Starts building the cross-language search index on a worker thread.</summary>
    public static void PrewarmTranslationIndex()
    {
        ThreadPool.QueueUserWorkItem(static generation =>
        {
            lock (TranslationLock)
            {
                if (generation != _translationGeneration) return;
                try { _translationsByText ??= BuildTranslationLookup(); }
                catch { /* The next lookup can retry a failed prewarm. */ }
            }
        }, Volatile.Read(ref _translationGeneration), preferLocal: false);
    }

    /// <summary>Releases the cross-language search index.</summary>
    public static void ReleaseTranslationIndex()
    {
        lock (TranslationLock)
        {
            _translationGeneration++;
            _translationsByText = null;
        }
    }

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
        GetOrLoadStrings(language) is { } table ? table.Keys : Array.Empty<string>();

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
        var current = _currentLanguage;
        if (strings.TryGetValue(current, out var langDict) && langDict.TryGetValue(key, out var value))
            return value;

        if (!string.Equals(current, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            && strings.TryGetValue(FallbackLanguage, out var enDict)
            && enDict.TryGetValue(key, out var enValue))
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

        var index = _translationsByText;
        if (index is null)
        {
            lock (TranslationLock)
            {
                index = _translationsByText ??= BuildTranslationLookup();
            }
        }

        return FindTranslations(index, text);
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

    // Registers every available locale and its metadata, streaming with Utf8JsonReader and stopping
    // immediately once the top-level "_metadata" object has been read.
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
            if (!TryReadLocaleMetadata(stream, out string? name, out string? culture))
                return;

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

    private static bool TryReadLocaleMetadata(Stream stream, out string? name, out string? culture)
    {
        name = null;
        culture = null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesInBuffer = 0;
            long totalBytesRead = 0;
            bool isFinalBlock = false;
            while (bytesInBuffer < 3 && !isFinalBlock)
            {
                int initialRead = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                if (initialRead == 0)
                {
                    isFinalBlock = true;
                }
                else
                {
                    bytesInBuffer += initialRead;
                    totalBytesRead += initialRead;
                }
            }

            if (bytesInBuffer >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                bytesInBuffer -= 3;
                if (bytesInBuffer > 0)
                {
                    buffer.AsSpan(3, bytesInBuffer).CopyTo(buffer);
                }
            }

            var state = new JsonReaderState(LocaleReaderOptions);
            bool seenRootStart = false;
            bool inMetadataObject = false;
            string? pendingProperty = null;

            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, state);
                while (reader.Read())
                {
                    if (!seenRootStart)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject) return false;
                        seenRootStart = true;
                        continue;
                    }

                    if (!inMetadataObject)
                    {
                        if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndObject)
                        {
                            return true;
                        }
                        if (reader.CurrentDepth == 1)
                        {
                            if (reader.TokenType == JsonTokenType.PropertyName)
                            {
                                pendingProperty = reader.ValueTextEquals("_metadata"u8) ? "_metadata" : null;
                            }
                            else if (pendingProperty is not null)
                            {
                                pendingProperty = null;
                                if (reader.TokenType == JsonTokenType.StartObject)
                                {
                                    inMetadataObject = true;
                                }
                            }
                        }
                        continue;
                    }

                    if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.EndObject)
                    {
                        return true;
                    }

                    if (reader.CurrentDepth == 2)
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            pendingProperty = reader.ValueTextEquals("name"u8)
                                ? "name"
                                : (reader.ValueTextEquals("culture"u8) ? "culture" : null);
                        }
                        else
                        {
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                if (pendingProperty == "name") name = reader.GetString();
                                else if (pendingProperty == "culture") culture = reader.GetString();
                            }
                            pendingProperty = null;
                        }
                    }
                }

                if (isFinalBlock) return false;

                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                if (consumed > 0)
                {
                    bytesInBuffer -= consumed;
                    if (bytesInBuffer > 0)
                    {
                        buffer.AsSpan(consumed, bytesInBuffer).CopyTo(buffer);
                    }
                }

                if (bytesInBuffer == buffer.Length)
                {
                    if (buffer.Length >= MaxExternalLocaleFileBytes) return false;
                    int nextSize = (int)Math.Min(MaxExternalLocaleFileBytes, (long)buffer.Length * 2);
                    byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
                    buffer.AsSpan(0, bytesInBuffer).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                int read = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                if (read == 0)
                {
                    isFinalBlock = true;
                }
                else
                {
                    totalBytesRead += read;
                    if (totalBytesRead > MaxExternalLocaleFileBytes) return false;
                    bytesInBuffer += read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Returns the strings for a locale, loading and publishing them on first use. Null when the locale is unknown.
    private static FrozenDictionary<string, string>? GetOrLoadStrings(string code)
    {
        if (_strings.TryGetValue(code, out var loaded)) return loaded;
        if (!_sources.ContainsKey(code)) return null;

        lock (LoadLock)
        {
            if (_strings.TryGetValue(code, out loaded)) return loaded;

            var dict = LoadLocaleStrings(code);
            var next = new Dictionary<string, FrozenDictionary<string, string>>(_strings, StringComparer.OrdinalIgnoreCase)
            {
                [code] = dict
            };
            _strings = next.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            return dict;
        }
    }

    internal static void MergeLocaleJson(Dictionary<string, string> target, ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty) throw new ArgumentException("Locale JSON cannot be empty.", nameof(utf8Json));
        if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
        {
            utf8Json = utf8Json[3..];
        }

        var staged = new List<(string Key, string Value)>();
        try
        {
            var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, new JsonReaderState(LocaleReaderOptions));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new ArgumentException("Locale JSON root must be an object.", nameof(utf8Json));
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    throw new ArgumentException("Malformed locale JSON property.", nameof(utf8Json));
                }

                string key = reader.GetString()!;
                if (!reader.Read())
                {
                    throw new ArgumentException("Unexpected end of locale JSON.", nameof(utf8Json));
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    staged.Add((key, reader.GetString()!));
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid locale JSON.", nameof(utf8Json), ex);
        }

        foreach (var (key, value) in staged)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '_' || string.Equals(key, value, StringComparison.Ordinal))
            {
                continue;
            }
            if (target.ContainsKey(key))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    target[key] = value;
                }
            }
            else
            {
                target[key] = value;
            }
        }
    }

    private static FrozenDictionary<string, string> LoadLocaleStrings(string code)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyBuiltInDefaults(code, dict);
        if (!_sources.TryGetValue(code, out var sources))
        {
            return dict.ToFrozenDictionary(StringComparer.Ordinal);
        }

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
                    int length = checked((int)stream.Length);
                    byte[] json = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        stream.ReadExactly(json.AsSpan(0, length));
                        MergeLocaleJson(dict, json.AsSpan(0, length));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(json);
                    }
                }
            }
            catch
            {
                // Ignore unreadable or broken community sources and keep what was loaded
            }
        }

        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Drops every loaded language except the fallback and the active one so a switch does not accumulate memory.
    private static void EvictInactiveLanguages(string active)
    {
        lock (LoadLock)
        {
            var previous = _strings;
            var kept = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            bool evictedAny = false;
            foreach (var (code, dict) in previous)
            {
                if (string.Equals(code, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, active, StringComparison.OrdinalIgnoreCase))
                {
                    kept[code] = dict;
                }
                else
                {
                    evictedAny = true;
                }
            }

            _strings = kept.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            if (evictedAny)
            {
                ReleaseTranslationIndex();
            }
        }
    }

    private static FrozenDictionary<string, string[]> BuildTranslationLookup()
    {
        var resident = _strings;
        var codes = DefaultLanguageOrder.Where(_sources.ContainsKey).Concat(_sources.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var tables = new List<FrozenDictionary<string, string>>();
        foreach (string code in codes)
        {
            if (!resident.TryGetValue(code, out var table))
            {
                table = LoadLocaleStrings(code);
            }
            tables.Add(table);
        }
        return BuildTranslationIndex(tables);
    }

    internal static FrozenDictionary<string, string[]> BuildTranslationIndex(
        IReadOnlyList<FrozenDictionary<string, string>> tables)
    {
        var keysByText = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < tables.Count; t++)
        {
            foreach (var (key, value) in tables[t])
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!keysByText.TryGetValue(value, out var keys))
                {
                    keys = new List<string>();
                    keysByText[value] = keys;
                }
                keys.Add(key);
            }
        }

        var result = new Dictionary<string, string[]>(keysByText.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (text, keys) in keysByText)
        {
            keys.Sort(StringComparer.Ordinal);
            var translations = new List<string>();
            string? previousKey = null;
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                if (string.Equals(key, previousKey, StringComparison.Ordinal)) continue;
                previousKey = key;

                for (int t = 0; t < tables.Count; t++)
                {
                    if (!tables[t].TryGetValue(key, out string? candidate) || string.IsNullOrWhiteSpace(candidate))
                    {
                        continue;
                    }
                    bool exists = false;
                    for (int i = 0; i < translations.Count; i++)
                    {
                        if (string.Equals(translations[i], candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        translations.Add(candidate);
                    }
                }
            }
            result[text] = translations.ToArray();
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    internal static List<string> FindTranslations(FrozenDictionary<string, string[]> index, string text) =>
        index.TryGetValue(text, out string[]? matches) && matches.Length > 0
            ? new List<string>(matches)
            : new List<string> { text };

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
