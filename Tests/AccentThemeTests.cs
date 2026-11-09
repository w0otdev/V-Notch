using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AccentThemeTests
{
    [Fact]
    public void MutedAccentsKeepReadableTextOnBothNormalAndHoverFills()
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255.0;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }

        double ink = Luminance(AccentTheme.ForegroundColor);
        foreach (var swatch in AccentTheme.Swatches)
        foreach (Color color in new[] { swatch.Color, swatch.Strong })
        {
            Assert.True((Luminance(color) + .05) / (ink + .05) >= 4.5,
                $"{swatch.Id} must keep at least 4.5:1 text contrast");
            Assert.All(new[] { color.R, color.G, color.B }, channel => Assert.InRange(channel, (byte)1, (byte)254));
        }
    }

    [Fact]
    public void ColorPickSurvivesAResourceConsumerSealingAnIdleBrush() =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reducedMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(false);
            var window = new SettingsWindow(new NotchSettings { AccentColor = "blue" }, new RecordingService());
            try
            {
                window.Show();
                await Task.Delay(50, ct);
                var choices = ((WrapPanel)window.FindName("AccentColorChoices")).Children.OfType<RadioButton>().ToArray();
                choices.Single(choice => (string)choice.Tag == "purple").IsChecked = true;
                var publishedBrush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                // WPF resource consumers may seal a shared Freezable as soon as it becomes idle.
                publishedBrush.Changed += (_, _) =>
                {
                    if (publishedBrush.CanFreeze) publishedBrush.Freeze();
                };
                choices.Single(choice => (string)choice.Tag == "green").IsChecked = true;
                await Task.Delay(300, ct);
                Assert.Equal(AccentTheme.GetSwatch("green").Color,
                    ((SolidColorBrush)((Button)window.FindName("SaveButton")).Background).Color);
            }
            finally
            {
                window.Close();
                AccentTheme.Apply("default");
                AnimationConfig.SetReduceMotion(reducedMotion);
            }
        });

    [Fact]
    public void VisibleSettingsAnimateSharedColorsAndRapidPicksKeepTheCurrentColor() =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reducedMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(false);
            BackgroundTestWindows.EnsureApplicationResources();
            var window = new SettingsWindow(new NotchSettings { AccentColor = "blue" }, new RecordingService());
            try
            {
                window.Show();
                await Task.Delay(50, ct);
                var choices = ((WrapPanel)window.FindName("AccentColorChoices")).Children.OfType<RadioButton>().ToArray();
                var save = (Button)window.FindName("SaveButton");
                choices.Single(choice => (string)choice.Tag == "purple").IsChecked = true;
                var brush = (SolidColorBrush)save.Background;

                Assert.Same(brush, Application.Current.Resources["AccentBrush"]);
                Assert.True(brush.HasAnimatedProperties);
                Assert.Equal(AccentTheme.GetSwatch("purple").Color, brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty));
                Assert.Equal(AccentTheme.GetSwatch("blue").Color, brush.Color);

                // The main window also applies each preview. Repeating the target must preserve the fade.
                AccentTheme.Apply("purple");
                Assert.True(brush.HasAnimatedProperties);
                await Task.Delay(60, ct);
                Color displayedColor = brush.Color;
                choices.Single(choice => (string)choice.Tag == "green").IsChecked = true;
                brush = (SolidColorBrush)save.Background;
                Assert.Equal(displayedColor, brush.Color);
                Assert.Equal(AccentTheme.GetSwatch("green").Color, brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty));

                await Task.Delay(400, ct);
                Assert.Equal(AccentTheme.GetSwatch("green").Color, brush.Color);
                Assert.Equal("green", window.ReadSettingsFromUi().AccentColor);
                Assert.Equal(AccentTheme.ForegroundColor,
                    ((SolidColorBrush)((TextBlock)window.FindName("NavAppearanceText")).Foreground).Color);
            }
            finally
            {
                window.Close();
                AccentTheme.Apply("default");
                AnimationConfig.SetReduceMotion(reducedMotion);
            }
        });

    [Fact]
    public void ReducedMotionFinishesActiveFadesAndFrozenStartupBrushesCanChange() =>
        SharedStaTestRunner.Run(() =>
        {
            BackgroundTestWindows.EnsureApplicationResources();
            bool reducedMotion = AnimationConfig.ReduceMotion;
            try
            {
                AnimationConfig.SetReduceMotion(false);
                AccentTheme.Apply("blue");
                var frozen = AccentTheme.Brush(AccentTheme.GetSwatch("blue").Color);
                Application.Current.Resources["AccentBrush"] = frozen;
                AccentTheme.Apply("pink", animate: true);
                var brush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                Assert.NotSame(frozen, brush);
                Assert.True(brush.HasAnimatedProperties);

                AnimationConfig.SetReduceMotion(true);
                AccentTheme.Apply("pink", animate: true);
                brush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                Assert.False(brush.HasAnimatedProperties);
                Assert.Equal(AccentTheme.GetSwatch("pink").Color, brush.Color);

                AccentTheme.Apply("yellow", animate: true);
                brush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                Assert.False(brush.HasAnimatedProperties);
                Assert.Equal(AccentTheme.GetSwatch("yellow").Color, brush.Color);
            }
            finally
            {
                AccentTheme.Apply("default");
                AnimationConfig.SetReduceMotion(reducedMotion);
            }
        });

    [Fact]
    public void SwatchesPreviewAcrossExistingControlsAndCancelRestoresTheSavedColor() =>
        SharedStaTestRunner.Run(() =>
        {
            BackgroundTestWindows.EnsureApplicationResources();
            var service = new RecordingService();
            var window = new SettingsWindow(new NotchSettings { AccentColor = "blue" }, service);
            var liveSurface = new Border();
            liveSurface.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            var otherWindow = new Window { Content = liveSurface };
            try
            {
                var choices = (WrapPanel)window.FindName("AccentColorChoices");
                var save = (Button)window.FindName("SaveButton");
                var navLabel = (TextBlock)window.FindName("NavAppearanceText");
                var toggle = (CheckBox)window.FindName("DynamicIslandModeCheck");
                toggle.IsChecked = true;
                toggle.ApplyTemplate();
                var toggleTrack = (Border)toggle.Template.FindName("SwitchOnTrack", toggle);
                var toggleThumb = (Border)toggle.Template.FindName("SwitchThumbCap", toggle);
                NotchSettings? preview = null;
                window.SettingsChanged += (_, settings) => preview = settings;

                Assert.Equal(AccentTheme.Swatches.Count, choices.Children.Count);
                foreach (RadioButton choice in choices.Children)
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(choice)));
                    choice.IsChecked = true;
                    var expected = AccentTheme.GetSwatch((string)choice.Tag);
                    var foreground = AccentTheme.ForegroundColor;
                    Assert.Equal(expected.Id, window.ReadSettingsFromUi().AccentColor);
                    Assert.Equal(expected.Color, ((SolidColorBrush)save.Background).Color);
                    Assert.Equal(expected.Color, ((SolidColorBrush)liveSurface.Background).Color);
                    Assert.Equal(foreground, ((SolidColorBrush)navLabel.Foreground).Color);
                    Assert.Equal(expected.Color, ((SolidColorBrush)((Border)window.FindName("NavActiveIndicator")).Background).Color);
                    Assert.Equal(foreground, ((SolidColorBrush)save.Foreground).Color);
                    Assert.Equal(expected.Color, ((SolidColorBrush)toggleTrack.Background).Color);
                    Assert.Equal(Colors.White, ((SolidColorBrush)toggleThumb.Background).Color);
                    Assert.True(choice.IsChecked);
                }

                Assert.Equal("teal", preview!.AccentColor);
                Assert.Null(service.Applied);
                typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, null);
                Assert.Equal(AccentTheme.GetSwatch("blue").Color, ((SolidColorBrush)save.Background).Color);
                Assert.Equal("blue", preview.AccentColor);
                Assert.Null(service.Applied);
            }
            finally { otherWindow.Close(); window.Close(); AccentTheme.Apply("default"); }
        });

    [Fact]
    public void ApplyCommitsColorAndReopeningKeepsTheSelection() =>
        SharedStaTestRunner.Run(() =>
        {
            BackgroundTestWindows.EnsureApplicationResources();
            var service = new RecordingService();
            var window = new SettingsWindow(new NotchSettings(), service);
            try
            {
                var choices = (WrapPanel)window.FindName("AccentColorChoices");
                choices.Children.OfType<RadioButton>().Single(choice => (string)choice.Tag == "purple").IsChecked = true;
                Assert.True(window.ApplyWithoutClosingAsync().GetAwaiter().GetResult());
                Assert.Equal("purple", service.Applied!.AccentColor);
            }
            finally { window.Close(); }

            var reopened = new SettingsWindow(service.Applied!, service);
            try
            {
                var selected = ((WrapPanel)reopened.FindName("AccentColorChoices")).Children.OfType<RadioButton>()
                    .Single(choice => choice.IsChecked == true);
                Assert.Equal("purple", selected.Tag);
                Assert.Equal("purple", reopened.ReadSettingsFromUi().AccentColor);
                var defaultPreview = reopened.ReadSettingsFromUi();
                defaultPreview.AccentColor = "default";
                reopened.ApplyPreview(defaultPreview);
            }
            finally { reopened.Close(); AccentTheme.Apply("default"); }
        });

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-color")]
    public void InvalidImportedColorsUseTheDefaultSwatch(string? id) =>
        SharedStaTestRunner.Run(() =>
        {
            BackgroundTestWindows.EnsureApplicationResources();
            var window = new SettingsWindow(new NotchSettings { AccentColor = id! }, new RecordingService());
            try
            {
                Assert.Equal("default", window.ReadSettingsFromUi().AccentColor);
                var choices = (WrapPanel)window.FindName("AccentColorChoices");
                Assert.Equal("default", choices.Children.OfType<RadioButton>().Single(choice => choice.IsChecked == true).Tag);
            }
            finally { window.Close(); AccentTheme.Apply("default"); }
        });

    [Fact]
    public async Task ColorSurvivesDiskAndExportImportAndLegacySettingsKeepTheDefault()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNotch_Accent_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var service = new SettingsService(System.IO.Path.Combine(directory, "settings.json"));
            var settings = new NotchSettings { AccentColor = "green", SettingsVersion = SettingsMigrator.CurrentVersion };
            await service.SaveAsync(settings);
            Assert.Equal("green", service.Load().AccentColor);
            var (imported, requiresRestart) = SettingsService.ImportSettingsFromString(service.ExportSettingsToString(settings), settings);
            Assert.Equal("green", imported.AccentColor);
            Assert.False(requiresRestart);
            Assert.Equal("default", JsonSerializer.Deserialize<NotchSettings>("{}")!.AccentColor);
            Assert.False(settings.ValueEquals(new NotchSettings { AccentColor = "blue" }));
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    private sealed class RecordingService : ISettingsApplicationService
    {
        public NotchSettings? Applied { get; private set; }
        public NotchSettings Load() => Applied?.Clone() ?? new();
        public Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            Applied = settings.Clone();
            SettingsApplied?.Invoke(this, Applied);
            return Task.CompletedTask;
        }
        public Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(string filePath, NotchSettings? currentSettings = null, CancellationToken ct = default) =>
            Task.FromResult((new NotchSettings(), false));
        public void Export(string filePath, NotchSettings settings) { }
        public bool IsAutoStartEnabled() => false;
        public event EventHandler<NotchSettings>? SettingsApplied;
    }
}
