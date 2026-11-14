using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class NotchSoftShadowTests
{
    private const int Width = 360, Height = 160;
    private const double ShapeLeft = 80, ShapeWidth = 200, ShapeHeight = 60, Radius = 18;

    [Fact]
    public void PaintedShadowLooksLikeTheBlurredSilhouetteItReplaces() => SharedStaTestRunner.Run(() =>
    {
        byte[] blurred = Render(painted: false);
        byte[] painted = Render(painted: true);

        int centre = (int)(ShapeLeft + ShapeWidth / 2);
        int below = (int)ShapeHeight + 1;
        int side = (int)ShapeLeft - 1;
        for (int d = 0; d < 24; d++)
        {
            // Under the middle and beside the flank the gradients follow the blur closely.
            Assert.InRange(Luma(painted, centre, below + d) - Luma(blurred, centre, below + d), -3, 3);
            Assert.InRange(Luma(painted, side - d, (int)(ShapeHeight / 2)) - Luma(blurred, side - d, (int)(ShapeHeight / 2)), -3, 3);
            // Out of a rounded corner a blur thins a little more than the radial gradient does (up to ~5 %).
            int x = (int)(ShapeLeft + Radius * 0.3) - d / 2, y = (int)(ShapeHeight - Radius * 0.3) + d / 2;
            Assert.InRange(Luma(painted, x, y) - Luma(blurred, x, y), -15, 3);
        }
        Assert.True(Luma(painted, centre, below) < 200, "the shadow is visible right under the notch");
        Assert.True(Luma(painted, centre, below + 30) > 250, "and gone a little further down");
    });

    [Fact]
    public void FalloffStartsUnderTheNotchAndFadesOut()
    {
        Assert.Equal(NotchSoftShadow.Strength / 2, NotchSoftShadow.AlphaAt(0), 6);
        Assert.InRange(NotchSoftShadow.AlphaAt(-NotchSoftShadow.Depth), 0.42, 0.45);
        Assert.InRange(NotchSoftShadow.AlphaAt(NotchSoftShadow.BlurRadius), 0, 0.002);
        for (double d = -10; d < 20; d += 0.5)
            Assert.True(NotchSoftShadow.AlphaAt(d + 0.5) < NotchSoftShadow.AlphaAt(d));
    }

    private static byte[] Render(bool painted)
    {
        var silhouette = new Grid { Width = ShapeWidth, Height = ShapeHeight };
        if (painted)
            silhouette.Children.Add(new NotchSoftShadow { CornerRadius = new CornerRadius(0, 0, Radius, Radius) });
        else
            silhouette.Effect = new DropShadowEffect
            {
                Color = Colors.Black, ShadowDepth = NotchSoftShadow.Depth, BlurRadius = NotchSoftShadow.BlurRadius,
                Opacity = NotchSoftShadow.Strength, Direction = 270, RenderingBias = RenderingBias.Quality,
            };
        silhouette.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(0, 0, Radius, Radius) });

        var root = new Canvas { Width = Width, Height = Height, Background = Brushes.White };
        Canvas.SetLeft(silhouette, ShapeLeft);
        root.Children.Add(silhouette);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static int Luma(byte[] pixels, int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
    }
}
