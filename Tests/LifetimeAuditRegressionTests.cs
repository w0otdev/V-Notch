using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class LifetimeAuditRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("v1.8.0-rc2", "1.7.9", 1)]
    [InlineData("1.8.0", "1.8.0-beta1", 1)]
    [InlineData("1.8.0-alpha", "1.8.0-beta", -1)]
    [InlineData("1.8.0-beta.2", "1.8.0-beta.11", -1)]
    [InlineData("1.8.0-beta", "1.8.0-beta.1", -1)]
    [InlineData("1.8.0-1", "1.8.0-alpha", -1)]
    [InlineData("1.8.0+build.9", "1.8.0+build.1", 0)]
    [InlineData("1.8", "1.8.0.0", 0)]
    [InlineData("1.8.0.1", "1.8.0", 1)]
    [InlineData("1.8.0-999999999999999999999", "1.8.0-99999999999999999999", 1)]
    [InlineData("invalid", "1.8.0", 0)]
    public void ReleaseVersionOrdering(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateService.CompareVersions(left, right)));
        Assert.Equal(-expected, Math.Sign(UpdateService.CompareVersions(right, left)));
    }

    [Fact]
    public async Task OwnedHttpClientsAreDisposedButInjectedClientsRemainUsable()
    {
        var weather = new WeatherService();
        var weatherClient = (HttpClient)typeof(WeatherService).GetField("_http", PrivateInstance)!.GetValue(weather)!;
        weather.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => weatherClient.GetAsync("https://example.test"));
        var updates = new UpdateService();
        var updateClient = (HttpClient)typeof(UpdateService).GetField("_httpClient", PrivateInstance)!.GetValue(updates)!;
        updates.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => updateClient.GetAsync("https://example.test"));

        using var handler = new TrackingHandler();
        using var shared = new HttpClient(handler);
        new WeatherService(shared).Dispose();
        new UpdateService(shared, UpdateSecurityPolicy.FromEnvironment()).Dispose();
        Assert.False(handler.Disposed);
        using var response = await shared.GetAsync("https://example.test");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task GpuStopStartReusesWorkerAndShutdownClosesHandle()
    {
        var service = new GpuMonitorService();
        var handle = (AutoResetEvent)typeof(GpuMonitorService).GetField("_wakeSamplerEvent", PrivateInstance)!.GetValue(service)!;
        try
        {
            service.Start();
            var worker = typeof(GpuMonitorService).GetField("_gpuSamplerThread", PrivateInstance)!.GetValue(service);
            for (int i = 0; i < 20; i++) { service.Stop(); service.Start(); }
            Assert.Same(worker, typeof(GpuMonitorService).GetField("_gpuSamplerThread", PrivateInstance)!.GetValue(service));
        }
        finally { await service.DisposeAsync(); }
        Assert.Throws<ObjectDisposedException>(() => handle.Set());
        Assert.Throws<ObjectDisposedException>(service.Start);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentSettingsInstancesPersistCompleteJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "VNotch-Audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        try
        {
            await using var first = new SettingsService(path);
            await using var second = new SettingsService(path);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                (i % 2 == 0 ? first : second).SaveAsync(new NotchSettings { Width = 200 + i })));
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.InRange(saved.RootElement.GetProperty("Width").GetDouble(), 200, 239);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ClipboardShutdownKeepsDispatcherResponsiveAndPersistsQueuedCapture() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "VNotch-Audit-" + Guid.NewGuid().ToString("N"));
        var store = new ClipboardHistoryStore(root);
        var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        var capture = new TaskCompletionSource<ClipboardCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = (Channel<Task<ClipboardCapture>>)typeof(ClipboardHistoryController).GetField("_captures", PrivateInstance)!.GetValue(controller)!;
        channel.Writer.TryWrite(capture.Task);
        try
        {
            var shutdown = controller.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            bool dispatched = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatched = true);
            Assert.True(dispatched);
            Assert.False(shutdown.IsCompleted);
            capture.SetResult(new ClipboardCapture { Text = "persist before exit" });
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            await using var reopened = new ClipboardHistoryStore(root);
            await reopened.InitializeAsync();
            Assert.Single(reopened.Snapshot());
        }
        finally
        {
            capture.TrySetResult(new ClipboardCapture { Text = "persist before exit" });
            await controller.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void DisposingBluetoothModuleDoesNotDisposeInjectedService() => SharedStaTestRunner.Run(() =>
    {
        using var service = new BluetoothMonitorService(Dispatcher.CurrentDispatcher);
        new BluetoothModule(service).Dispose();
        Assert.False((bool)typeof(BluetoothMonitorService).GetField("_disposed", PrivateInstance)!.GetValue(service)!);
    });

    [Fact]
    public void BluetoothResolvedOnWorkerUsesExplicitUiDispatcher() => SharedStaTestRunner.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var service = await Task.Run(() => new BluetoothMonitorService(dispatcher));
        Assert.Same(dispatcher, typeof(BluetoothMonitorService).GetField("_dispatcher", PrivateInstance)!.GetValue(service));
    });

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
