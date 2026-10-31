using System;
using System.Globalization;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public class LocalizationAndViewModelCultureTests
{
    private static readonly string[] SupportedLanguages =
    {
        "en", "vi", "es", "fr", "de", "ja", "zh", "ko",
        "pt", "ru", "ar", "hi", "it", "tr", "pl", "nl", "id"
    };

    private static readonly string[] RequiredKeys =
    {
        "common.new",
        "common.close",
        "setup.uninstall.invalidDir",
        "settings.translation.modelDownloadProgress",
        "update.preparingDownload",
        "media.nowPlaying",
        "media.noMedia",
        "media.unknownArtist",
        "nav.media",
        "nav.timer",
        "nav.audio"
    };

    [Fact]
    public void SecondaryViewModel_UpdateCalendar_UsesActiveLocalizedCulture()
    {
        string previousLanguage = Loc.CurrentLanguage;
        try
        {
            Loc.SetLanguage("vi");
            var vm = new SecondaryViewModel(null!);
            var sampleDate = new DateTime(2026, 3, 15);

            vm.UpdateCalendar(sampleDate);

            var viCulture = CultureInfo.GetCultureInfo("vi-VN");
            string expectedMonth = sampleDate.ToString("MMM", viCulture).ToUpper(viCulture);
            Assert.Equal(expectedMonth, vm.CalendarMonth);
            Assert.Equal("15", vm.CalendarDay);
        }
        finally
        {
            Loc.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void MediaViewModel_UsesLocalizedStringsForEmptyAndActiveMediaStates()
    {
        string previousLanguage = Loc.CurrentLanguage;
        try
        {
            Loc.SetLanguage("vi");
            var vm = new MediaViewModel(null!, static _ => Task.CompletedTask);

            Assert.Equal(Loc.Get("media.noMedia"), vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);
            Assert.Equal(Loc.Get("media.nowPlaying"), vm.SourceName);

            vm.Update(new MediaInfo
            {
                CurrentTrack = "Sample Track",
                CurrentArtist = string.Empty,
                MediaSource = null!,
                IsPlaying = true
            });

            Assert.Equal("Sample Track", vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);
            Assert.Equal(Loc.Get("media.nowPlaying"), vm.SourceName);

            vm.Update(new MediaInfo());

            Assert.Equal(Loc.Get("media.noMedia"), vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);
            Assert.Equal(Loc.Get("media.nowPlaying"), vm.SourceName);
        }
        finally
        {
            Loc.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void LocalizationService_AllNewAndRequiredKeysExistAcrossSupportedLanguages()
    {
        string previousLanguage = Loc.CurrentLanguage;
        try
        {
            foreach (string lang in SupportedLanguages)
            {
                Loc.SetLanguage(lang);
                foreach (string key in RequiredKeys)
                {
                    string value = Loc.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(value), $"Key '{key}' was empty for language '{lang}'.");
                    Assert.NotEqual(key, value);
                }
            }
        }
        finally
        {
            Loc.SetLanguage(previousLanguage);
        }
    }
}
