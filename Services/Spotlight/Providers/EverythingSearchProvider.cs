using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class EverythingSearchProvider : ISpotlightProvider, IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_everything_provider_compute_search_params(
        ReadOnlySpan<char> query,
        int queryLen,
        int limit,
        out int outMaxResults,
        out uint outSearchFlags);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_everything_provider_classify_item_kind(
        byte isFolder,
        ReadOnlySpan<char> ext,
        int extLen,
        out int outKind);

    [LibraryImport(NativeDll)]
    private static partial int vn_everything_provider_compute_final_score(
        double rankerScore,
        int titleLen,
        out double outScore);

    [LibraryImport(NativeDll)]
    private static partial int vn_everything_provider_parse_bounded_utf16_len(
        IntPtr basePtr,
        int offset,
        int totalBytes,
        out int outCharLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_everything_provider_read_reply_item_count(
        IntPtr listPtr,
        int byteCount,
        out int outCount);

    [LibraryImport(NativeDll)]
    private static partial int vn_everything_provider_extract_reply_item(
        IntPtr listPtr,
        int byteCount,
        int index,
        out int outNameOffset,
        out int outNameLen,
        out int outPathOffset,
        out int outPathLen,
        out byte outIsFolder);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_everything_provider_pack_ipc_query(
        long replyHwnd,
        uint queryId,
        uint searchFlags,
        int maxResults,
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        IntPtr outBuf,
        int outCap,
        out int outBytes);

    internal Func<string, bool>? IsExcluded { get; set; }

    private const string EverythingIpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const int CopyDataQueryW = 2;
    private const int WmCopyData = 0x004A;

    private const int QueryHeaderBytes = 20;
    private const int ReplyTimeoutMilliseconds = 900;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly object _replyGate = new();
    private HwndSource? _replyWindow;
    private uint _queryId;
    private TaskCompletionSource<IReadOnlyList<(string Name, string Parent, bool IsFolder)>>? _pendingReply;
    private uint _pendingReplyId;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(15);
    private volatile bool _disposed;
    private volatile bool _ipcFailed;
    private long _lastFailureTicks = DateTime.MinValue.Ticks;
    private readonly Func<IntPtr> _findWindow;

    public EverythingSearchProvider() : this(() => Win32Interop.FindWindow(EverythingIpcWindowClass)) { }

    internal EverythingSearchProvider(Func<IntPtr> findWindow) => _findWindow = findWindow;

    public bool IsAvailable
    {
        get
        {
            if (_disposed || _findWindow() == IntPtr.Zero) return false;
            if (!_ipcFailed) return true;
            return DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFailureTicks) > RetryCooldown.Ticks;
        }
        private set
        {
            if (!value) Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            _ipcFailed = !value;
        }
    }

    public bool IsInstant => true;

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        if (_disposed) return Array.Empty<SpotlightSearchItem>();
        if (!SpotlightFileVisibility.ShouldSearch(query) || limit <= 0) return Array.Empty<SpotlightSearchItem>();

        IntPtr everythingWindow = _findWindow();
        if (everythingWindow == IntPtr.Zero)
        {
            IsAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            IsAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }

        IReadOnlyList<(string Name, string Parent, bool IsFolder)> rows;
        await _queryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return Array.Empty<SpotlightSearchItem>();
            IntPtr replyHwnd = await EnsureReplyWindowAsync(dispatcher).ConfigureAwait(false);
            if (replyHwnd == IntPtr.Zero)
            {
                IsAvailable = false;
                return Array.Empty<SpotlightSearchItem>();
            }

            var completion = new TaskCompletionSource<IReadOnlyList<(string, string, bool)>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            uint id;
            lock (_replyGate)
            {
                if (_disposed) return Array.Empty<SpotlightSearchItem>();
                id = ++_queryId;
                _pendingReply = completion;
                _pendingReplyId = id;
            }

            try
            {
                _ = vn_everything_provider_compute_search_params(query.AsSpan(), query.Length, limit, out int maxResults, out uint searchFlags);
                string scopedQuery = SpotlightFileVisibility.BuildEverythingQuery(query);
                if (!SendQuery(everythingWindow, replyHwnd, id, scopedQuery, searchFlags, maxResults))
                {
                    IsAvailable = false;
                    return Array.Empty<SpotlightSearchItem>();
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ReplyTimeoutMilliseconds);
                using (timeoutCts.Token.Register(() => completion.TrySetCanceled(timeoutCts.Token)))
                {
                    rows = await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_replyGate)
                {
                    if (ReferenceEquals(_pendingReply, completion)) _pendingReply = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Everything accepted the query but never replied in time.
            IsAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }
        finally
        {
            _queryLock.Release();
        }

        IsAvailable = true;
        if (rows.Count == 0) return Array.Empty<SpotlightSearchItem>();

        var matches = new List<SpotlightSearchItem>(rows.Count);
        var isExcluded = IsExcluded;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var item = ToSearchItem(row.Name, row.Parent, row.IsFolder);
            if (item == null) continue;
            if (isExcluded?.Invoke(item.Target) == true) continue;
            if (!SpotlightFileVisibility.ShouldInclude(item, query)) continue;
            matches.Add(item with { Score = ScoreItem(item, query) });
        }

        if (matches.Count == 0) return Array.Empty<SpotlightSearchItem>();
        matches.Sort(SpotlightSearchService.s_scoreAndTitleComparison);
        int resultCount = Math.Min(limit, matches.Count);
        var results = new SpotlightSearchItem[resultCount];
        matches.CopyTo(0, results, 0, resultCount);
        return results;
    }

    private static double ScoreItem(SpotlightSearchItem item, string query)
    {
        double score = SpotlightRanker.Score(item, query);
        _ = vn_everything_provider_compute_final_score(score, item.Title.Length, out double finalScore);
        return finalScore;
    }

    private static SpotlightSearchItem? ToSearchItem(string name, string parent, bool isFolder)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string fullPath;
        try
        {
            fullPath = parent.Length == 0 ? name : Path.Join(parent, name);
        }
        catch
        {
            return null;
        }

        string ext = Path.GetExtension(name);
        _ = vn_everything_provider_classify_item_kind(isFolder ? (byte)1 : (byte)0, ext.AsSpan(), ext.Length, out int kindInt);
        var kind = (SpotlightResultKind)kindInt;

        string typePrefix = kind switch
        {
            SpotlightResultKind.Folder => "folder",
            SpotlightResultKind.Application => "app",
            _ => "file"
        };

        return new SpotlightSearchItem(
            $"{typePrefix}:{fullPath}",
            kind,
            name,
            fullPath,
            fullPath,
            fullPath);
    }

    private async Task<IntPtr> EnsureReplyWindowAsync(System.Windows.Threading.Dispatcher dispatcher)
    {
        HwndSource? window = _replyWindow;
        if (window != null && window.Handle != IntPtr.Zero) return window.Handle;

        try
        {
            return await dispatcher.InvokeAsync(() =>
            {
                lock (_replyGate)
                {
                    if (_disposed) return IntPtr.Zero;
                    if (_replyWindow != null && _replyWindow.Handle != IntPtr.Zero)
                        return _replyWindow.Handle;

                    var parameters = new HwndSourceParameters("VNotchEverythingIpc")
                    {
                        Width = 0,
                        Height = 0,
                        WindowStyle = 0,
                        ExtendedWindowStyle = 0,
                        ParentWindow = HwndMessage
                    };
                    var source = new HwndSource(parameters);
                    source.AddHook(ReplyWndProc);
                    _replyWindow = source;
                    return source.Handle;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-EVERYTHING", ex, "Could not create IPC reply window");
            return IntPtr.Zero;
        }
    }

    private IntPtr ReplyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmCopyData || lParam == IntPtr.Zero) return IntPtr.Zero;

        TaskCompletionSource<IReadOnlyList<(string, string, bool)>>? completion;
        uint expectedId;
        lock (_replyGate)
        {
            completion = _pendingReply;
            expectedId = _pendingReplyId;
        }
        if (completion == null) return IntPtr.Zero;

        try
        {
            var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if ((uint)data.dwData.ToInt64() != expectedId) return IntPtr.Zero;

            // The buffer is only valid for the duration of this message, so
            // the list must be parsed before returning.
            completion.TrySetResult(ParseReply(data.lpData, data.cbData));
            handled = true;
            return new IntPtr(1);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-EVERYTHING", ex, "Failed to parse IPC reply");
            completion.TrySetResult(Array.Empty<(string, string, bool)>());
            handled = true;
            return new IntPtr(1);
        }
    }

    internal static unsafe IReadOnlyList<(string, string, bool)> ParseReply(IntPtr list, int byteCount)
    {
        if (vn_everything_provider_read_reply_item_count(list, byteCount, out int itemCount) != 0 || itemCount <= 0)
            return Array.Empty<(string, string, bool)>();

        var rows = new List<(string, string, bool)>(itemCount);
        for (int index = 0; index < itemCount; index++)
        {
            if (vn_everything_provider_extract_reply_item(
                    list,
                    byteCount,
                    index,
                    out int nameOffset,
                    out int nameLen,
                    out int pathOffset,
                    out int pathLen,
                    out byte isFolder) != 0 || nameLen <= 0)
            {
                continue;
            }

            string name = new string((char*)(list + nameOffset), 0, nameLen);
            string parent = pathLen > 0 ? new string((char*)(list + pathOffset), 0, pathLen) : string.Empty;
            rows.Add((name, parent, isFolder != 0));
        }

        return rows;
    }

    private static unsafe string ReadBoundedUniString(IntPtr basePtr, int offset, int totalBytes)
    {
        if (vn_everything_provider_parse_bounded_utf16_len(basePtr, offset, totalBytes, out int charLen) != 0 || charLen <= 0)
            return string.Empty;
        return new string((char*)(basePtr + offset), 0, charLen);
    }

    private static bool SendQuery(
        IntPtr everythingWindow,
        IntPtr replyWindow,
        uint queryId,
        string query,
        uint searchFlags,
        int maxResults)
    {
        int stringBytes = (query.Length + 1) * sizeof(char);
        int totalBytes = QueryHeaderBytes + stringBytes;
        IntPtr buffer = Marshal.AllocHGlobal(totalBytes);
        try
        {
            if (vn_everything_provider_pack_ipc_query(
                    replyWindow.ToInt64(),
                    queryId,
                    searchFlags,
                    maxResults,
                    query.AsSpan(),
                    query.Length,
                    buffer,
                    totalBytes,
                    out int writtenBytes) != 0)
            {
                return false;
            }

            var copyData = new CopyDataStruct
            {
                dwData = new IntPtr(CopyDataQueryW),
                cbData = writtenBytes,
                lpData = buffer
            };
            int copyDataSize = Marshal.SizeOf<CopyDataStruct>();
            IntPtr copyDataBuffer = Marshal.AllocHGlobal(copyDataSize);
            try
            {
                Marshal.StructureToPtr(copyData, copyDataBuffer, fDeleteOld: false);
                IntPtr sendResult = Win32Interop.SendMessageTimeout(
                    everythingWindow,
                    WmCopyData,
                    replyWindow,
                    copyDataBuffer,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK,
                    1000,
                    out _);
                return sendResult != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(copyDataBuffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        HwndSource? window;
        lock (_replyGate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingReply?.TrySetCanceled();
            _pendingReply = null;
            window = Interlocked.Exchange(ref _replyWindow, null);
        }
        // A native IPC call may still own the semaphore. Leave this managed
        // semaphore alive for its finally/queued waiters to finish safely.
        if (window != null)
        {
            try
            {
                if (window.Dispatcher.HasShutdownStarted) return;
                void Cleanup()
                {
                    window.RemoveHook(ReplyWndProc);
                    window.Dispose();
                }
                if (window.Dispatcher.CheckAccess()) Cleanup();
                else _ = window.Dispatcher.BeginInvoke(Cleanup);
            }
            catch
            {
                // The dispatcher is tearing down with the process; the OS reclaims
                // the message-only window either way.
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
}
