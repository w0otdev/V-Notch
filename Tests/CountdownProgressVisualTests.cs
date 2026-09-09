using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

public class CountdownProgressVisualTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.01, 0.0)]
    [InlineData(0.02, 0.0)]
    [InlineData(0.021, 1.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    public void ComputeCountdownEdgeOpacity_ReturnsExpectedOpacity(double progress, double expectedOpacity)
    {
        double actual = MainWindow.ComputeCountdownEdgeOpacity(progress);
        Assert.Equal(expectedOpacity, actual);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void ShouldUpdateCountdownVisual_RequiresBothTimerVisibleAndExpanded(
        bool isTimerContentVisible, bool isExpanded, bool expected)
    {
        bool actual = MainWindow.ShouldUpdateCountdownVisual(isTimerContentVisible, isExpanded);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MainWindowXaml_CountdownProgressFill_UsesStretchScaleTransformAndLeftOrigin()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xamlPath = Path.Combine(repositoryRoot, "Windows", "MainWindow.xaml");
        var doc = XDocument.Load(xamlPath);

        var elements = doc.Descendants().ToList();
        var progressFill = elements.FirstOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "CountdownProgressFill"));

        Assert.NotNull(progressFill);

        // HorizontalAlignment should be Stretch (full width of track) and no fixed Width="0"
        var hAlignAttr = progressFill.Attributes().FirstOrDefault(a => a.Name.LocalName == "HorizontalAlignment");
        Assert.NotNull(hAlignAttr);
        Assert.Equal("Stretch", hAlignAttr.Value);

        var widthAttr = progressFill.Attributes().FirstOrDefault(a => a.Name.LocalName == "Width");
        Assert.Null(widthAttr);

        // RenderTransformOrigin should be "0,0.5" so scaling progresses from the left
        var originAttr = progressFill.Attributes().FirstOrDefault(a => a.Name.LocalName == "RenderTransformOrigin");
        Assert.NotNull(originAttr);
        Assert.Equal("0,0.5", originAttr.Value);

        // ScaleTransform named CountdownProgressScale should be present
        var scaleTransform = progressFill.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ScaleTransform" &&
                                 e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "CountdownProgressScale"));
        Assert.NotNull(scaleTransform);

        // Edge indicator inside fill with HorizontalAlignment="Right"
        var edge = progressFill.Descendants()
            .FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "CountdownProgressEdge"));
        Assert.NotNull(edge);
        var edgeAlignAttr = edge.Attributes().FirstOrDefault(a => a.Name.LocalName == "HorizontalAlignment");
        Assert.NotNull(edgeAlignAttr);
        Assert.Equal("Right", edgeAlignAttr.Value);
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
