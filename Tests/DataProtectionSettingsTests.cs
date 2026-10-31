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

[CollectionDefinition("DataProtection overrides", DisableParallelization = true)]
public sealed class DataProtectionOverrideCollection { }

// Failure injection changes a process-wide delegate used by settings tests.
[Collection("DataProtection overrides")]
public sealed class DataProtectionSettingsTests : IDisposable
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
    public void ProtectBytesNative_And_UnprotectBytesNative_RoundTrip_EmptySmallAndLargePayloads()
    {
        int[] sizes = [0, 1, 32, 512, 4096, 16384];
        foreach (int size in sizes)
        {
            byte[] plain = new byte[size];
            for (int i = 0; i < size; i++)
            {
                plain[i] = (byte)((i * 31 + 17) & 0xFF);
            }

            byte[] cipher = DataProtection.ProtectBytesNative(plain);
            Assert.True(cipher.Length > plain.Length);

            byte[] roundTripped = DataProtection.UnprotectBytesNative(cipher);
            Assert.Equal(plain, roundTripped);
        }

        Assert.Throws<CryptographicException>(() => DataProtection.UnprotectBytesNative(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ProtectAndUnprotect_LargeUnicodePayload_RoundTripsSinglePass()
    {
        string payload = string.Concat(Enumerable.Repeat("V-Notch-Secret-Khóa-Bí-Mật-🔒-0123456789;", 120));
        string encrypted = DataProtection.Protect(payload);

        Assert.StartsWith("enc:", encrypted);
        Assert.Equal(payload, DataProtection.Unprotect(encrypted));
    }

    [Fact]
    public void NativeDataProtection_AlwaysSecureZeroesDataBlobBeforeLocalFree_AndUsesSinglePassBuffers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "V-Notch.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        string cppSource = File.ReadAllText(Path.Combine(dir!.FullName, "Native", "Services", "DataProtection.cpp"));
        Assert.Contains("SecureZeroMemory(out_blob.pbData, out_blob.cbData);", cppSource, StringComparison.Ordinal);
        Assert.DoesNotContain("struct BlobGuard", cppSource, StringComparison.Ordinal);
        Assert.Contains("catch (...)", cppSource, StringComparison.Ordinal);

        string csSource = File.ReadAllText(Path.Combine(dir.FullName, "Services", "DataProtection.cs"));
        Assert.Contains("ArrayPool<byte>.Shared.Rent", csSource, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(rented)", csSource, StringComparison.Ordinal);
        Assert.DoesNotContain("null, 0, out int reqLen", csSource, StringComparison.Ordinal);
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
        using var backup = JsonDocument.Parse(File.ReadAllText(legacyBackup));
        string protectedKey = backup.RootElement.GetProperty(nameof(NotchSettings.YouTubeApiKey)).GetString()!;
        Assert.StartsWith("enc:", protectedKey);
        Assert.Equal(key, DataProtection.Unprotect(protectedKey));
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

        using var service = new SettingsService(path, message => warning = message);
        Assert.Throws<CryptographicException>(() => service.Save(new NotchSettings { YouTubeApiKey = key }));

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
    public async Task Save_RetainsAtMostTenBackups()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(path, _ => { });
        for (var width = 100; width < 112; width++)
        {
            await service.SaveAsync(new NotchSettings { Width = width });
            await Task.Delay(2);
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
        await File.WriteAllTextAsync(path, original);
        var service = new SettingsService(path, _ => { });

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(new NotchSettings { Width = 500 }));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
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
            YouTubeApiKey = key,
            Language = "vi",
            Width = 270
        });
        File.WriteAllText(path, original);
        File.Copy(path, legacyBackup);

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var service = new SettingsService(path, _ => { });
            var settings = service.Load();
            Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
            Assert.Equal("vi", settings.Language);
            Assert.Equal(270, settings.Width);
            Assert.Equal(key, settings.YouTubeApiKey);
        }

        Assert.True(File.Exists(path));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(File.Exists(legacyBackup));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_WithSpotifySpDc_IsEncryptedOnDisk()
    {
        const string cookie = "test-spotify-sp-dc-cookie";
        var path = Path.Combine(_directory, "settings-spotify.json");
        var service = new SettingsService(path, _ => { });

        service.Save(new NotchSettings { SpotifySpDc = cookie });

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(cookie, raw);
        Assert.Contains("enc:", raw);

        var loaded = service.Load();
        Assert.Equal(cookie, loaded.SpotifySpDc);
    }

    [Fact]
    public void ProtectFailure_WithSpotifySpDc_DoesNotCreateOrOverwriteSettingsContainingCookie()
    {
        const string cookie = "sensitive-sp-dc-must-not-leak";
        var path = Path.Combine(_directory, "settings-spotify-fail.json");
        var original = "{\"SettingsVersion\":9,\"SpotifySpDc\":\"enc:existing-sp-dc\"}";
        File.WriteAllText(path, original);
        string? warning = null;
        DataProtection.ProtectBytes = _ => throw new CryptographicException("simulated DPAPI failure");

        using var service = new SettingsService(path, message => warning = message);
        Assert.Throws<CryptographicException>(() => service.Save(new NotchSettings { SpotifySpDc = cookie }));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.NotNull(warning);
        Assert.DoesNotContain(cookie, File.ReadAllText(path));
    }

    [Fact]
    public void DpapiJsonConverter_RoundTripsSecretWithEncPrefix()
    {
        var converter = new DpapiJsonConverter();
        var options = new JsonSerializerOptions();
        const string secret = "  sk-live-secret-token-12345  ";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            converter.Write(writer, secret, options);
        }

        string rawJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        string? writtenValue = JsonSerializer.Deserialize<string>(rawJson);
        Assert.NotNull(writtenValue);
        Assert.StartsWith("enc:", writtenValue);
        Assert.DoesNotContain("sk-live-secret-token", rawJson, StringComparison.Ordinal);

        var reader = new Utf8JsonReader(stream.ToArray());
        Assert.True(reader.Read());
        string decrypted = converter.Read(ref reader, typeof(string), options);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void DpapiJsonConverter_EmptyAndNullValues_SerializeAndReadAsEmptyString()
    {
        var converter = new DpapiJsonConverter();
        var options = new JsonSerializerOptions();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            converter.Write(writer, "", options);
        }

        Assert.Equal("\"\"", System.Text.Encoding.UTF8.GetString(stream.ToArray()));

        var emptyReader = new Utf8JsonReader("\"\""u8);
        Assert.True(emptyReader.Read());
        Assert.Equal("", converter.Read(ref emptyReader, typeof(string), options));

        var nullReader = new Utf8JsonReader("null"u8);
        Assert.True(nullReader.Read());
        Assert.Equal("", converter.Read(ref nullReader, typeof(string), options));
    }

    [Fact]
    public void DpapiJsonConverter_PlaintextAndCorruptPayloads_EnforceMigrationAndCorruptionProtection()
    {
        var converter = new DpapiJsonConverter();
        var options = new JsonSerializerOptions();

        Assert.Throws<CryptographicException>(() =>
        {
            var plaintextReader = new Utf8JsonReader("\"unmigrated-plaintext-secret\""u8);
            Assert.True(plaintextReader.Read());
            return converter.Read(ref plaintextReader, typeof(string), options);
        });

        var (migratedSettings, migrated) = SettingsMigrator.Migrate("{\"SettingsVersion\":14,\"YouTubeApiKey\":\"plaintext-migrated\"}");
        Assert.True(migrated);
        Assert.Equal("plaintext-migrated", migratedSettings.YouTubeApiKey);

        foreach (string corrupt in new[] { "\"enc:not-valid-base64!!!\"", "\"enc:AAAA\"" })
        {
            Assert.Throws<CryptographicException>(() =>
            {
                var corruptReader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(corrupt));
                Assert.True(corruptReader.Read());
                return converter.Read(ref corruptReader, typeof(string), options);
            });
        }
    }

    public void Dispose()
    {
        DataProtection.ProtectBytes = _originalProtectBytes;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
