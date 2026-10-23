using System.IO;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class WindowTitleScannerWatcherTests
{
    [Fact]
    public async Task SettingsLoadAndSavePublishPermissionWithoutAWatcher()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vnotch-watcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            WindowTitleScanner.InvalidateInspectionAllowed();
            Assert.False(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
            Assert.False(Directory.Exists(directory));
            using var scanner = new WindowTitleScanner();
            Assert.Null(scanner.TryGetBrowserUrl());
            Assert.False(Directory.Exists(directory));
            await using var settings = new SettingsService(Path.Combine(directory, "settings.json"));
            await settings.SaveAsync(new NotchSettings { EnableBrowserUrlInspection = true });
            Assert.True(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
            await settings.SaveAsync(new NotchSettings { EnableBrowserUrlInspection = false });
            Assert.False(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
            Assert.False(settings.Load().EnableBrowserUrlInspection);
        }
        finally
        {
            WindowTitleScanner.InvalidateInspectionAllowed();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CorruptSettingsRevokePreviouslyGrantedPermission()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vnotch-settings-privacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{invalid");
            WindowTitleScanner.UpdateInspectionAllowed(true);
            using var settings = new SettingsService(path);
            Assert.False(settings.Load().EnableBrowserUrlInspection);
            Assert.False(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
        }
        finally
        {
            WindowTitleScanner.InvalidateInspectionAllowed();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
