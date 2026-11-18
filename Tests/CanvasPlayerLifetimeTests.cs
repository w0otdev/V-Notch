using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class CanvasPlayerLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CompactStartupDefersPlayersAndCanvasCreatesOnlyOnePair() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() =>
            (bool)typeof(MainWindow).GetField("_coreModulesStarted", Private)!.GetValue(window)!,
            "core services start", ct);
        var viewport = window.LyricsCanvasViewport;
        Assert.Empty(viewport.Children.OfType<MediaElement>());
        var property = typeof(MainWindow).GetProperty("SpotifyCanvasController", Private)!;
        var controller = property.GetValue(window);
        Assert.Same(controller, property.GetValue(window));
        var players = viewport.Children.OfType<MediaElement>().ToArray();
        Assert.Equal(2, players.Length);
        Assert.All(players, player =>
        {
            Assert.Equal(MediaState.Manual, player.LoadedBehavior);
            Assert.Equal(MediaState.Manual, player.UnloadedBehavior);
            Assert.True(player.IsMuted);
            Assert.True(player.ScrubbingEnabled);
            Assert.Null(player.Source);
        });
        Assert.Equal(1, players[0].Opacity);
        Assert.Equal(0, players[1].Opacity);
        Assert.True(Panel.GetZIndex(window.LyricsCanvasBrightnessOverlay) > Panel.GetZIndex(players[0]));
        typeof(MainWindow).GetMethod("DisposeSpotifyCanvasLifecycle", Private)!.Invoke(window, null);
        Assert.Empty(viewport.Children.OfType<MediaElement>());
        Assert.Contains(window.LyricsCanvasBrightnessOverlay, viewport.Children.Cast<UIElement>());
        Assert.NotSame(controller, property.GetValue(window));
        Assert.Equal(2, viewport.Children.OfType<MediaElement>().Count());
    });
}
