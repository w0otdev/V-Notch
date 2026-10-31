using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using VNotch;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Tests;

public sealed class SettingsLivePreviewAndTabOrderTests
{
    [Fact]
    public void ResolveFullNavTabOrder_AppendsMissingCameraToLegacyFourTabString()
    {
        const string legacyOrder = "Media,Secondary,Timer,AudioMixer";

        List<string> resolved = SettingsWindow.ResolveFullNavTabOrder(legacyOrder);

        Assert.Equal(new[] { "Media", "Secondary", "Timer", "AudioMixer", "Camera" }, resolved);
    }

    [Fact]
    public void ResolveFullNavTabOrder_AllowsReorderingCameraFromLegacyFourTabString()
    {
        const string legacyOrder = "Media,Secondary,Timer,AudioMixer";

        List<string> orderTokens = SettingsWindow.ResolveFullNavTabOrder(legacyOrder);
        int initialSlot = 4; // Camera
        int targetSlot = 1;  // Move Camera right after Media

        Assert.True(initialSlot < orderTokens.Count && targetSlot < orderTokens.Count);

        string movedToken = orderTokens[initialSlot];
        orderTokens.RemoveAt(initialSlot);
        orderTokens.Insert(targetSlot, movedToken);
        string updatedOrder = string.Join(",", orderTokens);

        Assert.Equal("Media,Camera,Secondary,Timer,AudioMixer", updatedOrder);
    }

    [Fact]
    public void ResolveFullNavTabOrder_DeduplicatesCaseInsensitivelyAndHandlesNull()
    {
        List<string> fromNull = SettingsWindow.ResolveFullNavTabOrder(null);
        Assert.Equal(5, fromNull.Count);
        Assert.Contains("Camera", fromNull, StringComparer.OrdinalIgnoreCase);

        List<string> deduplicated = SettingsWindow.ResolveFullNavTabOrder("Media,camera,Timer,CAMERA,Secondary");
        Assert.Equal(new[] { "Media", "camera", "Timer", "Secondary", "AudioMixer" }, deduplicated);
    }

    [Fact]
    public void AddLanguageSearchTerms_IncludesAllSixteenLanguagesAndNativeNames()
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        SettingsWindow.AddLanguageSearchTerms(terms);

        var available = Loc.GetAvailableLanguages();
        Assert.True(available.Count >= 16);

        foreach (var (code, nativeName) in available)
        {
            Assert.Contains(code, terms);
            Assert.Contains(nativeName, terms);

            var culture = CultureInfo.GetCultureInfo(code);
            Assert.Contains(culture.EnglishName, terms);
            Assert.Contains(culture.NativeName, terms);
        }

        Assert.Contains(terms, t => t.Contains("한국어", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(terms, t => t.Contains("Português", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsHandlers_DoNotPersistOrCommitOnLivePreviewEdits()
    {
        string repoRoot = FindRepoRoot();
        string controlsSource = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.Controls.cs"));
        string tabOrderingSource = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.TabOrdering.cs"));
        string subtitlesSource = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.Subtitles.cs"));

        // Verify LanguageCombo_SelectionChanged routes through ApplyPreview(ReadSettingsFromUi()) without Commit/ApplyAsync
        int langStart = controlsSource.IndexOf("private void LanguageCombo_SelectionChanged", StringComparison.Ordinal);
        int langEnd = controlsSource.IndexOf("private void PopulateWidgetCombo", langStart, StringComparison.Ordinal);
        string langBody = controlsSource[langStart..langEnd];
        Assert.Contains("ApplyPreview(ReadSettingsFromUi())", langBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_editSession.Commit", langBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_settingsAppService.ApplyAsync", langBody, StringComparison.Ordinal);

        // Verify ResetTabOrderButton_Click uses persist: false
        int resetStart = controlsSource.IndexOf("private void ResetTabOrderButton_Click", StringComparison.Ordinal);
        int resetEnd = controlsSource.IndexOf("private void EnableWeatherCheck_Changed", resetStart, StringComparison.Ordinal);
        string resetBody = controlsSource[resetStart..resetEnd];
        Assert.Contains("ApplySettingsFromUi(persist: false)", resetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySettingsFromUi(persist: true)", resetBody, StringComparison.Ordinal);

        // Verify NavTabs settings and CommitSettingsNavTabOrder do not call ApplyAsync or Commit
        Assert.DoesNotContain("_settingsAppService.ApplyAsync", tabOrderingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_editSession.Commit", tabOrderingSource, StringComparison.Ordinal);
        Assert.Contains("ApplySettingsFromUi(persist: false)", tabOrderingSource, StringComparison.Ordinal);
        Assert.Contains("ApplyPreview(ReadSettingsFromUi())", tabOrderingSource, StringComparison.Ordinal);

        // Verify SubtitlePriority_Drop uses persist: false
        Assert.Contains("ApplySettingsFromUi(persist: false)", subtitlesSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySettingsFromUi(persist: true)", subtitlesSource, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "V-Notch.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
