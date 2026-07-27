using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public class MarqueeControllerTests
{
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
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            try
            {
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

        private readonly TranslateTransform _titleMarqueeA = new();
        private readonly TranslateTransform _titleMorphA = new();
        private readonly TranslateTransform _titleMarqueeB = new();
        private readonly TranslateTransform _titleMorphB = new();
        private readonly TranslateTransform _artistMarqueeA = new();
        private readonly TranslateTransform _artistMorphA = new();
        private readonly TranslateTransform _artistMarqueeB = new();
        private readonly TranslateTransform _artistMorphB = new();
        private readonly TextBlock _compactTitle = new();
        private readonly TranslateTransform _compactTitleTranslate = new();

        public MarqueeTestTargets(string title, string artist)
        {
            TitleA = new TextBlock { Text = title };
            ArtistA = new TextBlock { Text = artist };
        }

        public MarqueeController CreateController() =>
            new(
                TitleA, _titleMarqueeA, _titleMorphA,
                TitleB, _titleMarqueeB, _titleMorphB,
                ArtistA, _artistMarqueeA, _artistMorphA,
                ArtistB, _artistMarqueeB, _artistMorphB,
                _compactTitle, _compactTitleTranslate,
                width => width);
    }
}
