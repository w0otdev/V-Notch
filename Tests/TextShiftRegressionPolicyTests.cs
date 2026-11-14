using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed partial class TextShiftRegressionPolicyTests
{
    [Fact]
    public void LoadedContainersAndControlsNormalizeFormattingHintingAndLayoutRounding() => SharedStaTestRunner.Run(() =>
    {
        TextRenderingPolicy.Register();

        var explicitClearType = new TextBlock { Text = "ClearType" };
        TextOptions.SetTextRenderingMode(explicitClearType, TextRenderingMode.ClearType);

        var textBlock = new TextBlock { Text = "Ideal Animated" };
        TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(textBlock, TextHintingMode.Fixed);
        textBlock.UseLayoutRounding = true;

        var accessText = new AccessText { Text = "_Save" };
        var button = new Button { Content = accessText, UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(button, TextFormattingMode.Display);

        var panel = new StackPanel { UseLayoutRounding = true };
        TextOptions.SetTextHintingMode(panel, TextHintingMode.Fixed);
        panel.Children.Add(textBlock);
        panel.Children.Add(button);
        panel.Children.Add(explicitClearType);

        var decorator = new Border { Child = panel, UseLayoutRounding = true };
        var hostWindow = new Window { Content = decorator, UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(hostWindow, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(hostWindow, TextHintingMode.Fixed);

        foreach (FrameworkElement element in new FrameworkElement[]
                 {
                     hostWindow, decorator, panel, button, accessText, textBlock, explicitClearType,
                     new ContentPresenter { Content = "Item", UseLayoutRounding = true },
                     new Popup { UseLayoutRounding = true }
                 })
        {
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        }

        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(hostWindow));
        Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(hostWindow));
        Assert.False(hostWindow.UseLayoutRounding);

        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(panel));
        Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(panel));
        Assert.False(panel.UseLayoutRounding);

        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(textBlock));
        Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(textBlock));
        Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(textBlock));
        Assert.False(textBlock.UseLayoutRounding);

        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(button));
        Assert.False(button.UseLayoutRounding);
        Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(explicitClearType));
    });

    [Fact]
    public void SettledTransformAnimationMatchesStaticSubpixelRenderByteForByte() => SharedStaTestRunner.RunAsync(async ct =>
    {
        TextRenderingPolicy.Register();

        static Grid BuildSampleSurface(out ScaleTransform scale, out TranslateTransform translate)
        {
            scale = new ScaleTransform(1.0, 1.0);
            translate = new TranslateTransform(0.0, 8.5);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);

            var label = new TextBlock
            {
                Text = "Subpixel stability 0123 Tiếng Việt",
                FontSize = 14,
                Foreground = Brushes.White
            };
            var container = new Grid
            {
                Width = 240,
                Height = 48,
                Background = Brushes.Black,
                RenderTransformOrigin = new Point(0.5, 0.0),
                RenderTransform = group
            };
            container.Children.Add(label);
            container.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            return container;
        }

        static byte[] RenderSurface(Grid surface)
        {
            surface.Measure(new Size(240, 48));
            surface.Arrange(new Rect(0, 0, 240, 48));
            surface.UpdateLayout();
            var rtb = new RenderTargetBitmap(240, 48, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(surface);
            byte[] pixels = new byte[240 * 48 * 4];
            rtb.CopyPixels(pixels, 240 * 4, 0);
            return pixels;
        }

        var baseline = BuildSampleSurface(out _, out _);
        byte[] expected = RenderSurface(baseline);

        var animated = BuildSampleSurface(out var scale, out var translate);
        bool completed = false;
        var scaleAnim = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(40));
        var moveAnim = new DoubleAnimation(16.5, 8.5, TimeSpan.FromMilliseconds(40));
        moveAnim.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            translate.Y = 8.5;
            completed = true;
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, moveAnim);

        await WpfFrameWaiter.UntilAsync(() => completed, "transform settlement", ct);
        byte[] actual = RenderSurface(animated);
        Assert.Equal(expected, actual);
    });

    [Fact]
    public void UiSourcesDisallowTextShiftingRenderingModesRoundingAndCacheToggles()
    {
        string repoRoot = FindRepoRoot();
        string[] directories = ["Windows", "Controls", "Presenters"];

        foreach (string relativeDir in directories)
        {
            string dirPath = Path.Combine(repoRoot, relativeDir);
            foreach (string file in Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string content = File.ReadAllText(file);
                string relPath = Path.GetRelativePath(repoRoot, file);

                Assert.False(DisplayFormattingRegex().IsMatch(content), $"{relPath} reintroduced Display text formatting.");
                Assert.False(FixedHintingRegex().IsMatch(content), $"{relPath} reintroduced Fixed text hinting.");
                Assert.False(ClearTypeModeRegex().IsMatch(content), $"{relPath} reintroduced ClearType rendering mode.");
                Assert.False(LayoutRoundingTrueRegex().IsMatch(content), $"{relPath} reintroduced UseLayoutRounding=True.");
                Assert.False(BitmapCacheAllocRegex().IsMatch(content), $"{relPath} reintroduced runtime BitmapCache.");
                Assert.DoesNotContain("CollapsedContentBlur", content, StringComparison.Ordinal);
                Assert.DoesNotContain("CalendarGreetingContextBlur", content, StringComparison.Ordinal);
                Assert.DoesNotContain("EnsureContentBlurEffect", content, StringComparison.Ordinal);
            }
        }

        string settingsXaml = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.xaml"));
        Assert.Contains("x:Name=\"MainShellShadow\"", settingsXaml, StringComparison.Ordinal);

        string mediaPlaybackXaml = File.ReadAllText(Path.Combine(repoRoot, "Controls", "MediaPlaybackView.xaml"));
        Assert.DoesNotContain("<BitmapCache", mediaPlaybackXaml, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "V-Notch.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate V-Notch.csproj from {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"TextFormattingMode\s*[=.]\s*""?Display\b", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayFormattingRegex();

    [GeneratedRegex(@"TextHintingMode\s*[=.]\s*""?Fixed\b", RegexOptions.CultureInvariant)]
    private static partial Regex FixedHintingRegex();

    [GeneratedRegex(@"TextRenderingMode\s*[=.]\s*""?ClearType\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClearTypeModeRegex();

    [GeneratedRegex(@"UseLayoutRounding\s*(?:=\s*""?[Tt]rue""?|,\s*""?[Tt]rue""?|Value\s*=\s*""[Tt]rue"")", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutRoundingTrueRegex();

    [GeneratedRegex(@"new\s+(?:System\.Windows\.Media\.)?BitmapCache\b", RegexOptions.CultureInvariant)]
    private static partial Regex BitmapCacheAllocRegex();
}
