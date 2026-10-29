using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationUxTests
{
    [Theory]
    [InlineData("   ", "translation.noSelection")]
    [InlineData("\u200b\u200d", "translation.invalidSelection")]
    [InlineData("...", "translation.invalidSelection")]
    [InlineData("asdfghjkl", "translation.invalidSelection")]
    public async Task InvalidSelectionDoesNotTouchTheEngine(string text, string expected)
    {
        using var engine = new EchoEngine();
        using var service = new LocalTranslationService(engine);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(text, "auto", "vi", default));
        Assert.Equal(expected, error.MessageKey);
        Assert.Equal(0, engine.Fits + engine.Calls);
    }

    [Fact]
    public async Task MeaningfulShortWordsAreNotRejectedByKeyboardMashValidation()
    {
        using var engine = new EchoEngine();
        using var service = new LocalTranslationService(engine);
        Assert.Equal("QWERTY", (await service.TranslateAsync("QWERTY", "auto", "vi", default)).Text);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task LongSelectionTranslatesPrefixAndCacheKeepsPerRequestTruncation()
    {
        using var engine = new EchoEngine();
        using var service = new LocalTranslationService(engine);
        string prefix = string.Concat(Enumerable.Repeat("hello ", 334))[..2000];
        var longResult = await service.TranslateAsync(prefix + " more text", "en", "fr", default);
        Assert.Equal(prefix.Trim(), longResult.Text);
        Assert.True(longResult.InputTruncated);
        var shortResult = await service.TranslateAsync(prefix, "en", "fr", default);
        Assert.False(shortResult.InputTruncated);
        Assert.True((await service.TranslateAsync(prefix + " different tail", "en", "fr", default)).InputTruncated);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ModelTokenLimitTruncatesBeforeInferenceAndMarksResult()
    {
        using var engine = new EchoEngine { LimitTokens = true };
        using var service = new LocalTranslationService(engine);
        string text = new string('漢', 800);
        var result = await service.TranslateAsync(text, "auto", "vi", default);
        Assert.Equal(new string('漢', 512), result.Text);
        Assert.True(result.InputTruncated);
        Assert.Equal(1, engine.Calls);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void PrefixKeepsUnicodeTextElementsTogether(string element)
    {
        string prefix = new string('a', 1999);
        Assert.Equal(prefix, TranslationInput.Prefix(prefix + element + "tail", 2000));
        Assert.Equal(prefix, TranslationInput.FitTokens(prefix + element, value => value.Length, 2000));
    }

    [Fact]
    public void NativeLongSelectionKeepsAnOverflowMarkerWithoutReadingTheWholeDocument() => SharedStaTestRunner.Run(() =>
    {
        using var box = new System.Windows.Forms.TextBox { Text = new string('a', 1999) + "😀" + new string('b', 3000) };
        _ = box.Handle;
        box.SelectAll();
        string captured = NativeSelectionReader.ReadControl(box.Handle)!;
        Assert.Equal(2001, captured.Length);
        Assert.Equal(new string('a', 1999), TranslationInput.Prepare(captured));
    });

    [Theory]
    [InlineData(50u)]
    [InlineData(uint.MaxValue - 200u)]
    public void TypingAndShiftSelectionRequireAQuietPeriod(uint start)
    {
        var detector = new ShiftTapDetector();
        var window = new IntPtr(1);
        detector.Process(0x41, true, start, window);
        detector.Process(0x41, false, unchecked(start + 20), window);
        detector.Process(0xA0, true, unchecked(start + 50), window);
        Assert.False(detector.Process(0xA0, false, unchecked(start + 80), window));
        detector.Process(0xA0, true, unchecked(start + 150), window);
        Assert.False(detector.Process(0xA0, false, unchecked(start + 180), window));
        detector.Process(0xA0, true, unchecked(start + 700), window);
        Assert.False(detector.Process(0xA0, false, unchecked(start + 740), window));
        detector.Process(0xA1, true, unchecked(start + 800), window);
        Assert.True(detector.Process(0xA1, false, unchecked(start + 840), window));
    }

    [Fact]
    public void AppExclusionsAndFullscreenPreferenceRoundTrip()
    {
        var settings = new NotchSettings { TranslationIgnoreFullscreen = false, TranslationExcludedApps = " GAME.exe ; editor, OTHER.EXE\n game" };
        var saved = System.Text.Json.JsonSerializer.Deserialize<NotchSettings>(System.Text.Json.JsonSerializer.Serialize(settings.Clone()))!;
        var apps = TranslationAppPolicy.ParseApps(saved.TranslationExcludedApps);
        Assert.Equal(3, apps.Count);
        Assert.True(TranslationAppPolicy.ShouldBlock(false, false, apps, "game"));
        Assert.True(TranslationAppPolicy.ShouldBlock(false, false, apps, "OTHER.exe"));
        Assert.False(TranslationAppPolicy.ShouldBlock(saved.TranslationIgnoreFullscreen, true, apps, "chrome"));
        Assert.True(TranslationAppPolicy.ShouldBlock(true, true, apps, "chrome"));
        Assert.False(TranslationAppPolicy.ShouldBlock(true, false, apps, "chrome"));
    }

    [Fact]
    public void ExplicitSgdWinsOverWindowsRegionAndSourceCanBeChanged()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.False(TranslationCurrencies.HasConflictingCurrency("$50 SGD", "auto"));
            Assert.True(TranslationCurrencies.TryParse("$50 SGD", "auto", out var explicitPrice));
            Assert.Equal("SGD", explicitPrice.Currency);
            Assert.False(explicitPrice.AssumedCurrency);
            Assert.True(TranslationCurrencies.TryParse("$50", "auto", out var changed, "SGD"));
            Assert.Equal("SGD", changed.Currency);
            Assert.False(changed.AssumedCurrency);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
            Assert.True(TranslationCurrencies.HasUnresolvedSymbol("$50", "auto"));
            Assert.True(TranslationCurrencies.TryParse("$50", "auto", out changed, "SGD"));
            Assert.Equal(50m, changed.Amount);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("VND", "2500000")]
    [InlineData("USD", "1234.56")]
    public void CurrencyActionsUseTheSelectedOutputFormat(string currency, string numeric) => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("auto", "vi");
        try
        {
            decimal amount = decimal.Parse(numeric, CultureInfo.InvariantCulture);
            window.ShowResult(new("≈ " + amount.ToString("N2", CultureInfo.GetCultureInfo("vi-VN")) + " " + currency, "auto", "vi", false)
            { Currency = new(new(100m, "SGD"), currency, amount, null, false) });
            Assert.Equal(numeric, window.OutputText);
            Assert.Equal("SGD", window.SourceCurrencyCombo.SelectedValue);
            Assert.Equal(currency, window.TargetCurrencyCombo.SelectedValue);
            window.CurrencyNumberOnlyCheck.IsChecked = false;
            Assert.Equal(window.ResultText.Text, window.OutputText);
            int changes = 0;
            window.CurrenciesChanged += (source, target) => { Assert.Equal("EUR", source); Assert.Equal(currency, target); changes++; };
            window.SourceCurrencyCombo.SelectedValue = "EUR";
            Assert.Equal(1, changes);
            Assert.False(window.CopyButton.IsEnabled);
            Assert.Empty(window.ResultText.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TruncatedTranslationDisablesReplacementAndReadOnlyButtonsExplainWhy() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "hello", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            window.ShowResult(new("xin chào", "en", "vi", true));
            Assert.False(window.ReplaceButton.IsEnabled);
            Assert.Equal(Loc.Get("translation.replaceReadOnly"), window.ReplaceButton.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(window.ReplaceButton));
            window.ShowSelection(new(2, "hello", new Rect(100, 100, 100, 20), IntPtr.Zero, true), true);
            await window.WaitForEntranceAsync(ct);
            window.ShowResult(new("xin chào", "en", "vi", true) { InputTruncated = true });
            Assert.False(window.CanReplaceResult);
            Assert.False(window.ReplaceButton.IsEnabled);
            Assert.True(window.CopyButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.TruncatedBadge.Visibility);
            Assert.Equal(Loc.Get("translation.replaceTruncated"), window.ReplaceButton.ToolTip);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MissingModelKeepsShortcutAndOffersDirectSettingsAction() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var engine = new EchoEngine();
        var store = new TranslationModelStore(Path.Combine(Path.GetTempPath(), "vnotch-missing-" + Guid.NewGuid()));
        int opened = 0;
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => opened++, store, engine);
        controller.ApplySettings(new NotchSettings { EnableLiveTranslation = true, EnableCurrencyConversion = false });
        var type = typeof(LiveTranslationController);
        Assert.NotNull(type.GetField("_shiftShortcut", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller));
        var window = (TranslationWindow)type.GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        type.GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowSelection(new(1, "hello", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WpfFrameWaiter.UntilAsync(() => window.ManageModelButton.Visibility == Visibility.Visible, "missing model CTA", ct);
        Assert.Equal(Loc.Get("translation.modelMissing"), window.StatusText.Text);
        window.ManageModelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opened);
        Assert.Equal(0, engine.Calls);
    });

    [Theory]
    [InlineData(-1920, 0, 1920, 1040, 1.25)]
    [InlineData(1920, -600, 3840, 2120, 1.5)]
    public void PopupFlipsAboveBottomRightAndFitsPhysicalWorkArea(int left, int top, int width, int height, double scale)
    {
        var work = new Rect(left, top, width, height);
        var selection = new Rect(work.Right - 40, work.Bottom - 25, 35, 20);
        double popupWidth = 536 * scale, popupHeight = 440 * scale;
        var point = TranslationWindow.ClampPlacement(selection, work, popupWidth, popupHeight);
        Assert.True(point.Y + popupHeight < selection.Top);
        Assert.True(work.Contains(new Rect(point, new Size(popupWidth, popupHeight))));
    }

    private sealed class EchoEngine : ILocalTranslationEngine
    {
        internal int Fits, Calls;
        internal bool LimitTokens;
        public Task<string> FitInputAsync(string text, CancellationToken cancellationToken)
        {
            Fits++;
            return Task.FromResult(LimitTokens ? TranslationInput.FitTokens(text, value => value.EnumerateRunes().Count(), 512) : text);
        }
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) { Calls++; return Task.FromResult(text); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
