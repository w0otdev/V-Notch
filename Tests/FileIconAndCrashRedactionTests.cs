using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class FileIconAndCrashRedactionTests
{
    [Fact]
    public void TryGetMscIcon_UncIconFileAttribute_ReturnsNullImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-msc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string mscPath = Path.Combine(tempDir, "untrusted.msc");
            File.WriteAllText(
                mscPath,
                "<MSC_File><VisualAttributes><Icon Index=\"0\" File=\"\\\\192.0.2.1\\share\\a.dll\" /></VisualAttributes></MSC_File>");

            var sw = Stopwatch.StartNew();
            var icon = FileIconProvider.TryGetMscIcon(mscPath);
            sw.Stop();

            Assert.Null(icon);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected immediate rejection without SMB stall, but took {sw.Elapsed}.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryGetMscIcon_ExternalDtdUncReference_ReturnsNullImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-msc-dtd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string mscPath = Path.Combine(tempDir, "untrusted-dtd.msc");
            File.WriteAllText(
                mscPath,
                "<!DOCTYPE MSC_File SYSTEM \"\\\\192.0.2.1\\share\\evil.dtd\"><MSC_File><VisualAttributes><Icon Index=\"0\" File=\"C:\\Windows\\System32\\mmc.exe\" /></VisualAttributes></MSC_File>");

            var icon = FileIconProvider.TryGetMscIcon(mscPath);
            Assert.Null(icon);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void QuarantineCorruptFile_ScrubsSensitiveApiKeysBeforeWritingToDisk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-corrupt-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string settingsPath = Path.Combine(tempDir, "settings.json");
            const string corruptJson = "{ \"DeepSeekApiKey\": \"sk-1234567890abcdef1234567890\", \"SpotlightDeepSeekApiKey\": \"sk-1234567890abcdef1234567890\", corrupt_tail";
            File.WriteAllText(settingsPath, corruptJson);

            using var service = new SettingsService(settingsPath, _ => { });
            _ = service.Load();

            string[] quarantinedFiles = Directory.GetFiles(tempDir, "settings.corrupt-*.json");
            Assert.NotEmpty(quarantinedFiles);

            foreach (string quarantinedFile in quarantinedFiles)
            {
                string content = File.ReadAllText(quarantinedFile);
                Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
                Assert.DoesNotContain("sk-1234567890abcdef1234567890", content, StringComparison.Ordinal);
            }

            string directPath = service.QuarantineCorruptFile(corruptJson, new JsonException("Simulated parse failure"));
            Assert.True(File.Exists(directPath));
            string directContent = File.ReadAllText(directPath);
            Assert.Contains("[REDACTED]", directContent, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-1234567890abcdef1234567890", directContent, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FormatCrashReport_RedactsMachineNameAndUser()
    {
        string report = CrashReporter.FormatCrashReport(
            "UnitTest",
            new InvalidOperationException("Simulated crash"),
            "context",
            isTerminating: false);

        Assert.Contains("User: [REDACTED] (Machine: [REDACTED])", report, StringComparison.Ordinal);
        Assert.Contains("Machine: [REDACTED]", report, StringComparison.Ordinal);
        Assert.Contains("Machine / User: [REDACTED] / [REDACTED]", report, StringComparison.Ordinal);
        Assert.DoesNotContain($"Machine: {Environment.MachineName}", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Machine / User: {Environment.MachineName}", report, StringComparison.OrdinalIgnoreCase);
    }
}
