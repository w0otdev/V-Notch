using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationPreviewTests
{
    [Fact]
    public void CaptureNotificationBlurFade() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("vi", greeting: false,
            configureSettings: settings =>
            {
                settings.EnableLocalOnlyMode = true; settings.ShowSystemNotifications = true;
                settings.AutoDismissNotifications = false; settings.EnableBlurEffects = true;
                settings.EnableDynamicIslandMode = false; settings.Width = 230; settings.Height = 34;
            }, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var app = global::Windows.ApplicationModel.AppInfo.GetFromAppUserModelId("OpenAI.Codex_2p2nqsd0c76g0!App");
        var logo = await AppNotificationService.TryLoadAppLogoAsync(app.DisplayInfo);
        var gif = new GifBitmapEncoder();
        long last = Environment.TickCount64;
        async Task Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Delay(25, ct);
                await WpfFrameWaiter.NextAsync(ct);
                var capture = Capture(window);
                long now = Environment.TickCount64;
                var metadata = new BitmapMetadata("gif");
                metadata.SetQuery("/grctlext/Delay", (ushort)Math.Clamp((now - last + 5) / 10, 1, 65535));
                metadata.SetQuery("/grctlext/Disposal", (byte)2);
                gif.Frames.Add(BitmapFrame.Create(capture, null, metadata, null)); last = now;
                if (gif.Frames.Count is 9 or 11 or 16 or 46 or 58 or 61)
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture));
                    using var output = File.Create(Path.Combine(Path.GetTempPath(), $"notification-blur-{gif.Frames.Count}.png"));
                    png.Save(output);
                }
            }
        }
        await Frames(6);
        window.ShowAppNotification(new AppNotificationInfo
        {
            AppName = "ChatGPT", Title = "Cải thiện ảnh thông báo",
            Body = "Đặng Thị Quỳnh đã gửi kết quả.\nỨng dụng đang phản hồi gypq.", AppIcon = logo
        });
        await Frames(32);
        window.AnimateAppNotificationHover(true); await Frames(12);
        window.BeginAppNotificationInteraction(new Point(0, 0), Environment.TickCount64); await Frames(7);
        window.EndAppNotificationInteraction(new Point(0, 0), Environment.TickCount64); await Frames(9);
        window.AnimateAppNotificationHover(false); await Frames(6);
        window.DismissAppNotificationBanner(); await Frames(24);
        using var file = File.Create(Path.Combine(Path.GetTempPath(), "notification-polish.gif")); gif.Save(file);
    }, timeoutSeconds: 30);

    private static RenderTargetBitmap Capture(MainWindow window)
    {
        double w = double.IsFinite(window.NotchBorder.Width) ? window.NotchBorder.Width : 230;
        double h = double.IsFinite(window.NotchBorder.Height) ? window.NotchBorder.Height : 34;
        var size = new Size(Math.Max(1, w), Math.Max(1, h));
        window.NotchBorder.Measure(size); window.NotchBorder.Arrange(new Rect(size)); window.NotchBorder.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 33, 36)), null, new Rect(0, 0, 560, 180));
            double x = (560 - w) / 2;
            var rect = new Rect(x, 0, w, h);
            double radius = Math.Min(h / 2, window.NotchBorder.CornerRadius.BottomLeft);
            dc.PushClip(Geometry.Combine(new RectangleGeometry(rect, radius, radius),
                new RectangleGeometry(new Rect(x, 0, w, Math.Max(1, radius))), GeometryCombineMode.Union, null));
            dc.DrawRectangle(Brushes.Black, null, rect);
            dc.DrawRectangle(new VisualBrush(window.NotchBorder)
                { Viewbox = new Rect(size), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, rect);
            dc.Pop();
        }
        var image = new RenderTargetBitmap(560, 180, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
}
