using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerAutomationCacheTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrowserScansExcludeToolbarsEditsAndTabsInsideThePageDocument(bool includeBrowserToolbar) => SharedStaTestRunner.Run(() =>
    {
        var content = new StackPanel();
        if (includeBrowserToolbar)
            content.Children.Add(new ToolBar { Items = { new TextBox { Text = "https://youtube.com/watch?v=toolbar" } } });
        content.Children.Add(new TextBox { Text = "https://example.com/outside-toolbar" });
        var page = new StackPanel();
        page.Children.Add(new ToolBar { Items = { new TextBox { Text = "https://open.spotify.com/page-impostor" } } });
        page.Children.Add(new TabControl { Items = { new TabItem { Header = "Page tab" } } });
        for (int i = 0; i < 300; i++) page.Children.Add(new TextBox { Text = "https://example.com/input" });
        content.Children.Add(new PageDocument { Content = page });
        var window = new BackgroundWindow
        {
            Width = 400,
            Height = 200,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = content
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            var scan = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(hwnd);
                // Verify the fixture really exposes page controls that a broad
                // descendant query would otherwise mistake for browser chrome.
                var document = Assert.Single(root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)).Cast<AutomationElement>());
                Assert.Single(document.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar)).Cast<AutomationElement>());
                int toolbarCount = WindowTitleScanner.FindBrowserChromeElements(root, ControlType.ToolBar).Count;
                int tabCount = WindowTitleScanner.FindCachedTabs(root).Count;
                var address = WindowTitleScanner.FindChromiumAddressBar(root);
                string? url = address == null ? null : ((ValuePattern)address.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                return (toolbarCount, tabCount, url);
            });
            PumpUntilComplete(scan);
            var result = scan.GetAwaiter().GetResult();
            Assert.Equal(includeBrowserToolbar ? 1 : 0, result.toolbarCount);
            Assert.Equal(0, result.tabCount);
            Assert.Equal(includeBrowserToolbar ? "https://youtube.com/watch?v=toolbar" : null, result.url);
        }
        finally { window.Close(); }
    });

    private sealed class PageDocument : ContentControl
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new PageDocumentPeer(this);
    }

    private sealed class PageDocumentPeer(PageDocument owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    }

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
        IReadOnlyList<AutomationElement> tabs;
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
