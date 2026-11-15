using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SystemMonitorPresenterLifetimeTests
{
    private static void Publish(SystemMonitorModule module, SystemMonitorInfo stats)
        => ((EventHandler<SystemMonitorInfo>?)typeof(SystemMonitorModule)
            .GetField("StatsUpdated", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module))?.Invoke(module, stats);

    [Fact]
    public void OneSubscriptionUpdatesBothViewsAndPreservesNetworkDiagnostics() => SharedStaTestRunner.Run(() =>
    {
        using var module = new SystemMonitorModule();
        var dispatcher = new TestDispatcher();
        var shelf = new SystemMonitorShelfViewRefs(new Border(), new TextBlock(), new Border(),
            new TextBlock(), new Border(), new TextBlock(), new TextBlock());
        var refs = CreateRefs() with { Shelf = shelf };
        var window = new Window { Content = shelf.Section, ShowActivated = false, Width = 200, Height = 100 };
        window.Show();
        try
        {
            using var presenter = new SystemMonitorPresenter(module, dispatcher, refs);
            Publish(module, new SystemMonitorInfo
            {
                CpuPercent = 42,
                RamUsedBytes = 1024UL * 1024 * 1024,
                RamTotalBytes = 8UL * 1024 * 1024 * 1024,
                RamPercent = 12.5,
                NetDownBytesPerSec = 2048,
                NetUpBytesPerSec = 16
            });
            Assert.Equal("42%", refs.CpuValueText.Text);
            Assert.Equal(refs.CpuValueText.Text, shelf.CpuValueText.Text);
            Assert.EndsWith(" GB", shelf.RamValueText.Text);
            Assert.Equal("↓ 2.0 KB/s", shelf.NetDownText.Text);
            Assert.Equal(2048, presenter.LastNetDownBytesPerSec);
            Assert.Equal(16, presenter.LastNetUpBytesPerSec);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HiddenWidgetKeepsDiagnosticsWithoutUpdatingTextOrAnimation() => SharedStaTestRunner.Run(() =>
    {
        using var module = new SystemMonitorModule();
        var refs = CreateRefs() with { Section = new Border() };
        var track = new Border { Width = 100, Height = 10, Child = refs.CpuBar };
        var window = new Window { Content = new StackPanel { Children = { refs.Section, track } }, ShowActivated = false, Width = 200, Height = 100 };
        using var presenter = new SystemMonitorPresenter(module, new TestDispatcher(), refs);
        window.Show();
        window.UpdateLayout();
        try
        {
            Publish(module, new SystemMonitorInfo { CpuPercent = 42, NetDownBytesPerSec = 128 });
            Assert.Equal("42%", refs.CpuValueText.Text);
            refs.Section.Visibility = Visibility.Collapsed;
            Assert.False(refs.CpuBar.HasAnimatedProperties);
            Publish(module, new SystemMonitorInfo { CpuPercent = 99, NetDownBytesPerSec = 256 });
            Assert.Equal("42%", refs.CpuValueText.Text);
            Assert.False(refs.CpuBar.HasAnimatedProperties);
            Assert.Equal(256, presenter.LastNetDownBytesPerSec);
            refs.Section.Visibility = Visibility.Visible;
            Publish(module, new SystemMonitorInfo { CpuPercent = 23 });
            Assert.Equal("23%", refs.CpuValueText.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void QueuedUpdateCannotTouchDisposedView() => SharedStaTestRunner.Run(() =>
    {
        using var module = new SystemMonitorModule();
        var dispatcher = new TestDispatcher { HasAccess = false };
        var refs = CreateRefs();
        var presenter = new SystemMonitorPresenter(module, dispatcher, refs);
        Publish(module, new SystemMonitorInfo { CpuPercent = 42 });
        Assert.Single(dispatcher.Pending);
        presenter.Dispose();
        presenter.Dispose();
        dispatcher.Pending.Dequeue()();
        Assert.Equal(string.Empty, refs.CpuValueText.Text);
        Publish(module, new SystemMonitorInfo { CpuPercent = 99 });
        Assert.Empty(dispatcher.Pending);
    });

    private static SystemMonitorViewRefs CreateRefs() => new(new TextBlock(), new Border(),
        new TextBlock(), new Border(), new TextBlock(), new TextBlock());

    private sealed class TestDispatcher : IDispatcherService
    {
        public bool HasAccess { get; init; } = true;
        public Queue<Action> Pending { get; } = new();
        public bool CheckAccess() => HasAccess;
        public void BeginInvoke(Action action) => Pending.Enqueue(action);
        public void Invoke(Action action) => throw new InvalidOperationException("Stats must not block the sampling thread.");
    }
}
