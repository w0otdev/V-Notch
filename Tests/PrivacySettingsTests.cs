using System;
using System.IO;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacySettingsTests
{
    [Fact]
    public void DefaultSettings_HaveExpectedPrivacyDefaults()
    {
        var settings = new NotchSettings();

        Assert.False(settings.EnableLocalOnlyMode);
        Assert.True(settings.AutoCheckUpdates);
        Assert.True(settings.EnableOnlineArtworkLookup);
        Assert.True(settings.EnableOnlineLyrics);
        Assert.True(settings.EnableBrowserUrlInspection);
        Assert.True(settings.EnablePrivacyIndicators);
        Assert.True(settings.EnableDiagnosticLogging);
        Assert.True(settings.EnableSpotlightHistory);
    }

    [Fact]
    public void Clone_PreservesAllPrivacySettings()
    {
        var original = new NotchSettings
        {
            EnableLocalOnlyMode = true,
            AutoCheckUpdates = false,
            EnableOnlineArtworkLookup = false,
            EnableOnlineLyrics = false,
            EnableBrowserUrlInspection = false,
            EnablePrivacyIndicators = false,
            EnableDiagnosticLogging = false,
            EnableSpotlightHistory = false
        };

        var clone = original.Clone();

        Assert.True(clone.EnableLocalOnlyMode);
        Assert.False(clone.AutoCheckUpdates);
        Assert.False(clone.EnableOnlineArtworkLookup);
        Assert.False(clone.EnableOnlineLyrics);
        Assert.False(clone.EnableBrowserUrlInspection);
        Assert.False(clone.EnablePrivacyIndicators);
        Assert.False(clone.EnableDiagnosticLogging);
        Assert.False(clone.EnableSpotlightHistory);
    }

    [Fact]
    public void Migration_V12ToV13_PopulatesPrivacyDefaults()
    {
        const string rawJson = """
            {
              "SettingsVersion": 12,
              "Width": 300
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(13, settings.SettingsVersion);
        Assert.False(settings.EnableLocalOnlyMode);
        Assert.True(settings.AutoCheckUpdates);
        Assert.True(settings.EnableOnlineArtworkLookup);
        Assert.True(settings.EnableOnlineLyrics);
        Assert.True(settings.EnableBrowserUrlInspection);
        Assert.True(settings.EnablePrivacyIndicators);
        Assert.True(settings.EnableDiagnosticLogging);
        Assert.True(settings.EnableSpotlightHistory);
    }

    [Fact]
    public async Task SpotlightUsageStore_ClearHistory_PurgesEntriesAndDeletesFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VNotchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string usagePath = Path.Combine(tempDir, "spotlight-usage.json");

        try
        {
            var store = new SpotlightUsageStore(
                usagePath,
                () => new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

            var item = new SpotlightSearchItem(
                "app:test",
                SpotlightResultKind.Application,
                "Test App",
                "subtitle",
                "test.exe");

            store.RecordLaunch(item);
            await store.WaitForPendingSavesAsync();
            Assert.True(store.GetBoost("app:test") > 0);

            store.ClearHistory();
            await store.WaitForPendingSavesAsync();

            Assert.Equal(0, store.GetBoost("app:test"));
            Assert.False(File.Exists(usagePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SerializationRoundTrip_PreservesAllPrivacySettings()
    {
        var settings = new NotchSettings
        {
            EnableLocalOnlyMode = true,
            AutoCheckUpdates = false,
            EnableOnlineArtworkLookup = false,
            EnableOnlineLyrics = false,
            EnableBrowserUrlInspection = false,
            EnablePrivacyIndicators = false,
            EnableDiagnosticLogging = false,
            EnableSpotlightHistory = false
        };

        string json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<NotchSettings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.EnableLocalOnlyMode);
        Assert.False(deserialized.AutoCheckUpdates);
        Assert.False(deserialized.EnableOnlineArtworkLookup);
        Assert.False(deserialized.EnableOnlineLyrics);
        Assert.False(deserialized.EnableBrowserUrlInspection);
        Assert.False(deserialized.EnablePrivacyIndicators);
        Assert.False(deserialized.EnableDiagnosticLogging);
        Assert.False(deserialized.EnableSpotlightHistory);
    }

    [Fact]
    public void IsInteractiveElement_RecognizesPrivacyAndAllNavTags()
    {
        RunSta(() =>
        {
            var method = typeof(SettingsWindow).GetMethod("IsInteractiveElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            string[] navTags = { "Searching", "Appearance", "Skins", "Behavior", "Devices", "System", "Privacy", "Spotlight", "Advanced", "Performance", "Donating", "Updates" };
            foreach (var tag in navTags)
            {
                var border = new System.Windows.Controls.Border { Tag = tag };
                bool isInteractive = (bool)method!.Invoke(null, new object[] { border })!;
                Assert.True(isInteractive, $"Tag '{tag}' should be recognized as an interactive element.");
            }
        });
    }

    [Fact]
    public void StrictLocalOnly_DimsAndDisablesNetworkOptions()
    {
        RunSta(() =>
        {
            var badge = new System.Windows.Controls.Border { Visibility = System.Windows.Visibility.Collapsed };
            var networkSection = new System.Windows.Controls.StackPanel { Visibility = System.Windows.Visibility.Visible, Opacity = 1.0 };
            var updatesCheck = new System.Windows.Controls.CheckBox { IsEnabled = true };
            var artworkCheck = new System.Windows.Controls.CheckBox { IsEnabled = true };
            var lyricsCheck = new System.Windows.Controls.CheckBox { IsEnabled = true };

            void ApplyLocalOnlyState(bool isLocalOnly)
            {
                badge.Visibility = isLocalOnly ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                networkSection.Visibility = System.Windows.Visibility.Visible;
                networkSection.Opacity = isLocalOnly ? 0.35 : 1.0;
                networkSection.IsEnabled = !isLocalOnly;
                networkSection.IsHitTestVisible = !isLocalOnly;
                updatesCheck.IsEnabled = !isLocalOnly;
                artworkCheck.IsEnabled = !isLocalOnly;
                lyricsCheck.IsEnabled = !isLocalOnly;
            }

            ApplyLocalOnlyState(true);
            Assert.Equal(System.Windows.Visibility.Visible, badge.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, networkSection.Visibility);
            Assert.Equal(0.35, networkSection.Opacity, 2);
            Assert.False(networkSection.IsEnabled);
            Assert.False(networkSection.IsHitTestVisible);
            Assert.False(updatesCheck.IsEnabled);
            Assert.False(artworkCheck.IsEnabled);
            Assert.False(lyricsCheck.IsEnabled);

            ApplyLocalOnlyState(false);
            Assert.Equal(System.Windows.Visibility.Collapsed, badge.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, networkSection.Visibility);
            Assert.Equal(1.0, networkSection.Opacity, 2);
            Assert.True(networkSection.IsEnabled);
            Assert.True(networkSection.IsHitTestVisible);
            Assert.True(updatesCheck.IsEnabled);
            Assert.True(artworkCheck.IsEnabled);
            Assert.True(lyricsCheck.IsEnabled);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
