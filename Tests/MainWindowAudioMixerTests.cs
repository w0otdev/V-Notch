using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowAudioMixerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void SnapshotBuildsSystemAndApplicationRowsAndFitsAvailableHeight(int applications) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var snapshot = Snapshot(applications);
        Invoke(window, "BuildAudioUI", snapshot);
        window.AudioRoot.Measure(new Size(800, double.PositiveInfinity));
        window.AudioRoot.Arrange(new Rect(0, 0, 800, window.AudioRoot.DesiredSize.Height));

        Assert.Equal(4, window.AudioRoot.Children.Count);
        Assert.Equal(2, Assert.IsType<StackPanel>(window.AudioRoot.Children[1]).Children.Count);
        Assert.Equal(applications,
            Assert.IsType<StackPanel>(window.AudioRoot.Children[3]).Children.Count);
        Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == "Speakers test");
        Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == "Microphone test");
        foreach (var session in snapshot.Sessions.Where(s => !s.IsSystemSounds))
            Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == session.DisplayName);
        double fit = (double)Invoke(window, "MeasureAudioFitHeight")!;
        Assert.InRange(fit, Field<double>(window, "_audioViewMinHeight"), Field<double>(window, "_audioViewMaxHeight"));
        Assert.Equal(fit - Field<double>(window, "_audioViewChrome"), window.AudioScrollViewer.Height);
    });

    [Fact]
    public void VolumeAndMetadataUpdatesReuseRowsUntilTheSessionStructureChanges() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Invoke(window, "ApplyAudioSnapshot", Snapshot(2), null);
        var originalRows = window.AudioRoot.Children.Cast<UIElement>().ToArray();
        window.AudioRoot.Measure(new Size(800, double.PositiveInfinity));
        window.AudioRoot.Arrange(new Rect(0, 0, 800, window.AudioRoot.DesiredSize.Height));
        var icon = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        icon.Freeze();
        var updated = Snapshot(2, icon, renamed: true);
        updated.Master = 0.85f;
        updated.Capture = 0.15f;
        Invoke(window, "ApplyAudioSnapshot", updated, null);

        Assert.Equal(originalRows, window.AudioRoot.Children.Cast<UIElement>());
        Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == "Renamed 1");
        Assert.Contains(Descendants<Image>(window.AudioRoot), image => ReferenceEquals(image.Source, icon));
        Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == "85%");
        Assert.Contains(Descendants<TextBlock>(window.AudioRoot), label => label.Text == "15%");
        Assert.Same(updated, Field<AudioMixerSnapshot>(window, "_lastAudioSnapshot"));
        Invoke(window, "EnsureAudioUIBuilt", updated);
        Assert.Same(originalRows[3], window.AudioRoot.Children[3]);

        Invoke(window, "ApplyAudioSnapshot", Snapshot(3), null);
        Assert.NotSame(originalRows[3], window.AudioRoot.Children[3]);
        Assert.Equal(3, Assert.IsType<StackPanel>(window.AudioRoot.Children[3]).Children.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadingRevealSettlesAndARepeatedSnapshotKeepsTheContentVisible(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            Invoke(window, "BuildAudioUI", Snapshot(2));
            Invoke(window, "SetAudioLoadingState", true);
            Assert.Equal(Visibility.Visible, window.AudioLoadingPanel.Visibility);
            Assert.Equal(0, window.AudioRoot.Opacity);
            Invoke(window, "SetAudioLoadingState", false);
            await WpfFrameWaiter.UntilAsync(() => window.AudioLoadingPanel.Visibility == Visibility.Collapsed,
                "mixer loading panel dismissal", ct);
            await WpfFrameWaiter.UntilAsync(() => window.AudioRoot.Opacity == 1 && window.AudioRootTranslate.Y == 0,
                "mixer content reveal", ct);
            Invoke(window, "SetAudioLoadingState", false);
            Assert.Equal(1, window.AudioRoot.Opacity);
            Assert.Equal(0, window.AudioRootTranslate.Y);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SectionHeadersCollapseReopenAndIgnoreInterruptedAnimationCompletions() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Invoke(window, "BuildAudioUI", Snapshot(2));
        var vm = (ShellViewModel)window.DataContext;
        for (int section = 0; section < 2; section++)
        {
            var header = Assert.IsType<Grid>(window.AudioRoot.Children[section * 2]);
            var target = Assert.IsType<StackPanel>(header.Children[0]);
            var rows = Assert.IsType<StackPanel>(window.AudioRoot.Children[section * 2 + 1]);
            Click(target);
            await WpfFrameWaiter.UntilAsync(() => rows.Visibility == Visibility.Collapsed, "collapsed mixer section", ct);
            Assert.False(section == 0 ? vm.AudioMixer.IsSystemExpanded : vm.AudioMixer.IsApplicationsExpanded);
            Click(target);
            Click(target);
            Click(target);
            await WpfFrameWaiter.UntilAsync(() => rows.Visibility == Visibility.Visible && double.IsNaN(rows.Height),
                "reopened mixer section after interruption", ct);
            Assert.True(section == 0 ? vm.AudioMixer.IsSystemExpanded : vm.AudioMixer.IsApplicationsExpanded);
            Assert.Equal(1, rows.Opacity);
            foreach (var label in header.Children.OfType<TextBlock>()) Assert.Equal(Visibility.Visible, label.Visibility);
        }
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services =>
        {
            services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService());
            services.AddSingleton<IVolumeService>(new FakeVolumeService());
        });

    private static AudioMixerSnapshot Snapshot(int count, ImageSource? icon = null, bool renamed = false) => new()
    {
        Output = [new() { Id = "output", FriendlyName = "Speakers test", IsDefault = true }],
        Input = [new() { Id = "input", FriendlyName = "Microphone test", IsDefault = true }],
        Master = 0.6f,
        Capture = 0.4f,
        Sessions = Enumerable.Range(1, count).Select(i => new AudioSessionInfo
        {
            ProcessId = (uint)i,
            DisplayName = renamed ? $"Renamed {i}" : $"Application {i}",
            Volume = i == 1 ? 0.3f : 0.7f,
            IsMuted = i == 2,
            Icon = icon
        }).Append(new AudioSessionInfo { ProcessId = 0, IsSystemSounds = true, DisplayName = "System sounds" }).ToList()
    };

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Click(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
        Environment.TickCount, MouseButton.Left)
    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments);
    private static T Field<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Private);
        return (T)(field != null ? field.GetValue(window)! : typeof(MainWindow).GetProperty(name, Private)!.GetValue(window)!);
    }
}
