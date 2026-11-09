using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AccentControlCoverageTests
{
    [Fact]
    public void WhiteNoiseChipsAndVolumeFollowAccentWhileAChipIsAlreadyActive() => SharedStaTestRunner.Run(() =>
    {
        int previousKind = WhiteNoiseService.PlayingKind;
        WhiteNoiseService.SetPlayingKindForTest(0);
        var strip = new WhiteNoiseStrip();
        var window = new Window { Content = strip };
        try
        {
            var chips = strip.ChipHost.Children.OfType<Border>().ToArray();
            foreach (string id in new[] { "purple", "green", "orange", "default" })
            {
                AccentTheme.Apply(id);
                var swatch = AccentTheme.GetSwatch(id);
                Assert.Equal(swatch.Color, Assert.IsType<SolidColorBrush>(strip.VolumeFill.Background).Color);
                Assert.Equal(swatch.Color, Assert.IsType<SolidColorBrush>(chips[0].Background).Color);
                Assert.Equal(AccentTheme.ForegroundColor, Assert.IsType<SolidColorBrush>(((TextBlock)chips[0].Child).Foreground).Color);
                Assert.Equal(swatch.Strong, Assert.IsType<SolidColorBrush>(((TextBlock)chips[1].Child).Foreground).Color);
            }
        }
        finally
        {
            window.Close();
            WhiteNoiseService.SetPlayingKindForTest(previousKind);
            AccentTheme.Apply("default");
        }
    });

    [Fact]
    public void SelectedClipboardTabAndMovingSelectionPillFollowAccent() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var tray = new ClipboardTray();
        var category = new ClipboardCategoryViewModel(TrayIconKind.Files) { IsSelected = true };
        var button = new Button { Style = (Style)tray.FindResource("CategoryButton"), DataContext = category, Content = "Files" };
        var panel = new StackPanel();
        panel.Children.Add(tray);
        panel.Children.Add(button);
        var window = new Window { Content = panel };
        try
        {
            window.Show();
            await Task.Delay(50, ct);
            window.Measure(new Size(1000, 600));
            window.Arrange(new Rect(0, 0, 1000, 600));
            window.UpdateLayout();
            foreach (string id in new[] { "blue", "pink", "green", "default" })
            {
                AccentTheme.Apply(id);
                var swatch = AccentTheme.GetSwatch(id);
                Assert.Equal(AccentTheme.ForegroundColor, Assert.IsType<SolidColorBrush>(button.Foreground).Color);
                var tint = Assert.IsType<SolidColorBrush>(tray.CategorySelection.Background).Color;
                Assert.Equal(swatch.Color, tint);
            }
        }
        finally { window.Close(); AccentTheme.Apply("default"); }
    });
}
