using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.ViewModels;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightPersonalizationAndMorphTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PinMenuUpdatesMatchingRowsAndRebuildsEmptyQueryResultsAfterFeedback(bool reducedMotion) => WithFixture(async (fixture, ct) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var window = fixture.Window;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "personalization entrance", ct);
            var item = ApplicationItem(fixture, "fixture");
            var vm = (SpotlightViewModel)window.DataContext;
            vm.Results.Add(item);
            var menu = new ContextMenu();
            Invoke(window, "AddPersonalizationActions", menu, item);
            Assert.Equal(new[] { Loc.Get("spotlight.pin"), Loc.Get("spotlight.alias"), Loc.Get("spotlight.excludedFolders") }, menu.Items.Cast<MenuItem>().Select(row => row.Header));
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(item.IsPinned);
            Assert.True(vm.Preferences.IsPinned(item.Target));
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 1 && vm.Results[0].IsPinned && vm.Results[0].Target == item.Target, "pinned empty-query result", ct);
            await Task.Delay(280, ct);
            menu = new ContextMenu();
            Invoke(window, "AddPersonalizationActions", menu, item);
            Assert.Equal(Loc.Get("spotlight.unpin"), ((MenuItem)menu.Items[0]).Header);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(item.IsPinned);
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 0, "unpinned empty-query result removed", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void PinLimitShowsAnExplanationAndRapidPinUnpinKeepsTheLatestChoice() => WithFixture(async (fixture, ct) =>
    {
        var window = fixture.Window;
        var vm = (SpotlightViewModel)window.DataContext;
        for (int i = 0; i < SpotlightPreferences.MaxPinnedApps; i++) Assert.True(vm.Preferences.SetApp(ApplicationItem(fixture, "pinned" + i), true, ""));
        var overflow = ApplicationItem(fixture, "overflow");
        Invoke(window, "TogglePinnedApplication", overflow);
        Assert.False(overflow.IsPinned);
        Assert.False(vm.Preferences.IsPinned(overflow.Target));
        Assert.Equal(Loc.Get("spotlight.pinLimit"), window.FailureText.Text);
        Assert.Equal(Visibility.Visible, window.FailureBar.Visibility);
        var item = ApplicationItem(fixture, "pinned0");
        Invoke(window, "TogglePinnedApplication", item);
        Invoke(window, "TogglePinnedApplication", item);
        Assert.True(vm.Preferences.IsPinned(item.Target));
        await Task.Delay(300, ct);
        Assert.True(item.IsPinned);
        Assert.Equal(10, vm.Preferences.Search("").Count);
    });

    [Theory]
    [InlineData(SpotlightResultKind.File)]
    [InlineData(SpotlightResultKind.Folder)]
    public void ExcludingAResultExcludesItsContainingFolderAndTracksContextMenuLifetime(SpotlightResultKind kind) => WithFixture(async (fixture, ct) =>
    {
        string folder = Path.Combine(fixture.DirectoryPath, "excluded");
        Directory.CreateDirectory(folder);
        string target = kind == SpotlightResultKind.Folder ? folder : Path.Combine(folder, "document.txt");
        var item = new SpotlightSearchItem("excluded", kind, "Fixture", "", target);
        var menu = new ContextMenu();
        Invoke(fixture.Window, "AddPersonalizationActions", menu, item);
        Assert.Equal(2, menu.Items.Count);
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var preferences = ((SpotlightViewModel)fixture.Window.DataContext).Preferences;
        Assert.True(preferences.IsExcluded(target));
        Assert.True(preferences.IsExcluded(Path.Combine(folder, "nested", "file.txt")));
        Assert.False(preferences.IsExcluded(folder + "-other"));
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(Field<bool>(fixture.Window, "_resultMenuOpen"));
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        Assert.False(Field<bool>(fixture.Window, "_resultMenuOpen"));
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreferenceEditorsPrepareAccessibleFolderAndApplicationFormsAndRejectInvalidFolders(bool application) => WithFixture(async (fixture, ct) =>
    {
        var preferences = ((SpotlightViewModel)fixture.Window.DataContext).Preferences;
        var item = application ? ApplicationItem(fixture, "fixture") : null;
        if (item != null) Assert.True(preferences.SetApp(item, false, "Fixture alias"));
        var dialog = new SpotlightPreferencesDialog(preferences, item) { Opacity = 0 };
        BackgroundTestWindows.ProtectInput(dialog);
        try
        {
            Assert.Equal(dialog.Title, dialog.Heading.Text);
            Assert.Equal(dialog.Title, System.Windows.Automation.AutomationProperties.GetName(dialog.Editor));
            Assert.Equal(application, dialog.SaveButton.IsDefault);
            if (application)
            {
                Assert.Equal("Fixture alias", dialog.Editor.Text);
                Assert.Equal(80, dialog.Editor.MaxLength);
                Assert.Equal("fixture", dialog.AppTitle.Text);
                Assert.Equal(Visibility.Visible, dialog.AppGlyph.Visibility);
            }
            else
            {
                Assert.True(dialog.Editor.AcceptsReturn);
                dialog.Editor.Text = "relative/folder";
                Invoke(dialog, "Save_Click", dialog.SaveButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
                Assert.Equal(Loc.Get("spotlight.invalidFolder"), dialog.ErrorText.Text);
                Assert.Empty(preferences.GetExcludedFolders());
                dialog.Editor.Text = fixture.DirectoryPath;
                Assert.Equal(Visibility.Collapsed, dialog.ErrorText.Visibility);
            }
            Invoke(dialog, "Window_Loaded", dialog, new RoutedEventArgs());
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => dialog.Shell.Opacity == 1 && dialog.EntranceOffset.Y == 0, "preference form animation resting values", ct);
            Assert.Equal(1, dialog.Shell.Opacity);
            Assert.Equal(0, dialog.EntranceOffset.Y);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosingAndReversingSpotlightMorphRestoresLayoutAndReleasesSourceOwnership(bool notch, bool snapshot) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { EnableSpotlightHistory = false, AutoCheckUpdates = false });
            var window = fixture.Window;
            var host = new MorphHost(snapshot);
            if (notch) window.MorphHostOverride = host;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "Spotlight entrance completed", ct);
            Assert.Equal(1, window.Shell.Opacity);
            Assert.True(double.IsNaN(window.Shell.Width));
            if (notch) Assert.True(host.SessionActive);
            window.SearchBox.Text = "fixture query";
            await WpfFrameWaiter.UntilAsync(() => window.ContentRegion.Visibility == Visibility.Visible, "query status content revealed", ct);
            window.HideSpotlight();
            Assert.True(Field<bool>(window, "_isClosing"));
            if (notch) Assert.True(window.ContentRegion.ClipToBounds);
            window.ToggleFromHotkey();
            Assert.False(Field<bool>(window, "_isClosing"));
            Assert.True(window.SearchBox.IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive") && !window.ContentRegion.ClipToBounds, "reversed Spotlight entrance and content layout completed", ct);
            Assert.Equal("fixture query", window.SearchBox.Text);
            Assert.True(double.IsNaN(window.Shell.Width));
            Assert.True(double.IsNaN(window.ContentRegion.Width));
            Assert.False(window.ContentRegion.ClipToBounds);
            window.HideSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "Spotlight exit handoff completed", ct);
            Assert.False(Field<bool>(window, "_isClosing"));
            Assert.Equal(Visibility.Hidden, window.Shell.Visibility);
            Assert.Equal(0, window.Shell.Opacity);
            Assert.Null(window.NotchMorphSnapshotBrush.ImageSource);
            if (notch)
            {
                Assert.False(host.SessionActive);
                Assert.False(host.MorphActive);
                Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(host.Handoffs));
            }
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "parked Spotlight reopened", ct);
            Assert.Equal("fixture query", window.SearchBox.Text);
            window.DismissFromGlobalShortcut();
            window.DismissFromGlobalShortcut();
            Assert.False(window.IsSpotlightOpen);
            window.ShowSpotlight();
            Assert.Equal("", window.SearchBox.Text);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static void WithFixture(Func<SpotlightWindowFixture, CancellationToken, Task> action) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings { EnableSpotlightHistory = false, AutoCheckUpdates = false });
        try { await action(fixture, ct); }
        finally
        {
            var preferences = ((SpotlightViewModel)fixture.Window.DataContext).Preferences;
            object gate = typeof(SpotlightPreferences).GetField("_gate", Private)!.GetValue(preferences)!;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                bool saving;
                lock (gate) saving = (bool)typeof(SpotlightPreferences).GetField("_saving", Private)!.GetValue(preferences)!;
                if (!saving) break;
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Spotlight preference saves did not drain.");
                await Task.Delay(10);
            }
        }
    });

    private static SpotlightSearchItem ApplicationItem(SpotlightWindowFixture fixture, string name)
    {
        string path = Path.Combine(fixture.DirectoryPath, name + ".exe");
        File.WriteAllBytes(path, []);
        return new SpotlightSearchItem(name, SpotlightResultKind.Application, name + ".exe", "", path);
    }

    private sealed class MorphHost(bool snapshot) : ISpotlightMorphHost
    {
        internal bool SessionActive { get; private set; }
        internal bool MorphActive { get; private set; }
        internal List<TimeSpan> Handoffs { get; } = [];
        public (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius) GetSpotlightMorphRect() => (240, 0, 230, 34, 0, 12);
        public ImageSource? CaptureSpotlightMorphVisual()
        {
            if (!snapshot) return null;
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            bitmap.Freeze();
            return bitmap;
        }
        public void SetSpotlightMorphSessionActive(bool active) => SessionActive = active;
        public void SetSpotlightMorphActive(bool active) => MorphActive = active;
        public void BeginSpotlightReturnHandoff(TimeSpan duration) => Handoffs.Add(duration);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object?[] arguments) => target.GetType().GetMethod(method, Private)!.Invoke(target, arguments);
}
