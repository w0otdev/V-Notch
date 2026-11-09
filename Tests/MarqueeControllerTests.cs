using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public class MarqueeControllerTests
{
    [Theory]
    [InlineData(-12)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(14)]
    public void RefreshOnlyScrollsTitlesThatOverflowTheirViewport(double spareWidth)
    {
        RunOnStaThread(() =>
        {
            var targets = new MarqueeTestTargets("Đình - Có Thực Đến Mùa Cây", "Artist");
            targets.TitleA.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double viewportWidth = targets.TitleA.DesiredSize.Width + spareWidth;
            using var controller = targets.CreateController(viewportWidth);
            var row = new Grid { Width = viewportWidth, Height = 35, ClipToBounds = true };
            targets.TitleLayerA.Children.Add(targets.TitleA);
            targets.TitleLayerA.RenderTransform = targets.TitleMarqueeA;
            row.Children.Add(targets.TitleLayerA);
            var host = new Window { Content = row, Width = viewportWidth + 40, Height = 100, ShowInTaskbar = false };
            try
            {
                host.Show();
                controller.RefreshMediaMarquee();
                PumpDispatcherFor(TimeSpan.FromMilliseconds(1500));

                if (spareWidth >= 0) Assert.Equal(0, targets.TitleMarqueeA.X);
                else Assert.True(targets.TitleMarqueeA.X < 0, "A title wider than the viewport must still scroll.");
            }
            finally { host.Close(); }
        });
    }

    [Fact]
    public void PlaceholderThenRealTitleImmediately_DoesNotDropRealTitle()
    {
        RunOnStaThread(() =>
        {
            var targets = new MarqueeTestTargets("No media playing", "Artist name");
            var controller = targets.CreateController();

            controller.UpdateTitleText("No media playing");
            controller.UpdateTitleText("Real track title");

            Assert.Equal("No media playing", targets.TitleA.Text);
            Assert.Equal("Real track title", targets.TitleB.Text);
        });
    }

    [Fact]
    public void RapidUpdates_AfterMorphWindow_LatestTitleAndArtistWin()
    {
        RunOnStaThread(() =>
        {
            var targets = new MarqueeTestTargets("Initial title", "Initial artist");
            var controller = targets.CreateController();

            controller.UpdateTitleText("First title");
            controller.UpdateArtistText("First artist");
            controller.UpdateTitleText("Intermediate title");
            controller.UpdateArtistText("Intermediate artist");
            controller.UpdateTitleText("Final title");
            controller.UpdateArtistText("Final artist");

            PumpDispatcherFor(TimeSpan.FromMilliseconds(500));

            Assert.Equal("Final title", targets.TitleA.Text);
            Assert.Equal("Final artist", targets.ArtistA.Text);
            Assert.NotEqual("Intermediate title", targets.TitleA.Text);
            Assert.NotEqual("Intermediate artist", targets.ArtistA.Text);
        });
    }

    [Fact]
    public void TrackTransition_BypassesPendingMetadataAndMovesBothRowsTogether()
    {
        RunOnStaThread(() =>
        {
            var targets = new MarqueeTestTargets("Initial title", "Initial artist");
            var controller = targets.CreateController();

            controller.TransitionTrackText("First title", "First artist");
            controller.UpdateTitleText("Stale title");
            controller.UpdateArtistText("Stale artist");
            controller.TransitionTrackText("Final title", "Final artist");

            Assert.True(targets.TitleLayerA.HasAnimatedProperties);
            Assert.True(targets.TitleLayerB.HasAnimatedProperties);
            Assert.True(targets.ArtistLayerA.HasAnimatedProperties);
            Assert.True(targets.ArtistLayerB.HasAnimatedProperties);

            PumpDispatcherFor(TimeSpan.FromMilliseconds(500));

            Assert.Equal("Final title", targets.TitleA.Text);
            Assert.Equal("Final artist", targets.ArtistA.Text);
            Assert.NotEqual("Stale title", targets.TitleB.Text);
            Assert.NotEqual("Stale artist", targets.ArtistB.Text);
        });
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher.CurrentDispatcher)
        {
            Interval = duration
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VNotch.TestSupport.BackgroundTestDesktop.AttachCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA test thread did not finish.");
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class MarqueeTestTargets
    {
        public TextBlock TitleA { get; }
        public TextBlock TitleB { get; } = new();
        public TextBlock ArtistA { get; }
        public TextBlock ArtistB { get; } = new();
        public Grid TitleLayerA { get; } = new() { Opacity = 1 };
        public Grid TitleLayerB { get; } = new() { Opacity = 0 };
        public Grid ArtistLayerA { get; } = new() { Opacity = 1 };
        public Grid ArtistLayerB { get; } = new() { Opacity = 0 };

        private readonly TranslateTransform _titleMarqueeA = new();
        public TranslateTransform TitleMarqueeA => _titleMarqueeA;
        private readonly TranslateTransform _titleMorphA = new();
        private readonly TranslateTransform _titleMarqueeB = new();
        private readonly TranslateTransform _titleMorphB = new();
        private readonly TranslateTransform _artistMarqueeA = new();
        private readonly TranslateTransform _artistMorphA = new();
        private readonly TranslateTransform _artistMarqueeB = new();
        private readonly TranslateTransform _artistMorphB = new();

        public MarqueeTestTargets(string title, string artist)
        {
            TitleA = new TextBlock { Text = title };
            ArtistA = new TextBlock { Text = artist };
        }

        public MarqueeController CreateController(double visibleWidth = 340) =>
            new(
                title: new MarqueeTargets(
                    A: new MarqueeLayer(TitleLayerA, TitleA, _titleMarqueeA, _titleMorphA),
                    B: new MarqueeLayer(TitleLayerB, TitleB, _titleMarqueeB, _titleMorphB)),
                artist: new MarqueeTargets(
                    A: new MarqueeLayer(ArtistLayerA, ArtistA, _artistMarqueeA, _artistMorphA),
                    B: new MarqueeLayer(ArtistLayerB, ArtistB, _artistMarqueeB, _artistMorphB)),
                _ => visibleWidth);
    }
}
