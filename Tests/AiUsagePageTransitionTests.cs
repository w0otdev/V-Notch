using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiUsagePageTransitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainAndAiSubscriptionsAnimateAndSettleInBothDirections(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        bool? previousSystemMotion = AnimationConfig.ClientAreaAnimationOverride;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
            }, services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow)
            .GetField("_transitionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(true);
            window.Show();
            window.NavigateToNotchView(NotchView.Media);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && coordinator.CurrentView == NotchView.Media,
                "main view ready", ct);
            AnimationConfig.SetReduceMotion(false);
            await WpfFrameWaiter.NextAsync(ct);
            Capture(window, island, "main");

            foreach (var target in new[] { NotchView.AiUsage, NotchView.Media })
            {
                var incoming = target == NotchView.AiUsage ? window.AiUsageContent : window.ExpandedContent;
                var outgoing = target == NotchView.AiUsage ? window.ExpandedContent : window.AiUsageContent;
                window.NavigateToNotchView(target);
                Assert.True(coordinator.IsTransitionActive);
                await WpfFrameWaiter.UntilAsync(() => incoming.Visibility == Visibility.Visible,
                    "incoming view shown by the transition dispatcher", ct);
                Assert.Equal(Visibility.Visible, incoming.Visibility);
                Assert.False(incoming.IsHitTestVisible);
                Assert.InRange(incoming.RenderTransform.Value.M11, 0.969, 0.999);
                Assert.Equal(Visibility.Visible, outgoing.Visibility);
                var elapsed = Stopwatch.StartNew();
                await WpfFrameWaiter.UntilAsync(() => elapsed.ElapsedMilliseconds >= 100, "transition frame", ct);
                Capture(window, island, $"{target}-transition");
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "view settled", ct);
                Assert.Equal(target, coordinator.CurrentView);
                Assert.Equal(1, incoming.Opacity);
                Assert.Equal(1, incoming.RenderTransform?.Value.M11 ?? 1);
                Assert.True(incoming.IsHitTestVisible);
                Assert.Equal(Visibility.Collapsed, outgoing.Visibility);
                Assert.True(window.NotchBorder.ActualHeight >= incoming.ActualHeight);
                Capture(window, island, $"{target}-settled");
            }
        }
        finally
        {
            AnimationConfig.ClientAreaAnimationOverride = previousSystemMotion;
            AnimationConfig.SetReduceMotion(previousMotion);
        }
    });

    private static void Capture(MainWindow window, bool island, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var surface = window.NotchBorder;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
            (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"ai-page-{(island ? "island" : "notch")}-{name}.png"));
        encoder.Save(stream);
    }
}
