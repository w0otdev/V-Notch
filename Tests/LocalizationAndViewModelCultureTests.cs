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
    public void MediaViewModel_UsesLocalizedStringsForEmptyAndActiveMediaStates()
    {
        string previousLanguage = Loc.CurrentLanguage;
        try
        {
            Loc.SetLanguage("vi");
            var vm = new MediaViewModel(null!);

            Assert.Equal(Loc.Get("media.noMedia"), vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);

            vm.Update(new MediaInfo
            {
                CurrentTrack = "Sample Track",
                CurrentArtist = string.Empty,
                MediaSource = null!,
                IsPlaying = true
            });

            Assert.Equal("Sample Track", vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);

            vm.Update(new MediaInfo());

            Assert.Equal(Loc.Get("media.noMedia"), vm.Title);
            Assert.Equal(Loc.Get("media.unknownArtist"), vm.Artist);
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
