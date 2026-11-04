using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class KaraokeLyricsPresenterTests
{
    [Fact]
    public void WordsFollowPauseGapsBackwardSeeksAndPlainText() => SharedStaTestRunner.Run(() =>
    {
        var block = new TextBlock { Foreground = Brushes.White };
        var presenter = new KaraokeLyricsPresenter();
        presenter.SetLine(block, new(TimeSpan.FromSeconds(1), "Đi qua", new[]
        {
            new LyricWord(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 0, 2),
            new LyricWord(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), 3, 3)
        }));
        var runs = block.Inlines.OfType<Run>().ToArray();
        Assert.Equal("Đi qua", block.Text);
        presenter.Update(TimeSpan.FromSeconds(1.5), false);
        var gradient = Assert.IsType<LinearGradientBrush>(runs[0].Foreground);
        Assert.Equal(0.36, gradient.GradientStops[0].Offset, 5);
        presenter.Update(TimeSpan.FromSeconds(2.5), false);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(runs[0].Foreground).Color.A);
        Assert.Equal(100, Assert.IsType<SolidColorBrush>(runs[2].Foreground).Color.A);
        presenter.Update(TimeSpan.FromSeconds(3.5), false);
        Assert.IsType<LinearGradientBrush>(runs[2].Foreground);
        presenter.Update(TimeSpan.FromSeconds(0.5), false);
        Assert.All(runs, run => Assert.Equal(100, Assert.IsType<SolidColorBrush>(run.Foreground).Color.A));
        presenter.SetLine(block, new(TimeSpan.Zero, "Plain lyrics"));
        presenter.Update(TimeSpan.FromSeconds(3), true);
        Assert.Equal("Plain lyrics", block.Text);
    });

    [Fact]
    public void ASteadyPlaybackTickDoesNotAllocate() => SharedStaTestRunner.Run(() =>
    {
        var presenter = new KaraokeLyricsPresenter();
        presenter.SetLine(new TextBlock(), new(TimeSpan.Zero, "Word", new[] { new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(4), 0, 4) }));
        presenter.Update(TimeSpan.FromSeconds(1), false);
        for (int i = 0; i < 100; i++) presenter.Update(TimeSpan.FromSeconds(1), false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) presenter.Update(TimeSpan.FromSeconds(1), false);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        presenter.Clear();
    });

    [Fact]
    public void ChangingLinesNeverMovesTheOutgoingWordsHighlight() => SharedStaTestRunner.Run(() =>
    {
        var presenter = new KaraokeLyricsPresenter();
        var outgoing = new TextBlock();
        var incoming = new TextBlock();
        var line = new LyricLine(TimeSpan.Zero, "Word", new[] { new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 4) });
        presenter.SetLine(outgoing, line);
        presenter.Update(TimeSpan.FromSeconds(0.5), false);
        var oldBrush = Assert.IsType<LinearGradientBrush>(outgoing.Inlines.FirstInline.Foreground);
        double oldOffset = oldBrush.GradientStops[0].Offset;
        presenter.SetLine(incoming, line);
        presenter.Update(TimeSpan.FromSeconds(0.8), false);
        Assert.NotSame(oldBrush, incoming.Inlines.FirstInline.Foreground);
        Assert.Equal(oldOffset, oldBrush.GradientStops[0].Offset);
        presenter.Update(TimeSpan.FromSeconds(0.75), false);
        Assert.Equal(0.68, Assert.IsType<LinearGradientBrush>(incoming.Inlines.FirstInline.Foreground).GradientStops[0].Offset, 5);
        presenter.Clear();
    });

    [Fact]
    public void ShortSyllablesAnimateWithoutAnotherPlaybackTick() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var presenter = new KaraokeLyricsPresenter();
        var block = new TextBlock();
        presenter.SetLine(block, new(TimeSpan.Zero, "Đi qua", new[]
        {
            new LyricWord(TimeSpan.Zero, TimeSpan.FromMilliseconds(60), 0, 2),
            new LyricWord(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(140), 3, 3)
        }));
        presenter.Update(TimeSpan.Zero, true);
        var second = Assert.IsType<LinearGradientBrush>(block.Inlines.LastInline.Foreground);
        await WpfFrameWaiter.UntilAsync(() => second.GradientStops[0].Offset >= 1, "native syllable reveal without a UI tick", ct);
        presenter.Suspend();
        Assert.False(second.GradientStops[0].HasAnimatedProperties);
        presenter.Clear();
    });

    [Fact]
    public void AWordRevealsFromLeftToRightAfterWrapping() => SharedStaTestRunner.Run(() =>
    {
        var block = new TextBlock
        {
            Width = 190,
            FontSize = 32,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Foreground = Brushes.White
        };
        var presenter = new KaraokeLyricsPresenter();
        presenter.SetLine(block, new(TimeSpan.Zero, "Above\nMMMMMMMM", new[]
        {
            new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 5),
            new LyricWord(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 6, 8)
        }));
        presenter.Update(TimeSpan.FromSeconds(1.5), false);
        block.Measure(new System.Windows.Size(190, 160));
        block.Arrange(new System.Windows.Rect(0, 0, 190, block.DesiredSize.Height));
        block.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(190, (int)block.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(block);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        long left = 0, right = 0;
        for (int y = image.PixelHeight / 2; y < image.PixelHeight; y++)
            for (int x = 0; x < 154; x++)
                if (x < 77) left += pixels[(y * image.PixelWidth + x) * 4 + 3];
                else right += pixels[(y * image.PixelWidth + x) * 4 + 3];
        Assert.True(left > right * 1.4, $"The active word should reveal locally after wrapping: left={left}, right={right}");
        presenter.Clear();
    });

    [Fact]
    public void MotionPreferenceChangesRescheduleTheCurrentWord() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        var presenter = new KaraokeLyricsPresenter();
        try
        {
            var block = new TextBlock();
            presenter.SetLine(block, new(TimeSpan.Zero, "Word", new[] { new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(4), 0, 4) }));
            AnimationConfig.SetReduceMotion(true);
            presenter.Update(TimeSpan.FromSeconds(1), true);
            var brush = Assert.IsType<LinearGradientBrush>(block.Inlines.FirstInline.Foreground);
            Assert.False(brush.GradientStops[0].HasAnimatedProperties);
            AnimationConfig.SetReduceMotion(false);
            presenter.Update(TimeSpan.FromSeconds(1), true);
            Assert.True(brush.GradientStops[0].HasAnimatedProperties);
            AnimationConfig.SetReduceMotion(true);
            presenter.Update(TimeSpan.FromSeconds(1), true);
            Assert.False(brush.GradientStops[0].HasAnimatedProperties);
        }
        finally { presenter.Clear(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SmallClockCorrectionsCannotAccumulateIntoDrift() => SharedStaTestRunner.Run(() =>
    {
        var presenter = new KaraokeLyricsPresenter();
        var block = new TextBlock();
        presenter.SetLine(block, new(TimeSpan.Zero, "Word", new[] { new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 4) }));
        presenter.Update(TimeSpan.Zero, true);
        var brush = Assert.IsType<LinearGradientBrush>(block.Inlines.FirstInline.Foreground);
        presenter.Update(TimeSpan.FromMilliseconds(50), true);
        presenter.Update(TimeSpan.FromMilliseconds(100), true);
        presenter.Update(TimeSpan.FromMilliseconds(150), true);
        Assert.True((double)brush.GradientStops[0].GetAnimationBaseValue(GradientStop.OffsetProperty) > -0.2);
        presenter.Clear();
    });
}
