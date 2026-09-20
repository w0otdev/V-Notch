using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Xunit;

namespace VNotch.Tests;

public sealed class LyricsMultilineLayoutTests
{
    [Fact]
    public void AutoRowHostDoesNotResizeWhenOldMultilineContentIsCleared()
    {
        SharedStaTestRunner.Run(() =>
        {
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var source = XDocument.Load(SourcePath());
            var widgetXaml = source.Descendants(ui + "Border").Single(e => (string?)e.Attribute(x + "Name") == "LyricsWidget");
            var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var layer = new Grid();
            var widget = new Border { Padding = new Thickness(4, 2, 7, 2), Margin = new Thickness(0, 0, 10, 2), Child = layer };
            if (widgetXaml.Attribute("Height") is { } height)
                widget.Height = double.Parse(height.Value, System.Globalization.CultureInfo.InvariantCulture);
            Grid.SetRowSpan(widget, 2);
            host.Children.Add(widget);
            var incoming = new TextBlock { Text = "One line", LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, VerticalAlignment = VerticalAlignment.Center };
            var outgoing = new TextBlock { LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, VerticalAlignment = VerticalAlignment.Center };
            layer.Children.Add(incoming);
            layer.Children.Add(outgoing);
            for (int lines = 2; lines <= 5; lines++)
            {
                outgoing.Text = string.Join("\n", Enumerable.Repeat("Previous line", lines));
                Layout();
                var before = incoming.TransformToAncestor(host).Transform(new Point());
                double heightBefore = host.DesiredSize.Height;
                outgoing.Text = "";
                Layout();
                Assert.Equal(heightBefore, host.DesiredSize.Height);
                Assert.Equal(before, incoming.TransformToAncestor(host).Transform(new Point()));
            }
            void Layout()
            {
                host.Measure(new Size(190, 180));
                host.Arrange(new Rect(0, 0, 190, host.DesiredSize.Height));
                host.UpdateLayout();
            }
        });
    }

    [Theory]
    [InlineData("First line\nSecond line")]
    [InlineData("First line\nSecond line\nThird line")]
    [InlineData("First line\nSecond line\nThird line\nFourth line")]
    [InlineData("First line\nSecond line\nThird line\nFourth line\nFifth line")]
    [InlineData("Một câu phụ đề dài cần tự xuống dòng khi không đủ chiều rộng hiển thị.")]
    public void MultilineBoundsStayFixedWhenEntranceClockIsRemoved(string text)
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = XDocument.Load(SourcePath());
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var resources = source.Descendants(ui + "Window.Resources").Single();
            var host = new XElement(ui + "Grid",
                new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(ui + "Grid.Resources",
                    resources.Elements().Where(e => (string?)e.Attribute(x + "Key") is "MainSystemFont" or "SubtitleText")));
            foreach (string name in new[] { "LyricTextA", "LyricTextB" })
                host.Add(new XElement(source.Descendants(ui + "TextBlock").Single(e => (string?)e.Attribute(x + "Name") == name)));
            var grid = (Grid)XamlReader.Parse(host.ToString());
            foreach (double width in new[] { 130d, 190d })
                foreach (TextBlock block in grid.Children)
                {
                    block.Text = "Short";
                    Layout();
                    block.Opacity = 0;
                    block.Text = text;
                    Layout();
                    Assert.True(block.ActualHeight >= 34);
                    var transform = (TranslateTransform)block.RenderTransform;
                    transform.Y = 0;
                    var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(450));
                    var clock = (AnimationClock)slide.CreateClock(true);
                    transform.ApplyAnimationClock(TranslateTransform.YProperty, clock);
                    clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(450), TimeSeekOrigin.BeginTime);
                    block.Opacity = 1;
                    Layout();
                    var animated = block.TransformToAncestor(grid).TransformBounds(new Rect(block.RenderSize));
                    var animatedPixels = RenderPixels();
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    Layout();
                    var settled = block.TransformToAncestor(grid).TransformBounds(new Rect(block.RenderSize));
                    Assert.Equal(animated, settled);
                    Assert.Equal(animatedPixels, RenderPixels());

                    byte[] RenderPixels()
                    {
                        var bitmap = new RenderTargetBitmap((int)width * 2, 280, 192, 192, PixelFormats.Pbgra32);
                        bitmap.Render(grid);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        return pixels;
                    }

                    void Layout()
                    {
                        grid.Measure(new Size(width, 140));
                        grid.Arrange(new Rect(0, 0, width, 140));
                        grid.UpdateLayout();
                    }
                }
        });
    }

    private static string SourcePath([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../Windows/MainWindow.xaml"));
}
