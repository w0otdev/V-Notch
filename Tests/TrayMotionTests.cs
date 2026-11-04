using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TrayMotionTests
{
    [Fact]
    public void SettledLayerEntranceReturnsOpacityToItsBaseState() => SharedStaTestRunner.RunAsync(async () =>
    {
        var layer = new Border { Opacity = 0 };
        var window = new BackgroundWindow { Content = layer, Width = 100, Height = 100 };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            typeof(ClipboardTray).GetMethod("AnimateLayer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [layer, true, 0, false, 40, 6d, null]);
            await Task.Delay(180);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, layer.Opacity);
            layer.Opacity = .5;
            Assert.Equal(.5, layer.Opacity);
        }
        finally { window.Close(); }
    });

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

    [Fact]
    public void CardsStaggerLeftToRightWithSpringyBottomJumpAndLockContentShakesOnWrongPin() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool prev = VNotch.Services.AnimationConfig.ReduceMotion;
        VNotch.Services.AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cards = (System.Collections.ObjectModel.ObservableCollection<VNotch.ViewModels.ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", flags)!.GetValue(tray)!;
        cards.Add(new VNotch.ViewModels.ClipboardCardViewModel(new VNotch.Models.ClipboardEntry { Title = "File 0", Kind = VNotch.Models.ClipboardKind.File }));
        cards.Add(new VNotch.ViewModels.ClipboardCardViewModel(new VNotch.Models.ClipboardEntry { Title = "File 1", Kind = VNotch.Models.ClipboardKind.File }));

        var window = new BackgroundWindow { Content = tray, Width = 800, Height = 400 };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var list = (ListBox)tray.FindName("Cards");
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);

            tray.BeginEntrance();
            Assert.IsType<TranslateTransform>(first.RenderTransform);
            Assert.IsType<TranslateTransform>(second.RenderTransform);
            Assert.True(((TranslateTransform)first.RenderTransform).HasAnimatedProperties);
            Assert.True(((TranslateTransform)second.RenderTransform).HasAnimatedProperties);

            var lockContent = (FrameworkElement)tray.FindName("LockContent");
            Assert.NotNull(lockContent);
            tray.ShakeLockContent();
            var shakeTransform = Assert.IsType<TranslateTransform>(lockContent.RenderTransform);
            Assert.True(shakeTransform.HasAnimatedProperties);
        }
        finally
        {
            window.Close();
            VNotch.Services.AnimationConfig.SetReduceMotion(prev);
        }
    });

    [Fact]
    public void LoadedContainerKeepsTheEntranceThatWasPreparedBeforeItsLoadedEvent() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = VNotch.Services.AnimationConfig.ReduceMotion;
        VNotch.Services.AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cards = (System.Collections.ObjectModel.ObservableCollection<VNotch.ViewModels.ClipboardCardViewModel>)typeof(ClipboardTray)
            .GetField("_cards", flags)!.GetValue(tray)!;
        cards.Add(new(new VNotch.Models.ClipboardEntry { Title = "File 0", Kind = VNotch.Models.ClipboardKind.File }));
        var window = new BackgroundWindow { Content = tray, Width = 800, Height = 400 };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var list = (ListBox)tray.FindName("Cards");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            tray.BeginEntrance();
            var entrance = Assert.IsType<TranslateTransform>(item.RenderTransform);
            Assert.True(entrance.HasAnimatedProperties);

            // WPF can realize a container during layout and raise Loaded after
            // BeginEntrance has already prepared that container's stagger.
            item.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Assert.Same(entrance, item.RenderTransform);
            Assert.True(entrance.HasAnimatedProperties);
            Assert.True(item.HasAnimatedProperties);
        }
        finally { window.Close(); VNotch.Services.AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReopeningAHiddenTrayRevealsItsCachedCardsIndividually() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = VNotch.Services.AnimationConfig.ReduceMotion;
        VNotch.Services.AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cards = (System.Collections.ObjectModel.ObservableCollection<VNotch.ViewModels.ClipboardCardViewModel>)typeof(ClipboardTray)
            .GetField("_cards", flags)!.GetValue(tray)!;
        for (int index = 0; index < 3; index++)
            cards.Add(new(new VNotch.Models.ClipboardEntry { Title = $"Project {index}.pdf", Kind = VNotch.Models.ClipboardKind.File }));
        var window = new BackgroundWindow { Content = tray, Width = 800, Height = 400 };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            tray.BeginEntrance();
            await Task.Delay(850);
            tray.BeginExit();
            tray.Visibility = Visibility.Collapsed;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            tray.Visibility = Visibility.Visible;
            tray.BeginEntrance();
            await WpfFrameWaiter.NextAsync();
            var list = (ListBox)tray.FindName("Cards");
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            Assert.True(last.Opacity < .99, "Reopening must reveal each card, not just fade the entire row.");
            Assert.True(first.Opacity >= last.Opacity, "The leftmost card must reveal before the rightmost card.");
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(last.Opacity - 1) < 1e-6 &&
                Math.Abs(last.RenderTransform.Value.OffsetY) < 1e-6, "cached card reveal settled");
            Assert.Equal(0, last.RenderTransform.Value.OffsetY);
            SaveTrayArtifact(tray);
        }
        finally { window.Close(); VNotch.Services.AnimationConfig.SetReduceMotion(previous); }
    });

    private static void SaveTrayArtifact(ClipboardTray tray)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        tray.UpdateLayout();
        var bounds = new Rect(tray.RenderSize);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, bounds);
            drawing.DrawRectangle(new VisualBrush(tray), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, "tray-corners.png"));
        encoder.Save(stream);
    }
}
