using System.IO;
using System.Xml.Linq;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsSearchMatcherTests
{
    [Theory]
    [InlineData("Ẩn notch khi không hoạt động", "an notch khi khong hoat dong")]
    [InlineData("GPU refraction (experimental)", "gpu-refraction")]
    [InlineData("Show Spotify Canvas background", "spotify canvas")]
    [InlineData("निष्क्रिय होने पर अपने आप छिपाएँ", "निष्क्रिय छिपाएँ")]
    [InlineData("スマートサムネイルクロップ", "サムネイル")]
    public void IsMatchSupportsSupportedScriptsAndPunctuation(string source, string query)
    {
        Assert.True(SettingsSearchMatcher.IsMatch(source, query));
    }

    [Fact]
    public void NormalizeMakesVietnameseDStrokeAccentInsensitive()
    {
        Assert.Equal(
            "tu dong an khi khong hoat dong",
            SettingsSearchMatcher.Normalize("Tự động ẩn khi không hoạt động"));
    }

    [Fact]
    public void IsMatchAllowsNonAdjacentQueryWordsInAnyOrder()
    {
        const string source = "Show the blurred album-art glow behind the expanded media view";

        Assert.True(SettingsSearchMatcher.IsMatch(source, "media album"));
        Assert.True(SettingsSearchMatcher.IsMatch(source, "album blur"));
    }

    [Fact]
    public void IsMatchRequiresEveryQueryWord()
    {
        Assert.False(SettingsSearchMatcher.IsMatch(
            "Show the blurred album-art glow",
            "album battery"));
    }

    [Fact]
    public void IsMatchKeepsFuzzyMatchingForLongKeywords()
    {
        Assert.True(SettingsSearchMatcher.IsMatch(
            "Adjust the Canvas brightness",
            "canvas brightnes"));
    }

    [Fact]
    public void TranslationLookupIncludesNewlySupportedHindi()
    {
        var translations = Loc.GetAllTranslations("Width");

        Assert.Contains("Chiều rộng", translations);
        Assert.Contains("幅", translations);
        Assert.Contains("चौड़ाई", translations);
    }

    [Fact]
    public void TranslationLookupWorksFromAnySupportedLanguage()
    {
        var translations = Loc.GetAllTranslations("चौड़ाई");

        Assert.Contains("Width", translations);
        Assert.Contains("Chiều rộng", translations);
    }

    [Fact]
    public void EverySettingsSectionContributesAtLeastOneSearchResult()
    {
        string repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Windows", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] panelNames =
        {
            "PanelAppearance", "PanelBehavior", "PanelSkins", "PanelDevices", "PanelSystem", "PanelSpotlight",
            "PanelAdvanced", "PanelPerformance", "PanelDonating", "PanelUpdates"
        };

        foreach (string panelName in panelNames)
        {
            XElement? panel = document.Descendants(presentation + "StackPanel")
                .SingleOrDefault(element => (string?)element.Attribute(xaml + "Name") == panelName);
            Assert.NotNull(panel);
            Assert.Contains(panel!.Descendants(presentation + "Border"), border =>
                (string?)border.Attribute("Style") == "{StaticResource SettingRowBorder}"
                && border.Parent?.Name == presentation + "StackPanel");
        }
    }

    [Fact]
    public void SpotlightToggleLivesInItsOwnSettingsSection()
    {
        string repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Windows", "SettingsWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement? spotlightNav = document.Descendants(presentation + "Border")
            .SingleOrDefault(element => (string?)element.Attribute(xaml + "Name") == "NavSpotlight");
        Assert.NotNull(spotlightNav);
        Assert.Equal("Spotlight", (string?)spotlightNav!.Attribute("Tag"));

        XElement? spotlightPanel = document.Descendants(presentation + "StackPanel")
            .SingleOrDefault(element => (string?)element.Attribute(xaml + "Name") == "PanelSpotlight");
        XElement? systemPanel = document.Descendants(presentation + "StackPanel")
            .SingleOrDefault(element => (string?)element.Attribute(xaml + "Name") == "PanelSystem");
        Assert.NotNull(spotlightPanel);
        Assert.NotNull(systemPanel);

        Assert.Contains(spotlightPanel!.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute(xaml + "Name") == "EnableSpotlightCheck");
        Assert.DoesNotContain(systemPanel!.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute(xaml + "Name") == "EnableSpotlightCheck");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
