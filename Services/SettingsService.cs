using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VNotch.Models;

namespace VNotch.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private readonly string _appFolder;
    private readonly Action<string> _apiKeySaveWarning;

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appDataPath, "V-Notch");

        if (!Directory.Exists(_appFolder))
        {
            Directory.CreateDirectory(_appFolder);
        }

        _settingsPath = Path.Combine(_appFolder, "settings.json");
        _apiKeySaveWarning = ShowApiKeySaveWarning;
    }

    // Test seam: production callers always use the APPDATA location and WPF notice.
    internal SettingsService(string settingsPath, Action<string>? apiKeySaveWarning = null)
    {
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _appFolder = Path.GetDirectoryName(_settingsPath)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(settingsPath));
        Directory.CreateDirectory(_appFolder);
        _apiKeySaveWarning = apiKeySaveWarning ?? ShowApiKeySaveWarning;
    }

    public NotchSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {

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

            RuntimeLog.Log("SETTINGS-LOAD", $"Failed to read {_settingsPath}: {ex}");
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        }

        try
        {
            var (settings, migrated) = SettingsMigrator.Migrate(raw);
            bool normalized = NormalizeSettings(settings);

            if (migrated || normalized)
            {
                RuntimeLog.Log(
                    "SETTINGS-LOAD",
                    $"Migrated/normalized settings to version {SettingsMigrator.CurrentVersion}");
                Save(settings, keepExistingBackup: !migrated);
                if (migrated)
                    RemovePlaintextKeySettingsFiles();
            }

            return settings;
        }
        catch (JsonException ex)
        {
            var backupPath = QuarantineCorruptFile(raw, ex);
            RuntimeLog.Log(
                "SETTINGS-LOAD",
                $"Corrupt settings file detected. Backed up to '{backupPath}'. Falling back to defaults. Error: {ex.Message}");

            var defaults = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
            Save(defaults);
            return defaults;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A legacy key could not be protected. Do not touch the existing file.
            RuntimeLog.Error("SETTINGS-LOAD", "DPAPI encryption failed; legacy settings were left unchanged.");
            _apiKeySaveWarning("Your API key was not saved because Windows DPAPI could not encrypt it. Your existing settings file was left unchanged.");
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        }
        catch (Exception ex)
        {

            RuntimeLog.Log("SETTINGS-LOAD", $"Unexpected error while loading settings: {ex}");
            return new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        }
    }

    public void Save(NotchSettings settings)
    {
        Save(settings, keepExistingBackup: true);
    }

    private void Save(NotchSettings settings, bool keepExistingBackup)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var tempPath = _settingsPath + ".tmp";

        try
        {
            settings.SettingsVersion = SettingsMigrator.CurrentVersion;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);

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
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // DPAPI encryption failed — do NOT overwrite the existing settings file.
            // The old file remains intact. Notify the user so they know the API keys
            // was not saved.
            RuntimeLog.Error("SETTINGS-SAVE", "DPAPI encryption failed — settings were not saved.");
            _apiKeySaveWarning(Loc.Get("error.apiKeyEncrypt"));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-SAVE", ex.ToString());
            System.Windows.MessageBox.Show(Loc.Get("error.settingsSave", ex.Message), Loc.Get("error.title"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            // Serialization happens before writing this file, but remove any stale
            // temporary output so an interrupted/failed save can never be mistaken
            // for a settings file containing sensitive data.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void ShowApiKeySaveWarning(string message) =>
        System.Windows.MessageBox.Show(message, Loc.Get("error.settingsNotSavedTitle"),
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    private void RemovePlaintextKeySettingsFiles()
    {
        // A backup made by an older application release may still contain a
        // plaintext key. Once migration has succeeded, remove only those unsafe
        // settings artifacts; encrypted backups remain available for recovery.
        foreach (var path in Directory.GetFiles(_appFolder, "settings*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                bool hasPlaintextKey = new[]
                {
                    nameof(NotchSettings.YouTubeApiKey),
                    nameof(NotchSettings.SpotifySpDc),
                    "PaxSenixApiKey",
                }.Any(keyName =>
                    document.RootElement.TryGetProperty(keyName, out var key)
                    && key.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(key.GetString())
                    && !DataProtection.IsEncrypted(key.GetString()));
                if (hasPlaintextKey)
                {
                    File.Delete(path);
                }
            }
            catch (JsonException)
            {
                // Corrupt files are handled by Load's existing quarantine path.
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("SETTINGS-LOAD", $"Unable to remove an unsafe legacy settings artifact: {ex.GetType().Name}");
            }
        }
    }

    private const int MaxSettingsBackups = 10;

    /// <summary>
    /// Snapshots the current settings.json into a timestamped backup before it is
    /// overwritten, keeping the most recent <see cref="MaxSettingsBackups"/>. Skips
    /// writing a new backup when the content is identical to the latest one so the
    /// history stays meaningful instead of filling with duplicates.
    /// </summary>
    private void BackupExistingSettings()
    {
        try
        {
            string current = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(current)) return;

            var existing = Directory.GetFiles(_appFolder, "settings.bak-*.json");
            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            if (existing.Length > 0)
            {
                try
                {
                    if (File.ReadAllText(existing[^1]) == current)
                        return; // unchanged since last backup
                }
                catch { /* unreadable backup — proceed to write a fresh one */ }
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.WriteAllText(Path.Combine(_appFolder, $"settings.bak-{timestamp}.json"), current);

            existing = Directory.GetFiles(_appFolder, "settings.bak-*.json");
            if (existing.Length > MaxSettingsBackups)
            {
                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < existing.Length - MaxSettingsBackups; i++)
                {
                    try { File.Delete(existing[i]); } catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SETTINGS-SAVE", $"Settings backup skipped: {ex.Message}");
        }
    }

    private static bool NormalizeSettings(NotchSettings settings)
    {
        bool changed = false;

        if (settings.DynamicIslandWidth < 100)
        {
            settings.DynamicIslandWidth = (int)Math.Round(settings.Width * 1.12 / 10.0) * 10;
            changed = true;
        }

        if (settings.DynamicIslandHeight < 24)
        {
            settings.DynamicIslandHeight = 40;
            changed = true;
        }

        double canvasBrightness = Math.Clamp(settings.SpotifyCanvasBrightness, 0.2, 1.0);
        if (Math.Abs(canvasBrightness - settings.SpotifyCanvasBrightness) > double.Epsilon)
        {
            settings.SpotifyCanvasBrightness = canvasBrightness;
            changed = true;
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

        var jsonNode = JsonSerializer.SerializeToNode(clone, new JsonSerializerOptions { WriteIndented = true });
        if (jsonNode is not JsonObject settingsObj)
        {
            settingsObj = new JsonObject();
        }

        // Ensure keys are portable across machines (unprotected plaintext in .vns)
        settingsObj[nameof(NotchSettings.YouTubeApiKey)] = clone.YouTubeApiKey ?? "";
        settingsObj[nameof(NotchSettings.SpotifySpDc)] = clone.SpotifySpDc ?? "";

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

        string rawJson = File.ReadAllText(filePath);
        return ImportSettingsFromString(rawJson, currentSettings);
    }

    public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromString(string rawJson, NotchSettings? currentSettings = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new JsonException("Settings file content is empty");

        var node = JsonNode.Parse(rawJson)
                   ?? throw new JsonException("Failed to parse settings JSON");

        string settingsJsonToMigrate;

        // Check if this is an envelope package format (.vns)
        if (node is JsonObject rootObj && rootObj.TryGetPropertyValue("settings", out var innerSettings) && innerSettings != null)
        {
            settingsJsonToMigrate = innerSettings.ToJsonString();
        }
        else
        {
            // Direct settings JSON format
            settingsJsonToMigrate = rawJson;
        }

        var (settings, _) = SettingsMigrator.Migrate(settingsJsonToMigrate);
        NormalizeSettings(settings);

        bool requiresRestart = CheckRequiresRestart(settings, currentSettings);
        return (settings, requiresRestart);
    }

    public static bool CheckRequiresRestart(NotchSettings imported, NotchSettings? current)
    {
        if (current == null) return false;

        // GPU Preference requires restart because DXGI device adapter is bound on launch
        if (imported.GpuPreference != current.GpuPreference)
            return true;

        // Process Priority applies at startup
        if (!string.Equals(imported.ProcessPriority, current.ProcessPriority, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string GetAppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v != null
            ? (v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}")
            : "1.9.1";
    }

    private string QuarantineCorruptFile(string rawContents, Exception reason)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(_appFolder, $"settings.corrupt-{timestamp}.json");
            File.WriteAllText(backupPath, rawContents);
            return backupPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(
                "SETTINGS-LOAD",
                $"Failed to write corrupt-settings backup (original error: {reason.Message}): {ex}");
            return "<backup-failed>";
        }
    }
}
