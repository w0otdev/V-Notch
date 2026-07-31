using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

/// <summary>
/// Queries the Everything search engine (voidtools) over its WM_COPYDATA IPC
/// protocol, the same engine Flow Launcher uses for instant global file
/// search. Requires Everything to be running; when it is not, the provider
/// reports unavailable and returns nothing so the Windows Search index
/// provider remains the fallback.
/// </summary>
internal sealed class EverythingSearchProvider : ISpotlightProvider, IDisposable
{
    private const string EverythingIpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const int CopyDataQueryW = 2;
    private const int WmCopyData = 0x004A;
    private const uint MatchPath = 0x00000004;
    private const int QueryHeaderBytes = 20;
    // EVERYTHING_IPC_LISTW header: totfolders, totfiles, totitems, numfolders,
    // numfiles, numitems, offset — 7 DWORDs before the item array.
    private const int ListHeaderBytes = 28;
    private const int ListNumItemsOffset = 20;
    private const int ItemBytes = 12;
    private const int ItemFolderFlag = 0x00000001;
    private const int MaxParsedItems = 512;
    private const int ReplyTimeoutMilliseconds = 900;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly object _replyGate = new();
    private HwndSource? _replyWindow;
    private uint _queryId;
    private TaskCompletionSource<IReadOnlyList<(string Name, string Parent, bool IsFolder)>>? _pendingReply;
    private uint _pendingReplyId;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Everything answers from its in-memory NTFS index in single-digit
    /// milliseconds, so it is cheap enough to run on every keystroke.
    /// </summary>
    public bool IsInstant => true;

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0 || limit <= 0) return Array.Empty<SpotlightSearchItem>();

        IntPtr everythingWindow = FindWindowW(EverythingIpcWindowClass, null);
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
                id = ++_queryId;
                _pendingReply = completion;
                _pendingReplyId = id;
            }

            try
            {
                int maxResults = Math.Clamp(limit * 8, 100, MaxParsedItems);
                // Matching the path too lets "docs\report" style queries work,
                // mirroring Flow Launcher's Everything plugin behavior.
                uint searchFlags = query.Contains('\\') || query.Contains('/') ? MatchPath : 0;
                if (!SendQuery(everythingWindow, replyHwnd, id, query, searchFlags, maxResults))
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
        return rows
            .Select(row => ToSearchItem(row.Name, row.Parent, row.IsFolder))
            .Where(item => item != null)
            .Select(item => item! with { Score = ScoreItem(item!, query) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static double ScoreItem(SpotlightSearchItem item, string query)
    {
        double score = SpotlightRanker.Score(item, query);
        if (score > 0) return score;

        // Everything matched the item (possibly on its path); keep it visible
        // below lexical title matches instead of dropping it.
        return Math.Max(120, 260 - item.Title.Length);
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".msc", ".cpl", ".com", ".ps1", ".appref-ms", ".lnk"
    };

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
        bool isExec = !isFolder && ExecutableExtensions.Contains(ext);
        SpotlightResultKind kind = isFolder
            ? SpotlightResultKind.Folder
            : (isExec ? SpotlightResultKind.Application : SpotlightResultKind.File);

        return new SpotlightSearchItem(
            $"{(isFolder ? "folder" : (isExec ? "app" : "file"))}:{fullPath}",
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

    internal static IReadOnlyList<(string, string, bool)> ParseReply(IntPtr list, int byteCount)
    {
        if (list == IntPtr.Zero || byteCount < ListHeaderBytes)
            return Array.Empty<(string, string, bool)>();

        // numitems counts the entries actually present in this list; the
        // preceding fields describe the whole index and the full result set.
        int itemCount = Marshal.ReadInt32(list, ListNumItemsOffset);
        itemCount = Math.Clamp(itemCount, 0, MaxParsedItems);
        var rows = new List<(string, string, bool)>(itemCount);

        for (int index = 0; index < itemCount; index++)
        {
            int itemOffset = ListHeaderBytes + index * ItemBytes;
            if (itemOffset + ItemBytes > byteCount) break;

            int flags = Marshal.ReadInt32(list, itemOffset);
            int nameOffset = Marshal.ReadInt32(list, itemOffset + 4);
            int pathOffset = Marshal.ReadInt32(list, itemOffset + 8);
            if (nameOffset < 0 || nameOffset >= byteCount) continue;
            if (pathOffset < 0 || pathOffset >= byteCount) continue;

            string name = Marshal.PtrToStringUni(list + nameOffset) ?? string.Empty;
            string parent = Marshal.PtrToStringUni(list + pathOffset) ?? string.Empty;
            if (name.Length == 0) continue;

            rows.Add((name, parent, (flags & ItemFolderFlag) != 0));
        }

        return rows;
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
            // EVERYTHING_IPC_QUERYW: reply_hwnd is documented as 32 bits even
            // on x64, followed by reply message id, flags, offset, max count.
            Marshal.WriteInt32(buffer, 0, unchecked((int)(uint)replyWindow.ToInt64()));
            Marshal.WriteInt32(buffer, 4, unchecked((int)queryId));
            Marshal.WriteInt32(buffer, 8, unchecked((int)searchFlags));
            Marshal.WriteInt32(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 16, maxResults);
            char[] characters = query.ToCharArray();
            Marshal.Copy(characters, 0, buffer + QueryHeaderBytes, characters.Length);
            Marshal.WriteInt16(buffer, QueryHeaderBytes + characters.Length * sizeof(char), 0);

            var copyData = new CopyDataStruct
            {
                dwData = new IntPtr(CopyDataQueryW),
                cbData = totalBytes,
                lpData = buffer
            };
            int copyDataSize = Marshal.SizeOf<CopyDataStruct>();
            IntPtr copyDataBuffer = Marshal.AllocHGlobal(copyDataSize);
            try
            {
                Marshal.StructureToPtr(copyData, copyDataBuffer, fDeleteOld: false);
                IntPtr sendResult = SendMessageTimeoutW(
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
        _queryLock.Dispose();
        HwndSource? window = Interlocked.Exchange(ref _replyWindow, null);
        if (window == null) return;
        try
        {
            if (window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.Invoke(() =>
            {
                window.RemoveHook(ReplyWndProc);
                window.Dispose();
            });
        }
        catch
        {
            // The dispatcher is tearing down with the process; the OS reclaims
            // the message-only window either way.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
