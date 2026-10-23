using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AppPathsMigrationTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "vnotch-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingLegacyDataDoesNotCreateDirectories()
    {
        AppPaths.MigrateLegacyLocalData(_parent);
        Assert.False(Directory.Exists(_parent));
    }

    [Fact]
    public void MigrationMovesDataAndKeepsBothVersionsOfConflictingFiles()
    {
        string legacy = Path.Combine(_parent, "VNotch"), canonical = Path.Combine(_parent, "V-Notch");
        Write(Path.Combine(legacy, "spotlight-chats.enc"), "legacy encrypted chats");
        Write(Path.Combine(canonical, "spotlight-chats.enc"), "current encrypted chats");
        Write(Path.Combine(legacy, "translation-models", "custom-example", "model.gguf"), "model bytes");
        Write(Path.Combine(legacy, "currency-rates", "USD-VND.json"), "rates");
        Write(Path.Combine(legacy, "seen-tray-settings.txt"), "seen");

        AppPaths.MigrateLegacyLocalData(_parent);
        AppPaths.MigrateLegacyLocalData(_parent);

        Assert.False(Directory.Exists(legacy));
        Assert.Equal("current encrypted chats", File.ReadAllText(Path.Combine(canonical, "spotlight-chats.enc")));
        Assert.Equal("legacy encrypted chats", File.ReadAllText(Path.Combine(canonical, "legacy-migration-conflicts", "spotlight-chats.enc")));
        Assert.Equal("model bytes", File.ReadAllText(Path.Combine(canonical, "translation-models", "custom-example", "model.gguf")));
        Assert.Equal("rates", File.ReadAllText(Path.Combine(canonical, "currency-rates", "USD-VND.json")));
        Assert.Equal("seen", File.ReadAllText(Path.Combine(canonical, "seen-tray-settings.txt")));
    }

    [Fact]
    public void LockedLegacyFileStaysIntactAndIsMigratedOnNextStartup()
    {
        string source = Path.Combine(_parent, "VNotch", "spotlight-chats.enc");
        Write(source, "preserve me");
        using (var handle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AppPaths.MigrateLegacyLocalData(_parent);
            Assert.True(File.Exists(source));
        }
        AppPaths.MigrateLegacyLocalData(_parent);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(_parent, "V-Notch", "spotlight-chats.enc")));
        Assert.False(Directory.Exists(Path.Combine(_parent, "VNotch")));
    }

    [Fact]
    public void DirectoryFileConflictsPreserveLegacyTreeInRecoveryFolder()
    {
        Write(Path.Combine(_parent, "V-Notch", "translation-models"), "canonical file");
        Write(Path.Combine(_parent, "VNotch", "translation-models", "profile", "model.gguf"), "legacy model");
        AppPaths.MigrateLegacyLocalData(_parent);
        Assert.Equal("canonical file", File.ReadAllText(Path.Combine(_parent, "V-Notch", "translation-models")));
        Assert.Equal("legacy model", File.ReadAllText(Path.Combine(_parent, "V-Notch", "legacy-migration-conflicts", "translation-models", "profile", "model.gguf")));
    }

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_parent), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_parent)) Directory.Delete(_parent, true);
    }
}
