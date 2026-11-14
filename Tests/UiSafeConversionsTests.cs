using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class UiSafeConversionsTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("hi")]
    [InlineData("zh")]
    [InlineData("ko")]
    [InlineData("ru")]
    public void WordClock_FormatsAllTenLanguagesAtMidnightNoonAndMinutes(string lang)
    {
        try
        {
            Loc.SetLanguage(lang);
            string prefix = Loc.Get("wordClock.prefix");
            string oclock = Loc.Get("wordClock.oclock");

            string midnight = WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 0, 0));
            string noon = WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 12, 0, 0));
            string lateNight = WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 23, 59, 0));

            Assert.Equal(midnight, noon);
            Assert.False(string.IsNullOrWhiteSpace(midnight));
            Assert.False(string.IsNullOrWhiteSpace(lateNight));
            Assert.StartsWith(prefix + "\n", midnight, StringComparison.Ordinal);

            if (lang is "en" or "zh" or "ko" or "ru")
            {
                Assert.Equal($"{prefix}\nTwelve\n{oclock}", midnight);
                Assert.Equal($"{prefix}\nEleven\nFifty Nine", lateNight);
            }
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void WordClock_PreservesLanguageSpecificGrammarRules()
    {
        try
        {
            Loc.SetLanguage("es");
            string esPrefix = Loc.Get("wordClock.prefix");
            string esOclock = Loc.Get("wordClock.oclock");
            Assert.Equal($"Es la\nuna\n{esOclock}", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 1, 0, 0)));
            Assert.Equal($"Es la\nuna\ny veintiuno", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 13, 21, 0)));
            Assert.Equal($"{esPrefix}\ndoce\n{esOclock}", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 0, 0)));

            Loc.SetLanguage("vi");
            string viPrefix = Loc.Get("wordClock.prefix");
            Assert.Equal($"{viPrefix}\nmười hai giờ", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 0, 0)));
            Assert.Equal($"{viPrefix}\nmột giờ\nlẻ năm", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 13, 5, 0)));
            Assert.Equal($"{viPrefix}\nhai giờ\nmười lăm", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 14, 15, 0)));
            Assert.Equal($"{viPrefix}\nba giờ\nhai mươi mốt", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 15, 21, 0)));

            Loc.SetLanguage("fr");
            string frPrefix = Loc.Get("wordClock.prefix");
            string frOclock = Loc.Get("wordClock.oclock");
            Assert.Equal($"{frPrefix}\nune heure\n{frOclock}", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 1, 0, 0)));
            Assert.Equal($"{frPrefix}\ndeux heures\nvingt-et-une", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 14, 21, 0)));
            Assert.Equal($"{frPrefix}\ntrois heures\nonze", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 15, 11, 0)));

            Loc.SetLanguage("de");
            string dePrefix = Loc.Get("wordClock.prefix");
            Assert.Equal($"{dePrefix}\nein Uhr", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 1, 0, 0)));
            Assert.Equal($"{dePrefix}\nzwölf Uhr\neinundzwanzig", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 21, 0)));

            Loc.SetLanguage("ja");
            string jaPrefix = Loc.Get("wordClock.prefix");
            string jaOclock = Loc.Get("wordClock.oclock");
            Assert.Equal($"{jaPrefix}\n{jaOclock}十二時", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 0, 0)));
            Assert.Equal($"{jaPrefix}\n一時\n十五分", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 13, 15, 0)));

            Loc.SetLanguage("hi");
            string hiPrefix = Loc.Get("wordClock.prefix");
            string hiOclock = Loc.Get("wordClock.oclock");
            Assert.Equal($"{hiPrefix}\nबारह {hiOclock}", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 0, 0, 0)));
            Assert.Equal($"{hiPrefix}\nएक बजकर\nपाँच मिनट", WordClock.FormatLocalizedTime(new DateTime(2026, 11, 14, 1, 5, 0)));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void DesktopEdgeReveal_NativeProbeHandlesNullInvalidAndSameProcessWindows() => SharedStaTestRunner.Run(() =>
    {
        Assert.False(MainWindow.ProbeNotchObscuredByAnyWindow(IntPtr.Zero));
        Assert.False(MainWindow.ProbeNotchObscuredByAnyWindow(new IntPtr(0x12345678)));

        var notch = new BackgroundWindow { Width = 120, Height = 36, Left = -32000, Top = -32000 };
        var sibling = new BackgroundWindow { Width = 200, Height = 100, Left = -32000, Top = -32000 };
        try
        {
            notch.Show();
            sibling.Show();
            IntPtr notchHwnd = new WindowInteropHelper(notch).Handle;
            Assert.NotEqual(IntPtr.Zero, notchHwnd);
            // Same-process windows and off-screen coordinates must not report false obscuration.
            Assert.False(MainWindow.ProbeNotchObscuredByAnyWindow(notchHwnd));
        }
        finally
        {
            sibling.Close();
            notch.Close();
        }
    });

    [Fact]
    public void AiMarkdown_CloseSpansBalancesFencesInlineCodeAndTrimmedEmphasis()
    {
        string clean = "Plain balanced **text**";
        Assert.Same(clean, AiMarkdown.CloseSpans(clean));
        Assert.Equal("```csharp\nint x = 1;\n```", AiMarkdown.CloseSpans("```csharp\nint x = 1;"));
        Assert.Equal("~~~~\nblock\n~~~~", AiMarkdown.CloseSpans("~~~~\nblock"));
        Assert.Equal("inline ``code``", AiMarkdown.CloseSpans("inline ``code"));
        Assert.Equal("trailing `` ` ``", AiMarkdown.CloseSpans("trailing `` `"));
        Assert.Equal("hello **bold**   ", AiMarkdown.CloseSpans("hello **bold   "));
        Assert.Equal("**outer *inner***", AiMarkdown.CloseSpans("**outer *inner"));
    }

    [Fact]
    public void LiquidGlass_NativeCapacityAndOpticalHelpersMatchCheckedContracts()
    {
        Assert.Equal(0, LiquidGlassController.GrowPresentCapacity(256, 0, 64));
        Assert.Equal(256, LiquidGlassController.GrowPresentCapacity(0, 180, 64));
        Assert.Throws<OverflowException>(() => LiquidGlassController.GrowPresentCapacity(0, int.MaxValue, 64));
        Assert.Throws<OverflowException>(() => LiquidGlassController.GrowPixelBufferCapacity(int.MaxValue - 100, int.MaxValue));

        Span<float> lut = stackalloc float[1024];
        LiquidGlassController.BuildOpticalLut(1.0f, 24.0f, lut);
        Assert.Equal(0.0f, lut[0], 5);
        Assert.Equal(0.0f, lut[1023], 5);
        Assert.True(lut[256] > 0.0f);
    }

    [Fact]
    public void WindowPlacementAndManifestEntryValidatorsMatchParity()
    {
        var region = SpotifyLoginWindow.CalculateRoundedWindowRegion(420, 640, 1.5);
        Assert.Equal(27, region.Left);
        Assert.Equal(27, region.Top);
        Assert.Equal(603, region.Right);
        Assert.Equal(933, region.Bottom);
        Assert.Equal(72, region.CornerDiameter);

        var clamped = TranslationWindow.ClampPlacement(
            new Rect(100, 950, 80, 24),
            new Rect(0, 0, 1920, 1000),
            300,
            180);
        Assert.Equal(100, clamped.X);
        Assert.Equal(762, clamped.Y);

        string tempDir = Directory.CreateTempSubdirectory("vnotch-manifest-entry-").FullName;
        string manifestPath = Path.Combine(tempDir, InstallDirectoryCleanup.ManifestFileName);
        try
        {
            string validSha = new string('a', 64);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(
                new InstallDirectoryCleanup.InstallManifest(2, [new("../escape.dll", validSha)])));
            Assert.Throws<IOException>(() => InstallDirectoryCleanup.ReadManifest(tempDir));

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(
                new InstallDirectoryCleanup.InstallManifest(2, [new("installed-files.json", validSha)])));
            Assert.Throws<IOException>(() => InstallDirectoryCleanup.ReadManifest(tempDir));
        }
        finally
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
        }
    }
}
