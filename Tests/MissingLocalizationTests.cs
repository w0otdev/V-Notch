using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MissingLocalizationTests
{
    [Fact]
    public void FormattedMessagesUseSelectedLanguageInsteadOfThreadCulture()
    {
        string previous = Loc.CurrentLanguage;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Loc.SetLanguage("vi");
            Assert.Contains("1,5", Loc.Get("debug.health.lowRam", 1.5, 95));
            Loc.SetLanguage("en");
            Assert.Contains("1.5", Loc.Get("debug.health.lowRam", 1.5, 95));
        }
        finally { Loc.SetLanguage(previous); CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void InspectorRefreshesUnchangedMetricsAndPreservesFiltersAcrossLanguages() => SharedStaTestRunner.Run(() =>
    {
        string previous = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new DebugWindow();
        try
        {
            var snapshot = new PerformanceDebugSnapshot
            {
                HealthLevel = PerformanceHealthLevel.Warning,
                HealthStatusSummary = "UI Lag: 120ms Dispatcher delay",
                HealthStatusKey = "debug.health.ui",
                HealthStatusArguments = [120d],
                ProcessThreadCount = 12,
                ProcessHandleCount = 64,
                GlobalRamAvailBytes = 1536UL * 1024 * 1024,
                ProcessPrivateBytes = 3UL * 1024 * 1024
            };
            window.UpdateSnapshot(snapshot);
            var service = PerformanceDiagnosticService.Instance;
            service.AddServiceLog(PerformanceHealthLevel.Nominal, "MEDIA-LOCALIZATION", "Keep original log text");
            service.AddServiceLog(PerformanceHealthLevel.Nominal, "UNRELATED", "Do not include this event");
            window.ServiceLogsPanel.Visibility = Visibility.Visible;
            window.ServiceCategoryFilterComboBox.SelectedIndex = 1;
            var selected = Assert.IsType<ComboBoxItem>(window.ServiceCategoryFilterComboBox.SelectedItem);
            var logs = (ObservableCollection<DiagnosticLogViewModel>)typeof(DebugWindow)
                .GetField("_serviceLogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

            foreach (var (language, _) in Loc.GetAvailableLanguages())
            {
                Loc.SetLanguage(language);
                Assert.Equal(Loc.Get("debug.title"), window.Title);
                Assert.Equal(Loc.Get("debug.warning"), window.HealthBadgeText.Text);
                Assert.Equal(Loc.Get("debug.health.ui", 120d), window.HealthSummaryText.Text);
                Assert.Equal(Loc.Get("debug.cpuThreads", 12, 64), window.VNotchCpuThreadsText.Text);
                Assert.Equal(Loc.Get("debug.availableRam", 1.5.ToString("0.0", Loc.GetCulture())), window.GlobalRamAvailText.Text);
                Assert.Equal(Loc.Get("debug.mediaFilter"), selected.Content);
                Assert.Equal("Media (Playback & Track)", selected.Tag);
                Assert.Same(selected, window.ServiceCategoryFilterComboBox.SelectedItem);
                Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);

                // Reapply the filter using the actual SelectionChanged handler after translation.
                window.ServiceCategoryFilterComboBox.SelectedIndex = 0;
                window.ServiceCategoryFilterComboBox.SelectedIndex = 1;
                Assert.Contains(logs, log => log.Message == "Keep original log text");
                Assert.All(logs, log => Assert.StartsWith("MEDIA", log.Category));
                Assert.Equal(Loc.Get("debug.serviceCount", logs.Count), window.ServiceLogCountText.Text);
                Assert.All(window.ViewStateComboBox.Items.Cast<ComboBoxItem>(), item =>
                    Assert.DoesNotContain("debug.", Assert.IsType<string>(item.Content)));
                window.UpdateSnapshot(snapshot);
                Assert.Equal(Loc.Get("debug.health.ui", 120d), window.HealthSummaryText.Text);
            }
        }
        finally { window.Close(); Loc.SetLanguage(previous); }
    });

    [Fact]
    public void MediaFallbacksFollowLanguageWithoutChangingRealMetadata()
    {
        string previous = Loc.CurrentLanguage;
        try
        {
            var controller = new MediaDisplayController();
            foreach (string language in new[] { "vi", "de", "ja", "ar", "en" })
            {
                Loc.SetLanguage(language);
                var empty = controller.ProcessMediaUpdate(new MediaInfo()).DisplayText;
                Assert.Equal(Loc.Get("media.noMedia"), empty.Title);
                Assert.Equal(Loc.Get("media.artistName"), empty.Artist);
                var missingArtist = controller.ProcessMediaUpdate(new MediaInfo { CurrentTrack = "My song" }).DisplayText;
                Assert.Equal(Loc.Get("media.unknownArtist"), missingArtist.Artist);
                var real = controller.ProcessMediaUpdate(new MediaInfo
                {
                    CurrentTrack = "My song", CurrentArtist = "Artist name", MediaSource = "Example Player"
                }).DisplayText;
                Assert.Equal("My song", real.Title);
                Assert.Equal("Artist name", real.Artist);
            }
        }
        finally { Loc.SetLanguage(previous); }
    }

    [Fact]
    public void ExistingTooltipsRefreshUsingTheirStoredLocalizationKey() => SharedStaTestRunner.Run(() =>
    {
        string previous = Loc.CurrentLanguage;
        try
        {
            Loc.SetLanguage("en");
            var root = new Grid();
            var button = new Button();
            root.Children.Add(button);
            TooltipHelper.SetLocalizedTooltip(button, "tooltip.showHideKey");
            var tooltip = Assert.IsType<ToolTip>(button.ToolTip);
            Loc.SetLanguage("vi");
            TooltipHelper.RefreshTooltips(root);
            Assert.Same(tooltip, button.ToolTip);
            Assert.Equal(Loc.Get("tooltip.showHideKey"), tooltip.Content);
        }
        finally { Loc.SetLanguage(previous); }
    });
}
