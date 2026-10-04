using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class EverythingSearchIpcTests
{
    [Theory]
    [InlineData("report", 0)]
    [InlineData("docs/report", 4)]
    public void SearchUsesTheIpcProtocolAndFiltersAndRanksItsReply(string query, uint expectedFlags) => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplication();
        using var server = new Server([
            ("report.pdf", @"C:\Fixture\docs", false),
            ("report", @"C:\Fixture\docs", true),
            ("runner.exe", @"C:\Fixture\docs", false),
            ("excluded.pdf", @"C:\Fixture\docs", false),
            ("report.dll", @"C:\Fixture\docs", false),
            ("report.pdf", @"\\server\share", false),
            ("unrelated.txt", @"C:\Fixture\docs", false)]);
        using var provider = new EverythingSearchProvider(() => server.Handle) { IsExcluded = path => path.EndsWith("excluded.pdf", StringComparison.Ordinal) };
        var results = await provider.SearchAsync(query, 4, ct);
        Assert.True(provider.IsAvailable);
        Assert.True(provider.IsInstant);
        Assert.Equal(expectedFlags, server.Flags);
        Assert.Equal(100, server.MaximumResults);
        Assert.Equal(SpotlightFileVisibility.BuildEverythingQuery(query), server.Query);
        Assert.Equal(4, results.Count);
        Assert.Contains(results, item => item.Kind == SpotlightResultKind.Folder);
        Assert.Contains(results, item => item.Kind == SpotlightResultKind.Application);
        Assert.DoesNotContain(results, item => item.Title == "excluded.pdf" || item.Title.EndsWith(".dll", StringComparison.Ordinal) || item.Target.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.All(results, item => Assert.True(item.Score > 0));
        Assert.Equal(results.OrderByDescending(item => item.Score).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase), results);
        await provider.SearchAsync(query, 100, ct);
        Assert.Equal(512, server.MaximumResults);
    });

    [Fact]
    public void QueryWithoutAReplyTimesOutAndReleasesTheNextQuery() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplication();
        using var server = new Server([("report.txt", @"C:\Fixture", false)]) { DropReplies = true };
        using var provider = new EverythingSearchProvider(() => server.Handle);
        Assert.Empty(await provider.SearchAsync("report", 3, ct));
        Assert.False(provider.IsAvailable);
        server.DropReplies = false;
        Assert.Single(await provider.SearchAsync("report", 3, ct));
        Assert.True(provider.IsAvailable);
    });

    [Fact]
    public void CancellationAndDisposalAllowPendingAndQueuedQueriesToFinishSafely() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplication();
        using var server = new Server([]) { DropReplies = true };
        using var provider = new EverythingSearchProvider(() => server.Handle);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IReadOnlyList<SpotlightSearchItem>> first = provider.SearchAsync("report", 3, cancel.Token);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        server.Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first = provider.SearchAsync("report", 3, ct);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Task<IReadOnlyList<SpotlightSearchItem>> queued = provider.SearchAsync("report", 3, ct);
        provider.Dispose();
        Assert.Empty(await first);
        Assert.Empty(await queued);
        Assert.Empty(await provider.SearchAsync("report", 3, ct));
        provider.Dispose();
    });

    [Fact]
    public void DisconnectedIpcWindowAndInvalidQueriesReturnNoResults() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplication();
        using var server = new Server([]);
        IntPtr handle = server.Handle;
        server.Dispose();
        using var provider = new EverythingSearchProvider(() => handle);
        Assert.Empty(await provider.SearchAsync("report", 3, ct));
        Assert.False(provider.IsAvailable);
        Assert.Empty(await provider.SearchAsync("x", 3, ct));
        Assert.Empty(await provider.SearchAsync("report", 0, ct));
        using var unavailable = new EverythingSearchProvider(() => IntPtr.Zero);
        Assert.Empty(await unavailable.SearchAsync("report", 3, ct));
        Assert.False(unavailable.IsAvailable);
    });

    private static void EnsureApplication() => _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    private sealed class Server : IDisposable
    {
        private HwndSource? _window;
        private readonly IReadOnlyList<(string Name, string Parent, bool Folder)> _rows;
        public IntPtr Handle => _window!.Handle;
        public bool DropReplies { get; set; }
        public TaskCompletionSource Received { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Flags { get; private set; }
        public int MaximumResults { get; private set; }
        public string Query { get; private set; } = "";
        public Server(IReadOnlyList<(string Name, string Parent, bool Folder)> rows)
        {
            _rows = rows;
            _window = new HwndSource(new HwndSourceParameters("VNotchTestEverything") { ParentWindow = new(-3), Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(Receive);
        }
        private IntPtr Receive(IntPtr hwnd, int message, IntPtr sender, IntPtr pointer, ref bool handled)
        {
            if (message != 0x4A || pointer == IntPtr.Zero) return IntPtr.Zero;
            var data = Marshal.PtrToStructure<CopyData>(pointer);
            Assert.Equal(2, data.Id.ToInt32());
            IntPtr reply = new((long)(uint)Marshal.ReadInt32(data.Buffer, 0));
            uint id = (uint)Marshal.ReadInt32(data.Buffer, 4);
            Flags = (uint)Marshal.ReadInt32(data.Buffer, 8);
            Assert.Equal(0, Marshal.ReadInt32(data.Buffer, 12));
            MaximumResults = Marshal.ReadInt32(data.Buffer, 16);
            Query = Marshal.PtrToStringUni(data.Buffer + 20)!;
            Received.TrySetResult();
            if (!DropReplies) SendReply(reply, id);
            handled = true;
            return new(1);
        }
        private void SendReply(IntPtr reply, uint id)
        {
            int length = 28 + _rows.Count * 12 + _rows.Sum(row => (row.Name.Length + row.Parent.Length + 2) * 2);
            IntPtr buffer = Marshal.AllocHGlobal(length);
            IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
            try
            {
                Marshal.Copy(new byte[length], 0, buffer, length);
                Marshal.WriteInt32(buffer, 20, _rows.Count);
                int cursor = 28 + _rows.Count * 12;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    Marshal.WriteInt32(buffer, 28 + i * 12, row.Folder ? 1 : 0);
                    Marshal.WriteInt32(buffer, 32 + i * 12, cursor);
                    cursor = WriteString(buffer, cursor, row.Name);
                    Marshal.WriteInt32(buffer, 36 + i * 12, cursor);
                    cursor = WriteString(buffer, cursor, row.Parent);
                }
                Marshal.StructureToPtr(new CopyData { Id = new((long)id), Bytes = length, Buffer = buffer }, header, false);
                Assert.Equal(new IntPtr(1), SendMessageW(reply, 0x4A, Handle, header));
            }
            finally { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(buffer); }
        }
        private static int WriteString(IntPtr buffer, int offset, string value)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
            Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
            return offset + bytes.Length;
        }
        public void Dispose()
        {
            _window?.RemoveHook(Receive);
            _window?.Dispose();
            _window = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public IntPtr Id; public int Bytes; public IntPtr Buffer; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr sender, IntPtr data);
}
