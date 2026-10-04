using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerAutomationCacheTests
{
    [Fact]
    public void ChildValuePatternIsReadFromTheSnapshot() => SharedStaTestRunner.Run(() =>
    {
        var editor = new TextBox { Text = "https://open.spotify.com/track/child" };
        AutomationProperties.SetName(editor, "URL editor");
        var control = new TabControl { Items = { new TabItem { Header = "Player", Content = editor } } };
        var window = new BackgroundWindow
        {
            Width = 400,
            Height = 200,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = control
        };
        AutomationElement tab;
        try
        {
            window.Show();
            window.UpdateLayout();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            var snapshot = Task.Run(() => WindowTitleScanner.FindCachedTabs(AutomationElement.FromHandle(hwnd)));
            PumpUntilComplete(snapshot);
            tab = Assert.Single(snapshot.GetAwaiter().GetResult().Cast<AutomationElement>());
        }
        finally { window.Close(); }
        Assert.Equal("https://open.spotify.com/track/child", WindowTitleScanner.TryReadTabUrl(tab));
        Assert.True(WindowTitleScanner.TabReferencesSpotifyWebPlayer(tab));
    });

    [Fact]
    public void FiftyTabSnapshotsRemainReadableAfterTheProviderWindowCloses() => SharedStaTestRunner.Run(() =>
    {
        var control = new TabControl();
        for (int i = 0; i < 50; i++)
        {
            var tab = new TabItem { Header = $"Tab {i}" };
            AutomationProperties.SetHelpText(tab, i == 49
                ? "https://open.spotify.com/track/example"
                : $"https://youtube.com/watch?v=video{i}");
            control.Items.Add(tab);
        }
        var window = new BackgroundWindow
        {
            Width = 400,
            Height = 200,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = control
        };
        AutomationElementCollection tabs;
        try
        {
            window.Show();
            window.UpdateLayout();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            var snapshot = Task.Run(() => WindowTitleScanner.FindCachedTabs(AutomationElement.FromHandle(hwnd)));
            PumpUntilComplete(snapshot);
            tabs = snapshot.GetAwaiter().GetResult();
        }
        finally { window.Close(); }

        Assert.Equal(50, tabs.Count);
        for (int i = 0; i < 49; i++)
            Assert.Equal($"https://youtube.com/watch?v=video{i}", WindowTitleScanner.TryReadTabUrl(tabs[i]));
        Assert.Equal("https://open.spotify.com/track/example", WindowTitleScanner.TryReadTabUrl(tabs[49]));
        Assert.True(WindowTitleScanner.TabReferencesSpotifyWebPlayer(tabs[49]));
        Assert.False(WindowTitleScanner.TabReferencesSpotifyWebPlayer(tabs[0]));
        // None mode forbids live access: the readers above must use only cached data.
        Assert.Throws<InvalidOperationException>(() => _ = tabs[0].Current.Name);
    });

    private static void PumpUntilComplete(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => frame.Continue = false;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(task.IsCompleted, "UI Automation did not finish while the provider dispatcher was pumping.");
    }
}
