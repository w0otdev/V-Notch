using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class DataDeletionSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vnotch-data-safety-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallDeletesOnlyUnchangedManifestFiles(bool changed)
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string executable = Write(install, "V-Notch.exe", "original app");
        string asset = Write(install, "Assets\\original.txt", "original asset");
        WriteManifest(install, "V-Notch.exe", "Assets\\original.txt");
        string personal = Write(install, "Assets\\personal.txt", "user data");
        if (changed) File.WriteAllText(asset, "user customization");
        Assert.Equal(0, await RunHelper(InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue)));
        Assert.False(File.Exists(executable));
        Assert.Equal(changed, File.Exists(asset));
        if (changed) Assert.Equal("user customization", File.ReadAllText(asset));
        Assert.Equal("user data", File.ReadAllText(personal));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("Assets/../../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("V-Notch.exe:stream")]
    [InlineData("installed-files.json")]
    public async Task TamperedManifestFailsBeforeDeletingAnyFile(string badPath)
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string executable = Write(install, "V-Notch.exe", "app");
        string outside = Write(_root, "outside.txt", "user data");
        WriteManifest(install, "V-Notch.exe");
        var start = InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue);
        var entries = InstallDirectoryCleanup.ReadManifest(install).Append(new(badPath, new string('0', 64))).ToArray();
        File.WriteAllText(Path.Combine(install, InstallDirectoryCleanup.ManifestFileName), JsonSerializer.Serialize(new InstallDirectoryCleanup.InstallManifest(2, entries)));
        Assert.ThrowsAny<Exception>(() => InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue));
        Assert.NotEqual(0, await RunHelper(start));
        Assert.Equal("app", File.ReadAllText(executable));
        Assert.Equal("user data", File.ReadAllText(outside));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[\"V-Notch.exe\"]")]
    public async Task MissingOrLegacyManifestPreservesAllFiles(string? manifest)
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string executable = Write(install, "V-Notch.exe", "app");
        string personal = Write(install, "personal.txt", "user data");
        if (manifest != null) Write(install, InstallDirectoryCleanup.ManifestFileName, manifest);
        Assert.Equal(0, await RunHelper(InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue)));
        Assert.Equal("app", File.ReadAllText(executable));
        Assert.Equal("user data", File.ReadAllText(personal));
    }

    [Fact]
    public async Task HelperRejectsJunctionInsertedAfterPlanning()
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string executable = Write(install, "V-Notch.exe", "app");
        Write(install, "Assets\\personal.txt", "user data");
        WriteManifest(install, "V-Notch.exe", "Assets\\personal.txt");
        var start = InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue);
        string assets = Path.Combine(install, "Assets");
        string outside = Path.Combine(_root, "outside");
        Directory.Move(assets, outside);
        CreateJunction(assets, outside);
        try
        {
            Assert.NotEqual(0, await RunHelper(start));
            Assert.Equal("app", File.ReadAllText(executable));
            Assert.Equal("user data", File.ReadAllText(Path.Combine(outside, "personal.txt")));
        }
        finally { Directory.Delete(assets); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheCleanupNeverFollowsJunctions(bool rootLink)
    {
        string app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string personal = Write(outside, "personal.txt", "user data");
        string cache = Path.Combine(app, "cache");
        if (!rootLink) Directory.CreateDirectory(cache);
        string link = rootLink ? cache : Path.Combine(cache, "linked");
        CreateJunction(link, outside);
        string? disposable = rootLink ? null : Write(cache, "cached.txt", "cache");
        try
        {
            SettingsWindow.DeleteCacheDirectories(app);
            Assert.Equal("user data", File.ReadAllText(personal));
            if (disposable != null) Assert.False(File.Exists(disposable));
            Assert.Throws<IOException>(() => SafeFileDeletion.DeleteDirectory(cache));
            Assert.Equal("user data", File.ReadAllText(personal));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ShortcutCleanupRetainsExtraUserFilesAndSubdirectories()
    {
        string menu = Directory.CreateDirectory(Path.Combine(_root, "menu")).FullName;
        Write(menu, "V-Notch.lnk", "shortcut");
        string personal = Write(menu, "notes\\personal.txt", "user data");
        SafeFileDeletion.DeleteShortcutDirectory(menu);
        Assert.False(File.Exists(Path.Combine(menu, "V-Notch.lnk")));
        Assert.Equal("user data", File.ReadAllText(personal));
    }

    [Fact]
    public async Task InstallerRejectsPayloadJunctionBeforeChangingTheInstallation()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Write(source, "V-Notch.exe", "new app");
        string old = Write(install, "V-Notch.exe", "old app");
        string personal = Write(outside, "personal.txt", "user data");
        string link = Path.Combine(source, "Assets");
        CreateJunction(link, outside);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SetupOperations.InstallAsync(new(source, install, false), _ => { }, Callbacks));
            Assert.Equal("old app", File.ReadAllText(old));
            Assert.Equal("user data", File.ReadAllText(personal));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ClipboardRetentionLeavesUnknownDirectoriesAndLinkedFilesAlone()
    {
        string root = Path.Combine(_root, "clipboard");
        await using var store = new ClipboardHistoryStore(root);
        await store.InitializeAsync();
        string unknown = Write(root, "exports\\public\\notes\\personal.txt", "unknown user data");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(unknown)!, DateTime.UtcNow.AddDays(-10));
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string personal = Write(outside, "personal.txt", "user data");
        string link = Path.Combine(root, "exports", "public", Guid.NewGuid().ToString("N"));
        CreateJunction(link, outside);
        try
        {
            await store.PruneAsync(DateTime.UtcNow);
            Assert.Equal("unknown user data", File.ReadAllText(unknown));
            Assert.Equal("user data", File.ReadAllText(personal));
        }
        finally { Directory.Delete(link); }
    }

    private static readonly SetupInstallCallbacks Callbacks = new(() => Task.CompletedTask, (_, _) => { }, (_, _) => { }, (_, _) => { }, _ => { });

    [Fact]
    public async Task UnreadableSettingsCannotBeReplacedByAnAutomaticSave()
    {
        string path = Write(_root, "settings.json", JsonSerializer.Serialize(new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion, Width = 555 }));
        string original = File.ReadAllText(path);
        await using var service = new SettingsService(path, _ => { });
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            service.Load();
        await Assert.ThrowsAsync<CryptographicException>(() => service.SaveAsync(new NotchSettings { Width = 400 }));
        Assert.Equal(original, File.ReadAllText(path));
        var recovered = service.Load();
        Assert.Equal(555, recovered.Width);
        await service.SaveAsync(recovered);
    }

    [Fact]
    public void SettingsMigrationRetainsUnknownFieldsAndNeverTouchesUserExports()
    {
        string original = JsonSerializer.Serialize(new { SettingsVersion = SettingsMigrator.CurrentVersion, YouTubeApiKey = "fixture-key", CustomUserData = "must survive" });
        string path = Write(_root, "settings.json", original);
        string legacy = Write(_root, "settings.bak-legacy.json", original);
        string export = Write(_root, "settings-my-export.json", original);
        using var service = new SettingsService(path, _ => { });
        service.Load();
        Assert.Equal(original, File.ReadAllText(export));
        using var backup = JsonDocument.Parse(File.ReadAllText(legacy));
        Assert.Equal("must survive", backup.RootElement.GetProperty("CustomUserData").GetString());
        Assert.Equal("fixture-key", DataProtection.Unprotect(backup.RootElement.GetProperty("YouTubeApiKey").GetString()));
        Assert.Contains(Directory.GetFiles(_root, "settings.bak-*.json"), file =>
            JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("CustomUserData").GetString() == "must survive");
    }

    [Fact]
    public async Task BackupRetentionNeverDeletesUnknownBackupNames()
    {
        for (int i = 0; i < 15; i++) Write(_root, $"settings.bak-personal-{i}.json", "personal data");
        await using var service = new SettingsService(Path.Combine(_root, "settings.json"), _ => { });
        for (int i = 0; i < 14; i++) await service.SaveAsync(new NotchSettings { Width = 400 + i });
        for (int i = 0; i < 15; i++) Assert.Equal("personal data", File.ReadAllText(Path.Combine(_root, $"settings.bak-personal-{i}.json")));
        Assert.Equal(25, Directory.GetFiles(_root, "settings.bak-*.json").Length);
    }

    [Fact]
    public void FailedQuarantinePreservesTheOriginalAndBlocksSavingDefaults()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        string path = Write(folder, "settings.json", "{broken original");
        using var service = new SettingsService(path, _ => { });
        string outside = Path.Combine(_root, "outside");
        Directory.Move(folder, outside);
        CreateJunction(folder, outside);
        try
        {
            service.Load();
            Assert.Throws<CryptographicException>(() => service.Save(new NotchSettings()));
            Assert.Equal("{broken original", File.ReadAllText(Path.Combine(outside, "settings.json")));
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public void CorruptSettingsRecoveryNeverOverwritesAnEarlierQuarantine()
    {
        string path = Write(_root, "settings.json", "{first corrupt data");
        using var service = new SettingsService(path, _ => { });
        service.Load();
        File.WriteAllText(path, "{second corrupt data");
        service.Load();
        string[] originals = Directory.GetFiles(_root, "settings.corrupt-*.json").Select(File.ReadAllText).ToArray();
        Assert.Equal(2, originals.Length);
        Assert.Contains("{first corrupt data", originals);
        Assert.Contains("{second corrupt data", originals);
    }

    [Fact]
    public async Task CorruptSettingsCanRecoverAfterAnEarlierReadFailure()
    {
        string path = Write(_root, "settings.json", "{original corrupt data");
        await using var service = new SettingsService(path, _ => { });
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) service.Load();
        var recovered = service.Load();
        await service.SaveAsync(recovered);
        Assert.Contains(Directory.GetFiles(_root, "settings.corrupt-*.json"), file => File.ReadAllText(file) == "{original corrupt data");
    }

    [Fact]
    public async Task CleanupCannotDeleteAFileHeldByAWriter()
    {
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        string executable = Write(install, "V-Notch.exe", "app");
        WriteManifest(install, "V-Notch.exe");
        var start = InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue);
        using (var writer = new FileStream(executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Assert.NotEqual(0, await RunHelper(start));
        Assert.Equal("app", File.ReadAllText(executable));
        Assert.True(File.Exists(Path.Combine(install, InstallDirectoryCleanup.ManifestFileName)));
    }

    [Fact]
    public async Task SuccessfulUpgradeRetainsRecoveryCopiesOfUnverifiedFiles()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        Write(source, "V-Notch.exe", "new app");
        Write(source, "asset.txt", "new asset");
        Write(install, "V-Notch.exe", "old app");
        Write(install, "asset.txt", "user customization");
        string recoveryRoot = Path.Combine(_root, "recovery");
        await SetupOperations.InstallAsync(new(source, install, false), _ => { }, Callbacks, recoveryRoot);
        string[] maps = Directory.GetFiles(recoveryRoot, "restore-map.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(map => File.ReadAllText(map).Contains(JsonSerializer.Serialize(Path.Combine(install, "asset.txt")), StringComparison.Ordinal)).ToArray();
        Assert.Single(maps);
        string backupDirectory = Path.GetDirectoryName(maps[0])!;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(maps[0]))!;
            Assert.Equal("user customization", File.ReadAllText(map[Path.Combine(install, "asset.txt")]));
            Assert.EndsWith(".recovery", backupDirectory);
            Assert.Equal("new asset", File.ReadAllText(Path.Combine(install, "asset.txt")));
        }
        finally
        {
            Assert.StartsWith(recoveryRoot + Path.DirectorySeparatorChar, backupDirectory, StringComparison.OrdinalIgnoreCase);
            SafeFileDeletion.DeleteDirectory(backupDirectory);
        }
    }

    [Theory]
    [InlineData("C:\\data\\V-Notch", "C:\\data\\V-Notch\\app", true)]
    [InlineData("C:\\data\\V-Notch\\app", "C:\\data\\V-Notch", true)]
    [InlineData("C:\\data\\V-Notch", "C:\\data\\V-Notch", true)]
    [InlineData("C:\\data\\V-Notch", "C:\\data\\V-Notch-other", false)]
    public void AppDataPurgeMustNotOverlapTheInstallation(string first, string second, bool overlaps) =>
        Assert.Equal(overlaps, SafeFileDeletion.PathsOverlap(first, second));

    [Fact]
    public void LinkedChildPreventsAnyPartialDeletionOfItsParent()
    {
        string owned = Directory.CreateDirectory(Path.Combine(_root, "owned")).FullName;
        string original = Write(owned, "first.txt", "original data");
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string personal = Write(outside, "personal.txt", "user data");
        string link = Path.Combine(owned, "link");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<IOException>(() => SafeFileDeletion.DeleteDirectory(owned));
            Assert.Equal("original data", File.ReadAllText(original));
            Assert.Equal("user data", File.ReadAllText(personal));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task UpgradeRetainsVerifiedOwnershipWithoutClaimingOldUserFiles()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string install = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        Write(source, "V-Notch.exe", "new app");
        Write(install, "V-Notch.exe", "old app");
        Write(install, "obsolete.dll", "old owned library");
        Write(install, "custom.txt", "original asset");
        WriteManifest(install, "V-Notch.exe", "obsolete.dll", "custom.txt");
        File.WriteAllText(Path.Combine(install, "custom.txt"), "user customization");
        await SetupOperations.InstallAsync(new(source, install, false), _ => { }, Callbacks, Path.Combine(_root, "recovery"));
        Assert.Equal(0, await RunHelper(InstallDirectoryCleanup.CreateStartInfo(install, int.MaxValue)));
        Assert.False(File.Exists(Path.Combine(install, "obsolete.dll")));
        Assert.Equal("user customization", File.ReadAllText(Path.Combine(install, "custom.txt")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InstallerRejectsOverlappingPayloadAndDestinationBeforeWriting(int direction)
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        string install = source;
        if (direction == 1) install = Directory.CreateDirectory(Path.Combine(source, "child")).FullName;
        if (direction == 2) source = Directory.CreateDirectory(Path.Combine(install, "child")).FullName;
        Write(source, "V-Notch.exe", "original source");
        if (install != source) Write(install, "V-Notch.exe", "original destination");
        await Assert.ThrowsAsync<ArgumentException>(() => SetupOperations.InstallAsync(new(source, install, false), _ => { }, Callbacks));
        Assert.Equal("original source", File.ReadAllText(Path.Combine(source, "V-Notch.exe")));
    }

    private static string Write(string root, string relative, string contents)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteManifest(string root, params string[] paths) => File.WriteAllText(
        Path.Combine(root, InstallDirectoryCleanup.ManifestFileName), JsonSerializer.Serialize(new InstallDirectoryCleanup.InstallManifest(2,
            paths.Select(path => new InstallDirectoryCleanup.InstalledFile(path, InstallDirectoryCleanup.HashFile(Path.Combine(root, path)))).ToArray())));

    private static async Task<int> RunHelper(ProcessStartInfo start)
    {
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync(), output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        await Task.WhenAll(errors, output);
        return process.ExitCode;
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        SafeFileDeletion.DeleteDirectory(_root);
    }
}
