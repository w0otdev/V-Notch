using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class DataProtectionSettingsTests : IDisposable
{
    private readonly Func<byte[], byte[]> _originalProtectBytes = DataProtection.ProtectBytes;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VNotchTests", Guid.NewGuid().ToString("N"));

    public DataProtectionSettingsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ProtectAndUnprotect_RoundTripsValue()
    {
        const string key = "test-api-key";

        var encrypted = DataProtection.Protect(key);

        Assert.StartsWith("enc:", encrypted);
        Assert.NotEqual(key, encrypted);
        Assert.Equal(key, DataProtection.Unprotect(encrypted));
    }

    [Fact]
    public void Unprotect_CorruptData_ThrowsCryptographicException()
    {
        Assert.Throws<CryptographicException>(() => DataProtection.Unprotect("enc:not-valid-base64"));
    }

    [Fact]
    public void Load_PlaintextLegacyKey_MigratesAndRewritesEncryptedValue()
    {
        const string key = "legacy-api-key";
        var path = Path.Combine(_directory, "settings.json");
        var legacyBackup = Path.Combine(_directory, "settings.bak-legacy.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SettingsVersion = SettingsMigrator.CurrentVersion,
            YouTubeApiKey = key
        }));
        File.Copy(path, legacyBackup);

        var settings = new SettingsService(path, _ => { }).Load();
        var stored = JsonDocument.Parse(File.ReadAllText(path)).RootElement
            .GetProperty(nameof(NotchSettings.YouTubeApiKey)).GetString();

        Assert.Equal(key, settings.YouTubeApiKey);
        Assert.NotNull(stored);
        Assert.StartsWith("enc:", stored);
        Assert.DoesNotContain(key, File.ReadAllText(path));
        Assert.False(File.Exists(legacyBackup));
    }

    [Fact]
    public void ProtectFailure_DoesNotCreateOrOverwriteSettingsContainingKey()
    {
        const string key = "must-never-be-written";
        var path = Path.Combine(_directory, "settings.json");
        var original = "{\"SettingsVersion\":9,\"YouTubeApiKey\":\"enc:existing\"}";
        File.WriteAllText(path, original);
        string? warning = null;
        DataProtection.ProtectBytes = _ => throw new CryptographicException("simulated DPAPI failure");

        new SettingsService(path, message => warning = message).Save(new NotchSettings { YouTubeApiKey = key });

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Contains("API key", warning);
        Assert.DoesNotContain(key, string.Concat(Directory.GetFiles(_directory, "settings*.json").Select(File.ReadAllText)));
    }

    [Fact]
    public void Save_ReplacesSettingsAtomically_AndKeepsPreviousVersion()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(path, _ => { });
        service.Save(new NotchSettings { Width = 400 });
        service.Save(new NotchSettings { Width = 500 });

        var current = JsonSerializer.Deserialize<NotchSettings>(File.ReadAllText(path));
        var backup = Directory.GetFiles(_directory, "settings.bak-*.json").Single();
        var previous = JsonSerializer.Deserialize<NotchSettings>(File.ReadAllText(backup));
        Assert.Equal(500, current!.Width);
        Assert.Equal(400, previous!.Width);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_CorruptSettings_QuarantinesFileAndRestoresDefaults()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ corrupt");

        var settings = new SettingsService(path, _ => { }).Load();

        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Single(Directory.GetFiles(_directory, "settings.corrupt-*.json"));
        Assert.Equal(SettingsMigrator.CurrentVersion, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("SettingsVersion").GetInt32());
    }

    [Fact]
    public void Save_RetainsAtMostTenBackups()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(path, _ => { });
        for (var width = 100; width < 112; width++)
        {
            service.Save(new NotchSettings { Width = width });
            Thread.Sleep(2);
        }

        Assert.Equal(10, Directory.GetFiles(_directory, "settings.bak-*.json").Length);
    }

    [Fact]
    public void Save_WhenWriteOrReplaceFails_ThrowsExceptionAndPreservesOriginalFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        var original = "{\"SettingsVersion\":9,\"Width\":400}";
        File.WriteAllText(path, original);
        var service = new SettingsService(path, _ => { });

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => service.Save(new NotchSettings { Width = 500 }));
        }

        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenWriteOrReplaceFails_FaultsTaskAndPreservesOriginalFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        var original = "{\"SettingsVersion\":9,\"Width\":400}";
        File.WriteAllText(path, original);
        var service = new SettingsService(path, _ => { });

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(new NotchSettings { Width = 500 }));
        }

        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_WhenMigrationSaveFails_PreservesPlaintextKeyFileWithoutCleanup()
    {
        const string key = "legacy-plaintext-key";
        var path = Path.Combine(_directory, "settings.json");
        var legacyBackup = Path.Combine(_directory, "settings.bak-legacy.json");
        var original = JsonSerializer.Serialize(new
        {
            SettingsVersion = SettingsMigrator.CurrentVersion,
            YouTubeApiKey = key
        });
        File.WriteAllText(path, original);
        File.Copy(path, legacyBackup);

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var settings = new SettingsService(path, _ => { }).Load();
            Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        }

        Assert.True(File.Exists(path));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(File.Exists(legacyBackup));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        DataProtection.ProtectBytes = _originalProtectBytes;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
