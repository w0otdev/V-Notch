using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Presenters;
using Xunit;

namespace VNotch.Tests;

public sealed class GlassMaterialClipPresenterTests
{
    [Fact]
    public void HiddenMaterialSkipsUpdatesAndSynchronizesWhenRevealed() => SharedStaTestRunner.Run(() =>
    {
        var host = new Grid { Visibility = Visibility.Collapsed };
        var backdrop = new Border();
        var rim = new Border();
        var presenter = new GlassMaterialClipPresenter(host, backdrop, rim);
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Equal(new CornerRadius(), backdrop.CornerRadius);
        Assert.Null(host.Clip);

        host.Visibility = Visibility.Visible;
        host.Measure(new Size(300, 100));
        host.Arrange(new Rect(0, 0, 300, 100));
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Equal(new CornerRadius(35), backdrop.CornerRadius);
        Assert.Equal(backdrop.CornerRadius, rim.CornerRadius);
        var clip = Assert.IsType<StreamGeometry>(host.Clip);
        Assert.Equal(new Rect(0, 0, 300, 100), clip.Bounds);
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Same(clip, host.Clip);

        host.Visibility = Visibility.Collapsed;
        presenter.SyncCornerRadius(new CornerRadius(15));
        Assert.Equal(new CornerRadius(35), backdrop.CornerRadius);
        Assert.Same(clip, host.Clip);

        host.Visibility = Visibility.Visible;
        presenter.SyncCornerRadius(new CornerRadius(15));
        Assert.Equal(new CornerRadius(15), backdrop.CornerRadius);
        Assert.NotSame(clip, host.Clip);
        host.Arrange(new Rect(0, 0, 200, 80));
        presenter.UpdateClip(new CornerRadius(15));
        Assert.Equal(new Rect(0, 0, 200, 80), host.Clip.Bounds);
    });
}
