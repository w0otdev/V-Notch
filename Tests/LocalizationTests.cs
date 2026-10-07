using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollectionDefinition { }

[Collection("Localization")]
public sealed class LocalizationTests
{
    private static readonly string[] Languages =
    {
        "en", "vi", "zh", "pt", "ru", "ar", "ko", "es", "fr", "de", "ja", "hi", "it", "tr", "pl", "nl", "id"
    };

    [Fact]
    public void AllSupportedLanguagesHaveTheSameTranslationKeys()
    {
        var englishKeys = Loc.GetKeys("en").OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(englishKeys);
        foreach (var language in Languages)
        {
            var keys = Loc.GetKeys(language).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.Equal(englishKeys, keys);
        }
    }

    [Fact]
    public void FormatPlaceholdersMatchAcrossLanguages()
    {
        foreach (var key in Loc.GetKeys("en"))
        {
            Loc.SetLanguage("en");
            var expected = GetPlaceholderIndexes(Loc.Get(key));

            foreach (var language in Languages.Skip(1))
            {
                Loc.SetLanguage(language);
                Assert.Equal(expected, GetPlaceholderIndexes(Loc.Get(key)));
            }
        }

        Loc.SetLanguage("en");
    }

    [Fact]
    public void LocaleFilesDoNotContainDuplicateKeys()
    {
        string root = FindRepositoryRoot();
        foreach (string language in Languages)
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Locales", language + ".json")));
            var duplicates = document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => language + ": " + group.Key);
            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void NewClipboardInstructionsAreTranslatedInEveryLanguage()
    {
        string[] keys =
        {
            "nav.clipboard", "settings.hideCamera", "settings.hideCamera.hint",
            "settings.clipboardHotkey", "settings.clipboardHotkey.hint", "clipboard.empty",
            "clipboard.locked", "clipboard.setPin", "clipboard.enterPin", "clipboard.confirmPin",
            "clipboard.error", "clipboard.readError", "clipboard.saveError", "clipboard.largeFile",
            "clipboard.pause", "clipboard.resume", "clipboard.shortcuts", "clipboard.info",
            "clipboard.invalidHotkey", "clipboard.hotkeyOsReserved", "clipboard.hotkeyConflict",
            "clipboard.forgotPin", "clipboard.resetTitle", "clipboard.resetConfirm", "clipboard.reset",
            "clipboard.noResults", "clipboard.typeDelete", "clipboard.deleteConfirm",
            "clipboard.keyboardShortcuts", "clipboard.information", "clipboard.personalPasscode", "clipboard.unlockPersonal"
        };
        try
        {
            Loc.SetLanguage("en");
            var english = keys.ToDictionary(key => key, Loc.Get);
            foreach (string language in Languages.Skip(1))
            {
                Loc.SetLanguage(language);
                foreach (string key in keys)
                {
                    string value = Loc.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(value), $"{language}: {key} is empty");
                    Assert.NotEqual(english[key], value);
                }
            }
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Theory]
    [InlineData("vi", "Hình ảnh", "Bộ nhớ tạm", "512 byte", "2 phút", "1,5 KB")]
    [InlineData("de", "Bild", "Zwischenablage", "512 Bytes", "2 Min.", "1,5 KB")]
    [InlineData("ja", "画像", "クリップボード", "512 バイト", "2 分", "1.5 KB")]
    public void ClipboardCardMetadataUsesSelectedLanguageAndPreservesUserContent(
        string language, string image, string source, string bytes, string age, string size) => SharedStaTestRunner.Run(() =>
    {
        try
        {
            Loc.SetLanguage("en");
            var copiedUtc = DateTime.UtcNow;
            var card = new VNotch.ViewModels.ClipboardCardViewModel(new VNotch.Models.ClipboardEntry
            {
                Kind = VNotch.Models.ClipboardKind.Image,
                Title = "Image",
                ByteSize = 512,
                CopiedUtc = copiedUtc
            });
            var changedProperties = new HashSet<string?>();
            card.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

            Loc.SetLanguage(language);
            card.ApplyLocalization();
            card.RefreshAge(copiedUtc.AddMinutes(2));
            Assert.Equal(image, card.Title);
            Assert.Equal(source, card.Source);
            Assert.Equal(bytes, card.Size);
            Assert.Equal(age, card.Age);
            Assert.Contains(nameof(card.Title), changedProperties);
            Assert.Contains(nameof(card.Source), changedProperties);
            Assert.Contains(nameof(card.Size), changedProperties);
            card.UpdateEntry(card.Entry with { ByteSize = 1536 });
            Assert.Equal(size, card.Size);

            var text = new VNotch.ViewModels.ClipboardCardViewModel(new VNotch.Models.ClipboardEntry
            {
                Kind = VNotch.Models.ClipboardKind.Text,
                Title = "Image",
                Preview = "User content",
                SourceApp = "Example App"
            });
            Assert.Equal("Image", text.Title);
            Assert.Equal("User content", text.Preview);
            Assert.Equal("Example App", text.Source);
        }
        finally { Loc.SetLanguage("en"); }
    });

    [Fact]
    public void HindiTranslationsDoNotFallBackToEnglish()
    {
        var allowedTechnicalLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.skin.liquidglass",
            "settings.youtubeApiKey",
            "settings.enableSpotifyCanvas",
            "greeting.hello" // Greetings intentionally fall back to English outside Vietnamese.
        };
        var untranslated = new List<string>();

        foreach (var key in Loc.GetKeys("en"))
        {
            Loc.SetLanguage("en");
            string english = Loc.Get(key);
            Loc.SetLanguage("hi");
            string hindi = Loc.Get(key);

            if (!string.IsNullOrWhiteSpace(english) &&
                string.Equals(english, hindi, StringComparison.Ordinal) &&
                !allowedTechnicalLabels.Contains(key) && !key.StartsWith("clipboard.category.", StringComparison.Ordinal))
            {
                untranslated.Add(key);
            }
        }

        Loc.SetLanguage("en");
        Assert.Empty(untranslated);
    }

    [Fact]
    public void HindiTranslationsContainNativeScriptOutsideTechnicalLabels()
    {
        var allowedTechnicalLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.skin.liquidglass",
            "settings.youtubeApiKey",
            "settings.enableSpotifyCanvas",
            "greeting.hello" // Greetings intentionally fall back to English outside Vietnamese.
        };
        var nonNativeValues = new List<string>();

        Loc.SetLanguage("hi");
        foreach (var key in Loc.GetKeys("hi"))
        {
            string value = Loc.Get(key);
            if (!string.IsNullOrWhiteSpace(value) &&
                !allowedTechnicalLabels.Contains(key) && !key.StartsWith("clipboard.category.", StringComparison.Ordinal) &&
                !Regex.IsMatch(value, "[\\u0900-\\u097F]"))
            {
                nonNativeValues.Add(key);
            }
        }

        Loc.SetLanguage("en");
        Assert.Empty(nonNativeValues);
    }

    [Fact]
    public void HindiAppearsInTheLanguagePickerWithItsNativeName()
    {
        Assert.Contains(Loc.GetAvailableLanguages(), language =>
            language.Code == "hi" && language.Name == "हिन्दी");
    }

    [Fact]
    public void EveryLiteralLocalizationKeyUsedByTheAppExists()
    {
        string repositoryRoot = FindRepositoryRoot();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var keyPattern = new Regex("(?:Loc\\.Get\\(|LocalizationKey\\s*=\\s*)\\\"(?<key>[^\\\"]+)\\\"(?!\\s*\\+)",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(repositoryRoot, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}.test-output{Path.DirectorySeparatorChar}")))
        {
            foreach (Match match in keyPattern.Matches(File.ReadAllText(file)))
                usedKeys.Add(match.Groups["key"].Value);
        }

        var englishKeys = Loc.GetKeys("en").ToHashSet(StringComparer.Ordinal);
        Assert.Empty(usedKeys.Except(englishKeys, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("vi", "vi-VN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("pt", "pt-BR")]
    [InlineData("ru", "ru-RU")]
    [InlineData("ar", "ar-SA")]
    [InlineData("ko", "ko-KR")]
    [InlineData("es", "es-ES")]
    [InlineData("fr", "fr-FR")]
    [InlineData("de", "de-DE")]
    [InlineData("ja", "ja-JP")]
    [InlineData("hi", "hi-IN")]
    [InlineData("it", "it-IT")]
    [InlineData("tr", "tr-TR")]
    [InlineData("pl", "pl-PL")]
    [InlineData("nl", "nl-NL")]
    [InlineData("id", "id-ID")]
    public void LanguageUsesTheExpectedCulture(string language, string culture)
    {
        Loc.SetLanguage(language);
        Assert.Equal(culture, Loc.GetCulture().Name);
        Loc.SetLanguage("en");
    }

    [Theory]
    [InlineData("en", 11, 13, "It's\nEleven\nThirteen")]
    [InlineData("en", 11, 0, "It's\nEleven\nO'Clock")]
    [InlineData("vi", 11, 13, "Bây giờ là\nmười một giờ\nmười ba")]
    [InlineData("vi", 11, 0, "Bây giờ là\nmười một giờ")]
    [InlineData("es", 11, 13, "Son las\nonce\ny trece")]
    [InlineData("es", 11, 0, "Son las\nonce\nEn punto")]
    [InlineData("fr", 11, 13, "Il est\nonze heures\ntreize")]
    [InlineData("fr", 11, 0, "Il est\nonze heures\nPile")]
    [InlineData("de", 11, 13, "Es ist\nelf Uhr\ndreizehn")]
    [InlineData("de", 11, 0, "Es ist\nelf Uhr")]
    [InlineData("ja", 11, 13, "現在\n十一時\n十三分")]
    [InlineData("ja", 11, 0, "現在\nちょうど十一時")]
    [InlineData("hi", 11, 13, "अभी समय है\nग्यारह बजकर\nतेरह मिनट")]
    [InlineData("hi", 11, 0, "अभी समय है\nग्यारह बजे")]
    public void WordClockUsesNativeTimeGrammarForEverySupportedLanguage(
        string language, int hour, int minute, string expected)
    {
        Loc.SetLanguage(language);

        string actual = WordClock.FormatLocalizedTime(new DateTime(2026, 7, 18, hour, minute, 0));

        Assert.Equal(expected, actual);
        Loc.SetLanguage("en");
    }

    [Theory]
    [InlineData("en", 11, 5, "It's\nEleven\nOh Five")]
    [InlineData("en", 11, 21, "It's\nEleven\nTwenty One")]
    [InlineData("vi", 11, 5, "Bây giờ là\nmười một giờ\nlẻ năm")]
    [InlineData("es", 1, 13, "Es la\nuna\ny trece")]
    [InlineData("fr", 1, 21, "Il est\nune heure\nvingt-et-une")]
    [InlineData("de", 1, 13, "Es ist\nein Uhr\ndreizehn")]
    public void WordClockHandlesLanguageSpecificTimeGrammarEdges(
        string language, int hour, int minute, string expected)
    {
        Loc.SetLanguage(language);

        string actual = WordClock.FormatLocalizedTime(new DateTime(2026, 7, 18, hour, minute, 0));

        Assert.Equal(expected, actual);
        Loc.SetLanguage("en");
    }

    [Theory]
    [InlineData("en", "Drizzle")]
    [InlineData("vi", "Mưa phùn")]
    [InlineData("es", "Llovizna")]
    [InlineData("fr", "Bruine")]
    [InlineData("de", "Nieselregen")]
    [InlineData("ja", "霧雨")]
    [InlineData("hi", "बूंदाबांदी")]
    public void WeatherConditionsUseTheSelectedLanguage(string language, string expected)
    {
        Loc.SetLanguage(language);

        Assert.Equal(expected, WeatherConditionFormatter.Format(51));

        Loc.SetLanguage("en");
    }

    [Fact]
    public void WeatherTextReformatsImmediatelyWhenSwitchingFromHindiToVietnamese()
    {
        Loc.SetLanguage("hi");
        Assert.Contains("अधिकतम", Loc.Get("weather.highLow", 32, 26));

        Loc.SetLanguage("vi");
        Assert.Equal("Mưa phùn", WeatherConditionFormatter.Format(51));
        Assert.Equal("C:32° T:26°", Loc.Get("weather.highLow", 32, 26));

        Loc.SetLanguage("en");
    }

    private static int[] GetPlaceholderIndexes(string value) =>
        Regex.Matches(value, @"\{(?<index>\d+)(?:[^}]*)\}")
            .Select(match => int.Parse(match.Groups["index"].Value))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
