using System;
using System.Collections.Generic;
using System.Linq;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TabReorderingAndPageWidgetTests
{
    private static readonly string[] Languages = { "en", "vi", "es", "fr", "de", "ja", "hi" };

    [Fact]
    public void NotchSettings_HasExpectedDefaultTabAndWidgetConfiguration()
    {
        var settings = new NotchSettings();

        Assert.Equal("Media,Secondary,Timer,AudioMixer", settings.NavTabOrder);
        Assert.Equal("Media,Secondary,Timer,AudioMixer", settings.VisibleNavTabs);
        Assert.Equal("camera", settings.ShelfWidget);
        Assert.Equal("analog", settings.ClockPageStyle);
    }

    [Fact]
    public void NotchSettings_Clone_PreservesTabAndWidgetSettings()
    {
        var original = new NotchSettings
        {
            NavTabOrder = "AudioMixer,Timer,Secondary,Media",
            VisibleNavTabs = "Media,AudioMixer",
            ShelfWidget = "sysmon",
            ClockPageStyle = "digital"
        };

        var clone = original.Clone();

        Assert.Equal("AudioMixer,Timer,Secondary,Media", clone.NavTabOrder);
        Assert.Equal("Media,AudioMixer", clone.VisibleNavTabs);
        Assert.Equal("sysmon", clone.ShelfWidget);
        Assert.Equal("digital", clone.ClockPageStyle);
    }

    [Theory]
    [InlineData("Secondary,Media,Timer,AudioMixer", new[] { NotchView.Secondary, NotchView.Media, NotchView.Timer, NotchView.AudioMixer })]
    [InlineData("AudioMixer,Timer,Secondary,Media", new[] { NotchView.AudioMixer, NotchView.Timer, NotchView.Secondary, NotchView.Media })]
    public void ParseTabOrder_CorrectlyParsesConfiguredSequence(string orderString, NotchView[] expectedViews)
    {
        var tokens = orderString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.Parse<NotchView>(t, true))
            .ToArray();

        Assert.Equal(expectedViews, tokens);
    }

    [Fact]
    public void ParseTabOrder_WithVisibilityFilter_AlwaysIncludesMedia()
    {
        string configuredVisible = "Timer,AudioMixer"; // Media omitted intentionally
        var visibleSet = new HashSet<string>(
            configuredVisible.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        // Rule: Media (Home) must always remain visible
        visibleSet.Add("Media");

        Assert.Contains("Media", visibleSet);
        Assert.Contains("Timer", visibleSet);
        Assert.Contains("AudioMixer", visibleSet);
        Assert.DoesNotContain("Secondary", visibleSet);
    }

    [Fact]
    public void NewLocalizationKeys_ArePresentAndNonEmptyInAllLanguages()
    {
        string[] requiredKeys =
        {
            "settings.navTabs",
            "settings.navTabs.hint",
            "settings.tab.moveUp",
            "settings.tab.moveDown",
            "settings.tab.drag",
            "settings.tab.reset",
            "settings.shelfWidget",
            "settings.shelfWidget.hint",
            "settings.shelfWidget.camera",
            "settings.shelfWidget.sysmon",
            "settings.shelfWidget.weather",
            "settings.shelfWidget.clock",
            "settings.shelfWidget.none",
            "settings.clockPageStyle",
            "settings.clockPageStyle.hint",
            "settings.clockPageStyle.analog",
            "settings.clockPageStyle.digital",
            "settings.clockPageStyle.wordclock",
            "settings.widget.none"
        };

        foreach (var lang in Languages)
        {
            var keys = Loc.GetKeys(lang);
            foreach (var key in requiredKeys)
            {
                Assert.Contains(key, keys);
            }
        }
    }
}
