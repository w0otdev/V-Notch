using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;

internal static class TranslationSettingsPreview
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VNotch.TestSupport.BackgroundTestDesktop.AttachCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher));
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
                app.Resources["SFProText"] = new FontFamily("Segoe UI");
                app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
                Loc.SetLanguage("vi");
                var window = new VNotch.SettingsWindow(new NotchSettings { Language = "vi", AutoCheckUpdates = false, EnableWeather = false }, new MemorySettings());
                var panel = window.PanelTranslation;
                ((Panel)panel.Parent).Children.Remove(panel);
                panel.Visibility = Visibility.Visible;
                window.TranslationCard.Opacity = 1;
                var surface = new Border { Width = 700, Padding = new Thickness(24), Background = new SolidColorBrush(Color.FromRgb(8, 8, 8)), Child = panel, Resources = window.Resources };
                window.PresentTranslationModelAvailability(true);
                window.TranslationReadyCard.BeginAnimation(UIElement.OpacityProperty, null);
                Save(surface, "translation-ready-preview.png");
                window.PresentTranslationModelAvailability(false);
                window.TranslationModelStatus.Visibility = Visibility.Collapsed;
                window.TranslationProgressCard.Visibility = Visibility.Visible;
                window.TranslationDownloadButton.Content = Loc.Get("translation.cancel");
                window.TranslationImportButton.IsEnabled = window.TranslationRemoveButton.IsEnabled = false;
                window.ShowTranslationDownloadProgress(new(TranslationDownloadStage.Downloading, 321_600_000, 2_497_281_120, 32_700_000));
                Save(surface, "translation-progress-preview.png");
                surface.Child = null;
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private static void Save(FrameworkElement surface, string name)
    {
        surface.Measure(new Size(700, double.PositiveInfinity));
        surface.Arrange(new Rect(new Point(), surface.DesiredSize));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1400, (int)Math.Ceiling(surface.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.GetFullPath(Path.Combine(".artifacts", "translation-benchmark", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        png.Save(file);
        Console.WriteLine(path);
    }

    private sealed class MemorySettings : ISettingsService
    {
        public NotchSettings Load() => new();
        public void Save(NotchSettings settings) { }
        public Task SaveAsync(NotchSettings settings) => Task.CompletedTask;
        public void ExportSettingsToFile(string filePath, NotchSettings settings) => throw new NotSupportedException();
        public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null) => throw new NotSupportedException();
    }
}
