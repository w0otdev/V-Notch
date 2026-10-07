using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class TrayMotionTests
{
    [Fact]
    public void BrushFeedbackPreservesBindingAndDoesNotMutateSharedBrushes() => SharedStaTestRunner.Run(() =>
    {
        var source = new Border { Background = Brushes.Red };
        var surface = new Border();
        BindingOperations.SetBinding(surface, TrayMotion.BackgroundProperty,
            new Binding(nameof(Border.Background)) { Source = source });
        Assert.Equal(Colors.Red, ((SolidColorBrush)surface.Background).Color);
        source.Background = Brushes.Blue;
        Assert.Equal(Colors.Blue, ((SolidColorBrush)surface.Background).Color);
        source.Background = Brushes.Red;
        Assert.Equal(Colors.Red, ((SolidColorBrush)surface.Background).Color);
        Assert.True(BindingOperations.IsDataBound(surface, TrayMotion.BackgroundProperty));
        Assert.True(Brushes.Red.IsFrozen);
    });

    [Fact]
    public void CopyFeedbackKeepsItsBindingAcrossRepeatedChanges() => SharedStaTestRunner.Run(() =>
    {
        var source = new CheckBox();
        var surface = new Border { IsHitTestVisible = false };
        BindingOperations.SetBinding(surface, TrayMotion.OpacityProperty,
            new Binding(nameof(CheckBox.IsChecked)) { Source = source, Converter = new TrayCopiedOpacityConverter() });
        Assert.Equal(0, surface.Opacity);
        source.IsChecked = true;
        Assert.Equal(1, surface.Opacity);
        source.IsChecked = false;
        Assert.Equal(0, surface.Opacity);
        Assert.True(BindingOperations.IsDataBound(surface, TrayMotion.OpacityProperty));
        Assert.False(surface.IsHitTestVisible);
    });

    [Fact]
    public void CopyFeedbackKeepsItsBlurBindingAcrossRepeatedChanges() => SharedStaTestRunner.Run(() =>
    {
        var source = new CheckBox();
        var surface = new Border();
        BindingOperations.SetBinding(surface, TrayMotion.BlurRadiusProperty,
            new Binding(nameof(CheckBox.IsChecked)) { Source = source, Converter = new TrayCopiedBlurConverter() });
        Assert.Equal(0, TrayMotion.GetBlurRadius(surface));
        source.IsChecked = true;
        Assert.Equal(16, TrayMotion.GetBlurRadius(surface));
        source.IsChecked = false;
        Assert.Equal(0, TrayMotion.GetBlurRadius(surface));
        Assert.True(BindingOperations.IsDataBound(surface, TrayMotion.BlurRadiusProperty));
    });

    [Fact]
    public void BeginEntranceAndExitManageEntranceLifecycleAndStagger() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 800, Height = 400 };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            tray.BeginEntrance();
            var search = (FrameworkElement)tray.FindName("SearchSurface");
            var toolbar = (FrameworkElement)tray.FindName("ToolbarSurface");
            var category = (FrameworkElement)tray.FindName("CategoryScroll");
            var cards = (ListBox)tray.FindName("Cards");

            Assert.NotNull(search);
            Assert.NotNull(toolbar);
            Assert.NotNull(category);
            Assert.NotNull(cards);
            Assert.Equal(1.0, cards.Opacity);

            tray.BeginExit();
            var isLeaving = (bool)typeof(ClipboardTray).GetField("_trayLeaving", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
            var staggerVer = (int)typeof(ClipboardTray).GetField("_entranceStaggerVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
            Assert.True(isLeaving);
            Assert.Equal(0, staggerVer);
        }
        finally
        {
            window.Close();
        }
    });
}
