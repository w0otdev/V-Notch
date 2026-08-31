using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using VNotch;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassClipTests
{
    [Fact]
    public void MaterialEffects_AreInsideEffectFreeFinalClipHost()
    {
        string repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Windows", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement clipHost = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "GlassMaterialClipHost");

        Assert.Equal("GlassMaterialClipHost_SizeChanged", (string?)clipHost.Attribute("SizeChanged"));
        Assert.Null(clipHost.Attribute("Effect"));
        Assert.DoesNotContain(clipHost.Elements(), element => element.Name == presentation + "Grid.Effect");

        string[] materialLayerNames =
        {
            "GlassBackdropHost",
            "GlassGrainOverlay",
            "GlassTintOverlay",
            "GlassDepthRimBorder",
            "GlassCoolRimBorder",
            "GlassWarmRimBorder",
            "GlassFresnelBloomBorder",
            "GlassFresnelBorder",
            "GlassInnerFresnelBorder",
            "GlassDarkOverlay",
            "GlassRimBorder",
            "GlassSpecularBorder"
        };

        foreach (string layerName in materialLayerNames)
        {
            Assert.Contains(clipHost.Descendants(),
                element => (string?)element.Attribute(xaml + "Name") == layerName);
        }

        XElement volumeIndicator = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "VolumeIndicatorContainer");
        Assert.DoesNotContain(clipHost, volumeIndicator.Ancestors());
    }

    [Fact]
    public void EffectFreeAncestorClip_BlocksPostBlurPixelsInRoundedCorners()
    {
        Exception? failure = null;
        byte leakedCornerAlpha = 0;
        byte clippedCornerAlpha = 0;
        byte clippedCenterAlpha = 0;

        var thread = new Thread(() =>
        {
            try
            {
                leakedCornerAlpha = RenderBlurredPill(useFinalClip: false).CornerAlpha;
                (clippedCornerAlpha, clippedCenterAlpha) = RenderBlurredPill(useFinalClip: true);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        Assert.True(leakedCornerAlpha > 0);
        Assert.Equal(0, clippedCornerAlpha);
        Assert.True(clippedCenterAlpha > 240);
    }

    [Fact]
    public void RoundedNotchClip_PreservesFlatTopAndClipsRoundedBottomCorners()
    {
        StreamGeometry geometry = Assert.IsType<StreamGeometry>(
            MainWindow.BuildRoundedNotchClipGeometry(
                w: 100,
                h: 40,
                rTop: 0,
                rBottom: 20));

        Assert.True(geometry.FillContains(new Point(1, 1)));
        Assert.False(geometry.FillContains(new Point(1, 39)));
        Assert.True(geometry.FillContains(new Point(50, 39)));
    }

    private static (byte CornerAlpha, byte CenterAlpha) RenderBlurredPill(bool useFinalClip)
    {
        const int width = 100;
        const int height = 40;
        const double radius = height / 2.0;

        var root = new Grid
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent
        };
        var clipHost = new Grid
        {
            Width = width,
            Height = height
        };
        if (useFinalClip)
        {
            clipHost.Clip = new RectangleGeometry(
                new Rect(0, 0, width, height), radius, radius);
        }

        clipHost.Children.Add(new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(radius),
            Effect = new BlurEffect
            {
                Radius = 10,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            }
        });
        root.Children.Add(clipHost);

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        byte cornerAlpha = pixels[3];
        int centerOffset = ((height / 2) * stride) + ((width / 2) * 4);
        byte centerAlpha = pixels[centerOffset + 3];
        return (cornerAlpha, centerAlpha);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "V-Notch.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the V-Notch repository root.");
    }
}
