using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class SettingsService : ISettingsService, IAsyncDisposable, IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSettingsNormalizationParams
    {
        public int Width;
        public int Height;
        public int CornerRadius;
        public int DynamicIslandWidth;
        public int DynamicIslandHeight;
        public int NotificationDuration;
        public int AnimationFps;
        public int MinFps;
        public int MaxFps;
        public double SpotifyCanvasBrightness;
        public double Opacity;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_settings_service_normalize_numeric(
        ref VnSettingsNormalizationParams inoutParams,
        out byte outChanged);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_service_is_language_valid(
        ReadOnlySpan<char> lang,
        int langLen,
        out byte outValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_service_clean_manual_city(
        ReadOnlySpan<char> city,
        int cityLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_service_check_requires_restart(
        int importedGpu,
        int currentGpu,
        ReadOnlySpan<char> importedPriority,
        int importedPriorityLen,
        ReadOnlySpan<char> currentPriority,
        int currentPriorityLen,
        out byte outRequiresRestart);

    private const string LogCategoryLoad = "SETTINGS-LOAD";
    private const string LogCategorySave = "SETTINGS-SAVE";

    private readonly string _settingsPath;
    private readonly string _appFolder;
    private readonly Action<string> _apiKeySaveWarning;
    private readonly object _loadLock = new();
    private readonly object _cacheLock = new();
    private NotchSettings? _cachedSettings;
    private (long Length, DateTime WrittenUtc)? _cachedStamp;
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = true };

    private sealed record SaveOperation(NotchSettings Snapshot, bool KeepExistingBackup, TaskCompletionSource Tcs);

    private const int SaveChannelCapacity = 32;

    private readonly Channel<SaveOperation> _saveChannel = Channel.CreateBounded<SaveOperation>(new BoundedChannelOptions(SaveChannelCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly Task _worker;
    private readonly object _inProcessSaveGate = new();
    private bool _lastSaveSucceeded;
    private int _disposeSignaled;
    private volatile bool _disposed;
    private volatile bool _settingsLoadFailed;

    private Task StartWorker()
    {
        return Task.Run(async () =>
        {
            try
            {
                var reader = _saveChannel.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var op))
                    {
                        try
                        {
                            lock (_inProcessSaveGate) { ExecuteSave(op.Snapshot, op.KeepExistingBackup); if (!_lastSaveSucceeded) throw new TimeoutException("Timed out waiting for the settings lock."); }
                            op.Tcs.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            op.Tcs.TrySetException(ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogCategorySave, $"Save worker loop exited unexpectedly: {ex}");
            }
        });
    }

    public SettingsService()
    {
        _appFolder = AppPaths.RoamingAppDataRoot;

        if (!Directory.Exists(_appFolder))
        {
            Directory.CreateDirectory(_appFolder);
        }

        _settingsPath = Path.Combine(_appFolder, "settings.json");
        _mutexName = CreateMutexName(_settingsPath);
        _apiKeySaveWarning = ShowApiKeySaveWarning;
        _worker = StartWorker();
    }

    // Test seam: production callers always use the APPDATA location and WPF notice.
    internal SettingsService(string settingsPath, Action<string>? apiKeySaveWarning = null)
    {
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _appFolder = Path.GetDirectoryName(_settingsPath)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(settingsPath));
        Directory.CreateDirectory(_appFolder);
        _mutexName = CreateMutexName(_settingsPath);
        _apiKeySaveWarning = apiKeySaveWarning ?? ShowApiKeySaveWarning;
        _worker = StartWorker();
    }

    private readonly string _mutexName;

    private static string CreateMutexName(string settingsPath) =>
        @"Local\VNotch_Settings_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath).ToUpperInvariant())));

    public NotchSettings Load()
    {
        ThrowIfDisposed();

        lock (_loadLock)
        {
            var stamp = GetSettingsStamp();
            lock (_cacheLock)
            {
                if (stamp != null && stamp == _cachedStamp && _cachedSettings != null)
                    return _cachedSettings.Clone();
            }

            var settings = LoadFromDisk(out bool cacheable);
            // A migration/save may have replaced the file while loading. Its save
            // transaction already publishes the matching cache snapshot.
            if (cacheable && stamp != null && stamp == GetSettingsStamp())
                CacheSettings(settings, stamp);
            return settings;
        }
    }

    private (long Length, DateTime WrittenUtc)? GetSettingsStamp()
    {
        try
        {
            var file = new FileInfo(_settingsPath);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void CacheSettings(NotchSettings settings, (long Length, DateTime WrittenUtc)? stamp)
    {
        lock (_cacheLock)
        {
            _cachedSettings = settings.Clone();
            _cachedStamp = stamp;
        }
    }

    private NotchSettings LoadFromDisk(out bool cacheable)
    {
        cacheable = false;
        WindowTitleScanner.InvalidateInspectionAllowed();

        if (!File.Exists(_settingsPath))
        {
            _settingsLoadFailed = false;
            var fresh = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
            Save(fresh);
            return fresh;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(_settingsPath);
        }
        catch (Exception ex)
        {

            RuntimeLog.Log(LogCategoryLoad, $"Failed to read {_settingsPath}: {ex}");
            _settingsLoadFailed = true;
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion, EnableBrowserUrlInspection = false };
        }

        try
        {
            var (settings, migrated) = SettingsMigrator.Migrate(raw);
            _settingsLoadFailed = false;
            bool normalized = NormalizeSettings(settings);
            bool migrationSaved = true;

            if (migrated || normalized)
            {
                RuntimeLog.Log(
                    LogCategoryLoad,
                    $"Migrated/normalized settings to version {SettingsMigrator.CurrentVersion}");
                try
                {
                    bool savedOk; lock (_inProcessSaveGate) { Save(settings, keepExistingBackup: true); savedOk = _lastSaveSucceeded; }
                    if (!savedOk) migrationSaved = false;
                    if (migrated && savedOk)
                        RemovePlaintextKeySettingsFiles();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked/read-only file must not discard settings that were
                    // successfully loaded. Retry the migration on the next Load.
                    migrationSaved = false;
                    RuntimeLog.Error(LogCategoryLoad, ex, "Could not persist migrated settings; keeping the loaded preferences and the existing file.");
                }
            }

            WindowTitleScanner.UpdateInspectionAllowed(settings.EnableBrowserUrlInspection);
            if (migrationSaved) PruneSettingsBackups();
            cacheable = migrationSaved;
            return settings;
        }
        catch (JsonException ex)
        {
            var backupPath = QuarantineCorruptFile(raw, ex);
            RuntimeLog.Log(
                LogCategoryLoad,
                $"Corrupt settings file detected. Backed up to '{backupPath}'. Falling back to defaults. Error: {ex.Message}");

            var defaults = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion, EnableBrowserUrlInspection = false };
            if (backupPath == "<backup-failed>")
            {
                _settingsLoadFailed = true;
                return defaults;
            }
            _settingsLoadFailed = false;
            Save(defaults);
            return defaults;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _settingsLoadFailed = true;
            // A legacy key could not be protected. Do not touch the existing file.
            RuntimeLog.Error(LogCategoryLoad, "DPAPI encryption failed; legacy settings were left unchanged.");
            _apiKeySaveWarning("Your API keys could not be protected or read. Existing settings were preserved. Restore Windows credential access and reload settings before saving.");
            try
            {
                var recovered = ImportSettingsFromString(raw).Settings;
                WindowTitleScanner.UpdateInspectionAllowed(recovered.EnableBrowserUrlInspection);
                return recovered;
            }
            catch (Exception recoveryError) when (recoveryError is JsonException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion, EnableLocalOnlyMode = true, EnableBrowserUrlInspection = false };
            }
        }
        catch (Exception ex)
        {

            RuntimeLog.Log(LogCategoryLoad, $"Unexpected error while loading settings: {ex}");
            _settingsLoadFailed = true;
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion, EnableBrowserUrlInspection = false };
        }
    }

    public void Save(NotchSettings settings)
    {
        Save(settings, keepExistingBackup: true);
    }

    public Task SaveAsync(NotchSettings settings)
    {
        return SaveAsync(settings, keepExistingBackup: true);
    }

    public async Task SaveAsync(NotchSettings settings, bool keepExistingBackup)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        ThrowIfDisposed();

        var snapshot = settings.Clone();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _saveChannel.Writer.WriteAsync(new SaveOperation(snapshot, keepExistingBackup, tcs)).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(SettingsService), "SettingsService has been disposed.");
        }

        await tcs.Task.ConfigureAwait(false);
    }

    private void Save(NotchSettings settings, bool keepExistingBackup)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        // Loading/migrating a file is a synchronous operation. Interactive callers
        // use SaveAsync; the legacy API writes directly instead of blocking its worker.
        lock (_inProcessSaveGate) { ExecuteSave(settings.Clone(), keepExistingBackup); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
        {
            await _worker.ConfigureAwait(false);
            return;
        }

        _disposed = true;
        _saveChannel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!disposing) return;

        // Wait for queued saves to reach disk so they are not lost on exit. The worker
        // runs on the thread pool and never needs the UI dispatcher, so blocking here
        // cannot deadlock; the timeout bounds shutdown if the disk stalls.
        try
        {
            if (!DisposeAsync().AsTask().Wait(DisposeDrainTimeout))
                RuntimeLog.Log("SETTINGS-DISPOSE", $"Pending saves did not finish within {DisposeDrainTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-DISPOSE", ex, "Save worker failed while draining");
        }
    }

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SaveMutexTimeout = TimeSpan.FromSeconds(5);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SettingsService), "SettingsService has been disposed.");
    }

    private void ExecuteSave(NotchSettings settings, bool keepExistingBackup)
    {
        if (_settingsLoadFailed)
            throw new System.Security.Cryptography.CryptographicException("Reload settings successfully before overwriting data that could not be read or backed up.");
        // Serialize the replace and backup transaction across service instances/processes.
        _lastSaveSucceeded = false; using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(SaveMutexTimeout); }
        catch (AbandonedMutexException) { acquired = true; /* The previous writer exited; this thread owns the mutex. */ }

        if (!acquired)
        {
            RuntimeLog.Error(LogCategorySave, $"Timed out after {SaveMutexTimeout.TotalSeconds:0}s waiting for the settings lock; save skipped.");
            return;
        }

        try { ExecuteSaveCore(settings, keepExistingBackup); _lastSaveSucceeded = true; }
        finally { mutex.ReleaseMutex(); }
    }

    private void ExecuteSaveCore(NotchSettings settings, bool keepExistingBackup)
    {
        var tempPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            SafeFileDeletion.ValidatePath(_settingsPath);
            settings.SettingsVersion = SettingsMigrator.CurrentVersion;

            var json = JsonSerializer.Serialize(settings, SaveJsonOptions);

            File.WriteAllText(tempPath, json);

            if (File.Exists(_settingsPath))
            {
                // Keep a rolling history of the previous on-disk state so a bad
                // overwrite (e.g. losing hand-tuned values) can always be recovered.
                if (keepExistingBackup)
                    BackupExistingSettings();
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _settingsPath);
            }

            WindowTitleScanner.UpdateInspectionAllowed(settings.EnableBrowserUrlInspection);
            CacheSettings(settings, GetSettingsStamp());
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // DPAPI encryption failed; preserve existing settings file and notify
            // user that API keys could not be saved.
            RuntimeLog.Error(LogCategorySave, "DPAPI encryption failed — settings were not saved.");
            DispatchSaveWarning(Loc.Get("error.apiKeyEncrypt"));
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategorySave, ex.ToString());
            throw;
        }
        finally
        {
            // Clean up stale temporary files before writing so failed saves never
            // leave incomplete sensitive artifacts.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception)
            {
                // Ignore cleanup failure for temporary save file
            }
        }
    }

    private void DispatchSaveWarning(string message)
    {
        if (_apiKeySaveWarning != ShowApiKeySaveWarning)
        {
            _apiKeySaveWarning(message);
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher != null &&
            !System.Windows.Application.Current.Dispatcher.HasShutdownStarted &&
            !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => _apiKeySaveWarning(message));
        }
        else
        {
            _apiKeySaveWarning(message);
        }
    }

    private static void ShowApiKeySaveWarning(string message) =>
        System.Windows.MessageBox.Show(message, Loc.Get("error.settingsNotSavedTitle"),
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    private void RemovePlaintextKeySettingsFiles()
    {
        // Preserve recovery content and unknown fields; only protect plaintext credentials.
        foreach (var path in Directory.GetFiles(_appFolder, "settings*.json"))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_settingsPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsGeneratedSettingsBackup(path) && Path.GetFileName(path) != "settings.bak-legacy.json") continue;
                SafeFileDeletion.ValidatePath(path);
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document) continue;
                bool changed = false;
                foreach (string keyName in new[]
                {
                    nameof(NotchSettings.YouTubeApiKey),
                    nameof(NotchSettings.SpotlightOpenAIApiKey),
                    nameof(NotchSettings.SpotlightGeminiApiKey),
                    nameof(NotchSettings.SpotlightClaudeApiKey),
                    nameof(NotchSettings.SpotlightDeepSeekApiKey),

                    nameof(NotchSettings.SpotifySpDc),
                    "PaxSenixApiKey",
                })
                {
                    if (document[keyName] is JsonValue key && key.TryGetValue<string>(out var value) &&
                        !string.IsNullOrEmpty(value) && !DataProtection.IsEncrypted(value))
                    {
                        document[keyName] = DataProtection.Protect(value);
                        changed = true;
                    }
                }
                if (changed)
                {
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, document.ToJsonString());
                        SafeFileDeletion.ValidatePath(path);
                        File.Replace(temporary, path, null);
                    }
                    finally { SafeFileDeletion.DeleteFile(temporary); }
                }
            }
            catch (JsonException)
            {
                // Corrupt files are handled by Load's existing quarantine path.
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(LogCategoryLoad, $"Unable to remove an unsafe legacy settings artifact: {ex.GetType().Name}");
            }
        }
    }

    private const int MaxSettingsBackups = 10;

    private static bool IsGeneratedSettingsBackup(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.StartsWith("settings.bak-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        string stamp = name[13..^5];
        if (stamp.Length == 52 && stamp[19] == '-' && Guid.TryParseExact(stamp[20..], "N", out _)) stamp = stamp[..19];
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }

    private void BackupExistingSettings()
    {
        try
        {
            string current = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(current)) return;

            var existing = Directory.GetFiles(_appFolder, "settings.bak-*.json").Where(IsGeneratedSettingsBackup).ToArray();
            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            if (existing.Length > 0)
            {
                try
                {
                    if (File.ReadAllText(existing[^1]) == current)
                        return; // unchanged since last backup
                }
                catch (Exception)
                {
                    // Unreadable backup — proceed to write a fresh one
                }
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            string backup = Path.Combine(_appFolder, $"settings.bak-{timestamp}-{Guid.NewGuid():N}.json");
            SafeFileDeletion.ValidatePath(backup);
            using (var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file)) writer.Write(current);

            PruneSettingsBackups();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategorySave, $"Settings backup skipped: {ex.Message}");
            throw new IOException("Settings were not replaced because the recovery backup could not be saved.", ex);
        }
    }

    private void PruneSettingsBackups()
    {
        try
        {
            var existing = Directory.GetFiles(_appFolder, "settings.bak-*.json").Where(IsGeneratedSettingsBackup).ToArray();
            if (existing.Length > MaxSettingsBackups)
            {
                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < existing.Length - MaxSettingsBackups; i++)
                {
                    try
                    {
                        SafeFileDeletion.DeleteFile(existing[i]);
                    }
                    catch (Exception)
                    {
                        // Best effort cleanup of old backups
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategorySave, $"Settings backup skipped: {ex.Message}");
        }
    }

    private static bool NormalizeSettings(NotchSettings settings)
    {
        bool changed = false;

        var normParams = new VnSettingsNormalizationParams
        {
            Width = settings.Width,
            Height = settings.Height,
            CornerRadius = settings.CornerRadius,
            DynamicIslandWidth = settings.DynamicIslandWidth,
            DynamicIslandHeight = settings.DynamicIslandHeight,
            NotificationDuration = settings.NotificationDuration,
            AnimationFps = settings.AnimationFps,
            MinFps = AnimationConfig.MinFps,
            MaxFps = AnimationConfig.MaxFps,
            SpotifyCanvasBrightness = settings.SpotifyCanvasBrightness,
            Opacity = settings.Opacity
        };

        if (vn_settings_service_normalize_numeric(ref normParams, out byte numericChanged) == 0 && numericChanged != 0)
        {
            settings.Width = normParams.Width;
            settings.Height = normParams.Height;
            settings.CornerRadius = normParams.CornerRadius;
            settings.DynamicIslandWidth = normParams.DynamicIslandWidth;
            settings.DynamicIslandHeight = normParams.DynamicIslandHeight;
            settings.NotificationDuration = normParams.NotificationDuration;
            settings.AnimationFps = normParams.AnimationFps;
            settings.SpotifyCanvasBrightness = normParams.SpotifyCanvasBrightness;
            settings.Opacity = normParams.Opacity;
            changed = true;
        }

        string normalizedPriority = ProcessPriorityPolicy.Normalize(settings.ProcessPriority);
        if (!string.Equals(settings.ProcessPriority, normalizedPriority, StringComparison.Ordinal))
        {
            settings.ProcessPriority = normalizedPriority;
            changed = true;
        }

        ReadOnlySpan<char> langSpan = settings.Language is not null ? settings.Language.AsSpan() : ReadOnlySpan<char>.Empty;
        bool langValid = vn_settings_service_is_language_valid(langSpan, langSpan.Length, out byte outLangValid) == 0
            && outLangValid != 0;
        if (!langValid)
        {
            settings.Language = "en";
            changed = true;
        }

        if (settings.ManualCity != null)
        {
            Span<char> cityBuf = stackalloc char[100];
            ReadOnlySpan<char> citySpan = settings.ManualCity.AsSpan();
            if (vn_settings_service_clean_manual_city(citySpan, citySpan.Length, cityBuf, cityBuf.Length, out int cleanLen) == 0)
            {
                ReadOnlySpan<char> cleanSpan = cityBuf[..cleanLen];
                if (!cleanSpan.SequenceEqual(citySpan))
                {
                    settings.ManualCity = new string(cleanSpan);
                    changed = true;
                }
            }
        }

        return changed;
    }
    public void ExportSettingsToFile(string filePath, NotchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        string json = ExportSettingsToString(settings);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(filePath, json);
    }

    public string ExportSettingsToString(NotchSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        var clone = settings.Clone();
        clone.SettingsVersion = SettingsMigrator.CurrentVersion;

        clone.SpotlightOpenAIApiKey = "";
        clone.SpotlightGeminiApiKey = "";
        clone.SpotlightClaudeApiKey = "";
        clone.SpotlightDeepSeekApiKey = "";
        clone.YouTubeApiKey = "";
        clone.SpotifySpDc = "";
        SpotifyCanvasConsent.Revoke(clone);

        var jsonNode = JsonSerializer.SerializeToNode(clone, new JsonSerializerOptions { WriteIndented = true });
        if (jsonNode is not JsonObject settingsObj)
        {
            settingsObj = new JsonObject();
        }

        // Exclude sensitive credentials from export to prevent accidental leakage;
        // users re-authenticate on destination machines.
        settingsObj.Remove(nameof(NotchSettings.SpotlightOpenAIApiKey));
        settingsObj.Remove(nameof(NotchSettings.SpotlightGeminiApiKey));
        settingsObj.Remove(nameof(NotchSettings.SpotlightClaudeApiKey));
        settingsObj.Remove(nameof(NotchSettings.SpotlightDeepSeekApiKey));
        settingsObj.Remove(nameof(NotchSettings.YouTubeApiKey));
        settingsObj.Remove(nameof(NotchSettings.SpotifySpDc));

        var rootObj = new JsonObject
        {
            ["format"] = "vns",
            ["fileVersion"] = 1,
            ["appVersion"] = GetAppVersion(),
            ["exportedAt"] = DateTime.UtcNow.ToString("o"),
            ["settings"] = settingsObj
        };

        return rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Settings file not found", filePath);

        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxImportPayloadBytes)
            throw new InvalidOperationException("Settings payload exceeds maximum allowed size (2 MB).");
        using var reader = new StreamReader(input);
        var buffer = new char[MaxImportPayloadBytes + 1];
        int length = 0, read;
        while (length < buffer.Length && (read = reader.Read(buffer, length, buffer.Length - length)) > 0) length += read;
        string rawJson = new(buffer, 0, length);
        return ImportSettingsFromString(rawJson, currentSettings);
    }

    private const int MaxImportPayloadBytes = 2 * 1024 * 1024; // 2 MB limit

    public static (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromString(string rawJson, NotchSettings? currentSettings = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new JsonException("Settings file content is empty");

        if (rawJson.Length > MaxImportPayloadBytes)
            throw new InvalidOperationException("Settings payload exceeds maximum allowed size (2 MB).");

        var node = JsonNode.Parse(rawJson)
                   ?? throw new JsonException("Failed to parse settings JSON");

        if (node is JsonObject envelope && envelope["settings"] is { } innerSettings) node = innerSettings;
        if (node is not JsonObject imported) throw new JsonException("Settings must be an object.");
        // Imported credentials are ignored before any decryption or legacy migration.
        foreach (string name in new[] { nameof(NotchSettings.YouTubeApiKey), nameof(NotchSettings.SpotifySpDc),
            nameof(NotchSettings.SpotlightOpenAIApiKey), nameof(NotchSettings.SpotlightGeminiApiKey),
            nameof(NotchSettings.SpotlightClaudeApiKey), nameof(NotchSettings.SpotlightDeepSeekApiKey) }) imported.Remove(name);
        var (settings, _) = SettingsMigrator.Migrate(imported.ToJsonString());

        // Security requirement: Treat imported settings as untrusted. Never import credentials.
        // Keep the local user's existing credentials intact.
        settings.SpotlightOpenAIApiKey = currentSettings?.SpotlightOpenAIApiKey ?? "";
        settings.SpotlightGeminiApiKey = currentSettings?.SpotlightGeminiApiKey ?? "";
        settings.SpotlightClaudeApiKey = currentSettings?.SpotlightClaudeApiKey ?? "";
        settings.SpotlightDeepSeekApiKey = currentSettings?.SpotlightDeepSeekApiKey ?? "";
        settings.YouTubeApiKey = currentSettings?.YouTubeApiKey ?? "";
        settings.SpotifySpDc = currentSettings?.SpotifySpDc ?? "";

        // Consent belongs to the local interaction, never to the imported file.
        SpotifyCanvasConsent.Revoke(settings);

        NormalizeSettings(settings);

        bool requiresRestart = CheckRequiresRestart(settings, currentSettings);
        return (settings, requiresRestart);
    }

    public static bool CheckRequiresRestart(NotchSettings imported, NotchSettings? current)
    {
        if (current == null) return false;

        ReadOnlySpan<char> impPri = imported.ProcessPriority is not null ? imported.ProcessPriority.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> curPri = current.ProcessPriority is not null ? current.ProcessPriority.AsSpan() : ReadOnlySpan<char>.Empty;

        if (vn_settings_service_check_requires_restart(
                imported.GpuPreference,
                current.GpuPreference,
                impPri,
                impPri.Length,
                curPri,
                curPri.Length,
                out byte outRestart) == 0)
        {
            return outRestart != 0;
        }

        if (imported.GpuPreference != current.GpuPreference)
            return true;

        if (!string.Equals(imported.ProcessPriority, current.ProcessPriority, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string GetAppVersion() => AppVersion.Current;

    private string QuarantineCorruptFile(string rawContents, Exception reason)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(_appFolder, $"settings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
            SafeFileDeletion.ValidatePath(backupPath);
            using (var file = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file)) writer.Write(rawContents);
            return backupPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(
                LogCategoryLoad,
                $"Failed to write corrupt-settings backup (original error: {reason.Message}): {ex}");
            return "<backup-failed>";
        }
    }
}
