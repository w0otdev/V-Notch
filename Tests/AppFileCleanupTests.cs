using System.IO;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class AppFileCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-cleanup-test-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;

    public AppFileCleanupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProductionPolicies_DeleteKnownStaleArtifacts_PreserveUserDataAndUnknownFiles()
    {
        string roaming = Path.Combine(_root, "roaming"), local = Path.Combine(_root, "local"), temp = Path.Combine(_root, "temp");
        string model = Path.Combine(local, "VNotch", "translation-models", TranslationModelCatalog.All[0].Id);
        string asset = TranslationModelCatalog.All[0].Assets[0].Name;
        string[] junk =
        [
            Create(Path.Combine(temp, "V-Notch", Guid.NewGuid().ToString("N"), "installer.exe")),
            Create(Path.Combine(local, "V-Notch", "SpotifyWebView2", Guid.NewGuid().ToString("N"), "Default", "Cache", "data")),
            Create(Path.Combine(local, "V-Notch", "Clipboard", "staging", Guid.NewGuid().ToString("N"), "scratch.bin")),
            Create(Path.Combine(local, "V-Notch", "Clipboard", "exports", "public", Guid.NewGuid().ToString("N"), "copy.bin")),
            Create(Path.Combine(roaming, "V-Notch", $"settings.json.{Guid.NewGuid():N}.tmp")),
            Create(Path.Combine(roaming, "V-Notch", $"spotlight-usage.json.{Guid.NewGuid():N}.tmp")),
            Create(Path.Combine(roaming, "V-Notch", "spotlight-usage.preferences.json.tmp")),
            Create(Path.Combine(local, "VNotch", "spotlight-chats.enc.tmp")),
            Create(Path.Combine(model, asset + ".install.part")),
        ];
        string[] preserved =
        [
            Create(Path.Combine(temp, "other-app", "data.tmp")),
            Create(Path.Combine(temp, "V-Notch", "user-folder", "important.txt")),
            Create(Path.Combine(roaming, "V-Notch", "settings.json")),
            Create(Path.Combine(roaming, "V-Notch", "settings.bak-20261009.json")),
            Create(Path.Combine(roaming, "V-Notch", "settings.json.tmp")),
            Create(Path.Combine(roaming, "V-Notch", "settings.json.unknown.tmp")),
            Create(Path.Combine(roaming, "V-Notch", "spotlight-usage.json")),
            Create(Path.Combine(roaming, "V-Notch", "Locales", "custom.json")),
            Create(Path.Combine(local, "V-Notch", "Clipboard", "items", "saved.bin")),
            Create(Path.Combine(local, "V-Notch", "Clipboard", "personal", "saved.enc")),
            Create(Path.Combine(local, "V-Notch", "Clipboard", "staging", Guid.NewGuid().ToString("N") + ".previous", "recovery.bin")),
            Create(Path.Combine(local, "VNotch", "spotlight-chats.enc")),
            Create(Path.Combine(model, asset)),
            Create(Path.Combine(model, asset + ".part")),
            Create(Path.Combine(model, "verified.txt")),
        ];
        AgeTree(_root, _now.AddDays(-10));

        var result = AppFileCleanupService.Clean(AppFileCleanupService.CreatePolicies(roaming, local, temp), _now);

        Assert.Equal(junk.Length, result.FilesDeleted);
        foreach (string file in junk) Assert.False(File.Exists(file), file);
        foreach (string file in preserved) Assert.True(File.Exists(file), file);
    }

    [Fact]
    public void CapacityLimit_EvictsOldestEligibleArtifact_ProtectsRecentFiles()
    {
        string oldest = Create(Path.Combine(_root, "old.tmp"), 60);
        string newer = Create(Path.Combine(_root, "newer.tmp"), 60);
        string recent = Create(Path.Combine(_root, "recent.tmp"), 60);
        Age(oldest, _now.AddDays(-3)); Age(newer, _now.AddDays(-2)); Age(recent, _now.AddMinutes(-10));
        var policy = new AppFileCleanupService.Policy(_root, false, _ => true, TimeSpan.FromDays(7), 130, TimeSpan.FromDays(1));

        var result = AppFileCleanupService.Clean([policy], _now);

        Assert.Equal(60, result.BytesDeleted);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void RecentChild_ProtectsEntireOldDirectory()
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string old = Create(Path.Combine(dir, "old.bin")), recent = Create(Path.Combine(dir, "recent.bin"));
        AgeTree(dir, _now.AddDays(-10));
        Age(recent, _now.AddMinutes(-10));

        Assert.Equal(0, AppFileCleanupService.Clean([DirectoryPolicy()], _now).FilesDeleted);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void NewlyCreatedFile_WithOldWriteTime_IsPreserved()
    {
        string path = Create(Path.Combine(_root, "copy.tmp"));
        File.SetLastWriteTimeUtc(path, _now.AddDays(-10));
        Assert.Equal(0, AppFileCleanupService.Clean([FilePolicy()], _now).FilesDeleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ActiveLease_ProtectsOldArtifacts_UntilLastLeaseIsReleased()
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string path = Create(Path.Combine(dir, "model.install.part"));
        AgeTree(dir, _now.AddDays(-10));
        using var first = AppFileCleanupService.Protect(dir);
        using var second = AppFileCleanupService.Protect(path);
        first.Dispose(); first.Dispose();
        Assert.Equal(0, AppFileCleanupService.Clean([DirectoryPolicy()], _now).FilesDeleted);
        second.Dispose();
        Assert.Equal(1, AppFileCleanupService.Clean([DirectoryPolicy()], _now).FilesDeleted);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void LockedFile_IsSkipped_OtherStaleFilesAreReclaimed()
    {
        string locked = Create(Path.Combine(_root, "locked.tmp")), free = Create(Path.Combine(_root, "free.tmp"));
        AgeTree(_root, _now.AddDays(-10));
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(1, AppFileCleanupService.Clean([FilePolicy()], _now).FilesDeleted);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void CancellationAndScanBudget_PreserveUninspectedArtifacts()
    {
        string path = Create(Path.Combine(_root, "old.tmp"));
        AgeTree(_root, _now.AddDays(-10));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Equal(0, AppFileCleanupService.Clean([FilePolicy()], _now, stop.Token).FilesDeleted);
        Assert.Equal(0, AppFileCleanupService.Clean([FilePolicy()], _now, entryLimit: 1).FilesDeleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LargeTree_ExceedingBudget_IsNotPartiallyDeleted()
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        for (int i = 0; i < 30; i++) Create(Path.Combine(dir, $"{i}.bin"));
        AgeTree(_root, _now.AddDays(-10));
        Assert.Equal(0, AppFileCleanupService.Clean([DirectoryPolicy()], _now, entryLimit: 10).FilesDeleted);
        Assert.Equal(30, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public void SafetyCheck_RejectsTraversalSiblingAndDriveRoot()
    {
        Assert.False(AppFileCleanupService.IsSafePath(_root, Path.Combine(_root, "..")));
        Assert.False(AppFileCleanupService.IsSafePath(_root, _root + "-other"));
        Assert.False(AppFileCleanupService.IsSafePath(Path.GetPathRoot(_root)!, _root));
        Assert.True(AppFileCleanupService.IsSafePath(_root, _root));
    }

    [Fact]
    public async Task Junctions_InCandidatesAndAncestors_AreNeverFollowed()
    {
        string owned = Path.Combine(_root, "owned");
        string external = Path.Combine(_root, "external");
        string externalFile = Create(Path.Combine(external, "saved.bin"));
        Directory.CreateDirectory(owned);
        AgeTree(_root, _now.AddDays(-10));
        string link = Path.Combine(owned, Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", link, external }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.False(AppFileCleanupService.IsSafePath(owned, Path.Combine(link, "saved.bin")));
            var policies = new[]
            {
                new AppFileCleanupService.Policy(owned, true, _ => true, TimeSpan.Zero),
                new AppFileCleanupService.Policy(link, false, _ => true, TimeSpan.Zero),
            };
            Assert.Equal(0, AppFileCleanupService.Clean(policies, _now).FilesDeleted);
            Assert.True(File.Exists(externalFile));
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    [Fact]
    public async Task Worker_StartIsIdempotent_AndShutdownCancelsStartupDelay()
    {
        var service = new AppFileCleanupService();
        service.Start(); service.Start();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(service.Start);
    }

    private AppFileCleanupService.Policy FilePolicy() => new(_root, false, _ => true, TimeSpan.FromDays(1));
    private AppFileCleanupService.Policy DirectoryPolicy() => new(_root, true,
        name => Guid.TryParseExact(name, "N", out _), TimeSpan.FromDays(1));
    private static string Create(string path, int size = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
    private static void Age(string path, DateTime utc)
    {
        File.SetCreationTimeUtc(path, utc);
        File.SetLastWriteTimeUtc(path, utc);
    }
    private static void AgeTree(string root, DateTime utc)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) Age(file, utc);
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        { Directory.SetCreationTimeUtc(dir, utc); Directory.SetLastWriteTimeUtc(dir, utc); }
        Directory.SetCreationTimeUtc(root, utc); Directory.SetLastWriteTimeUtc(root, utc);
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
