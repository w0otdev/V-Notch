using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsTreePanelTests
{
    [Fact]
    public void BranchTargetsTheActualSliderLabelInsidePaddedGridRows() => SharedStaTestRunner.Run(() =>
    {
        var host = new StackPanel { Width = 320, Height = 180, Background = Brushes.Black };
        var tree = new SettingsTreePanel();
        var slider = new ElasticSlider
        {
            Label = "Text reveal speed",
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:controls="clr-namespace:VNotch.Controls;assembly=V-Notch"
                    TargetType="{x:Type controls:ElasticSlider}">
                    <StackPanel>
                        <TextBlock x:Name="PART_Label" Height="26" Margin="0,24,0,0"/>
                        <Border Height="36"/>
                    </StackPanel>
                </ControlTemplate>
                """)
        };
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.Children.Add(slider);
        tree.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 0), Child = grid });
        host.Children.Add(tree);
        host.Measure(new Size(320, 180));
        host.Arrange(new Rect(0, 0, 320, 180));
        host.UpdateLayout();

        var label = Assert.IsType<TextBlock>(slider.Template.FindName("PART_Label", slider));
        int center = (int)Math.Round(label.TranslatePoint(new Point(0, label.ActualHeight / 2), host).Y);
        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(20, center, 1, 1), pixel, 4, 0);
        Assert.True(pixel[0] >= 96, $"Connector should meet the label center clearly; brightness was {pixel[0]}.");
        bitmap.CopyPixels(new Int32Rect(20, center - 6, 1, 1), pixel, 4, 0);
        Assert.Equal(0, pixel[0]);
    });

    [Fact]
    public void HidingTheLastChildShortensTheConnectorAndKeepsTheRightEdge() => SharedStaTestRunner.Run(() =>
    {
        var host = new StackPanel { Width = 320, Height = 180, Background = Brushes.Black };
        host.Children.Add(new Border { Height = 40 });
        var tree = new SettingsTreePanel();
        var first = new Border { Height = 40 };
        var last = new Border { Height = 40 };
        tree.Children.Add(first);
        tree.Children.Add(last);
        host.Children.Add(tree);

        Layout();
        int top = (int)tree.TranslatePoint(new Point(), host).Y;
        Assert.Equal(28, tree.TranslatePoint(new Point(), host).X);
        Assert.Equal(320, first.TranslatePoint(new Point(first.ActualWidth, 0), host).X);
        Assert.True(ConnectorPixel(8, top + 30) > 0);

        last.Visibility = Visibility.Collapsed;
        Layout();
        Assert.Equal(0, ConnectorPixel(8, top + 30));
        Assert.True(ConnectorPixel(8, top + 1) > 0);

        first.Visibility = Visibility.Collapsed;
        Layout();
        Assert.Equal(0, ConnectorPixel(8, top + 1));

        void Layout()
        {
            host.Measure(new Size(320, 180));
            host.Arrange(new Rect(0, 0, 320, 180));
            host.UpdateLayout();
        }

        byte ConnectorPixel(int x, int y)
        {
            var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel[0];
        }
    });
}
