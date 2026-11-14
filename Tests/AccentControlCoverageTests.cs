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
    public void WhiteNoiseChipsAndVolumeFollowAccentWhileAChipIsAlreadyActive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        int previousKind = WhiteNoiseService.PlayingKind;
        WhiteNoiseService.SetPlayingKindForTest(0);
        var strip = new WhiteNoiseStrip();
        var window = new Window { Content = strip };
        try
        {
            window.Show();
            await Task.Delay(50, ct);
            window.Measure(new Size(640, 120));
            window.Arrange(new Rect(0, 0, 640, 120));
            window.UpdateLayout();

            static TextBlock GetChipLabel(Grid chipRoot)
            {
                var surface = Assert.IsType<Border>(chipRoot.Children[0]);
                var contentHost = Assert.IsType<Grid>(surface.Child);
                return Assert.IsType<TextBlock>(contentHost.Children[0]);
            }

            var chip0 = Assert.IsType<Grid>(strip.ChipHost.Children[0]);
            var chip1 = Assert.IsType<Grid>(strip.ChipHost.Children[1]);
            var chip2 = Assert.IsType<Grid>(strip.ChipHost.Children[2]);
            double initialX = strip.ChipSelectionOffset.X;
            Assert.Equal(1.0, strip.ChipSelection.Opacity, 3);
            Assert.True(strip.ChipSelection.Width > 0);

            foreach (string id in new[] { "purple", "green", "orange", "default" })
            {
                AccentTheme.Apply(id);
                var swatch = AccentTheme.GetSwatch(id);
                Assert.Equal(swatch.Color, Assert.IsType<SolidColorBrush>(strip.VolumeFill.Background).Color);
                Assert.Equal(swatch.Color, Assert.IsType<SolidColorBrush>(strip.ChipSelection.Background).Color);
                Assert.Equal(AccentTheme.ForegroundColor, Assert.IsType<SolidColorBrush>(GetChipLabel(chip0).Foreground).Color);
                Assert.Equal(swatch.Strong, Assert.IsType<SolidColorBrush>(GetChipLabel(chip1).Foreground).Color);
            }

            WhiteNoiseService.SetPlayingKindForTest(2);
            strip.RefreshForTest();
            await Task.Delay(320, ct);
            Assert.True(strip.ChipSelectionOffset.X > initialX);
            Assert.Equal(AccentTheme.ForegroundColor, Assert.IsType<SolidColorBrush>(GetChipLabel(chip2).Foreground).Color);
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
