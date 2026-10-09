using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;

namespace VNotch.Controllers;

/// <summary>Snapshots the Windows clipboard on its STA, then queues local disk work.</summary>
public sealed class ClipboardHistoryController : IDisposable, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Channel<Task<ClipboardCapture>> _captures = Channel.CreateUnbounded<Task<ClipboardCapture>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _consumer;
    private readonly DispatcherTimer _retentionTimer;
    private readonly DispatcherTimer _debounceTimer;
    private uint _lastSequence;
    private uint _ownSequence;
    private uint _pendingSequence;
    private bool _isWritingClipboard;
    private readonly SemaphoreSlim _clipboardWriteGate = new(1, 1);
    private bool _disposed;
    private Task? _disposeTask;
    private string[] _clipboardExportPaths = [];
    private uint _personalClipboardSequence;
    private static readonly TimeSpan ClipboardSettleDelay = TimeSpan.FromMilliseconds(250);
    public ClipboardHistoryStore Store { get; }
    private bool _captureText = true, _captureImages = true, _captureFiles = true;
    public void ApplySettings(NotchSettings settings)
    {
        _captureText = settings.ClipboardCaptureText;
        _captureImages = settings.ClipboardCaptureImages;
        _captureFiles = settings.ClipboardCaptureFiles;
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(settings.ClipboardCaptureDelay);
    }
    public bool IsPaused { get; set; }
    public bool IsDragging { get; private set; }
    public IReadOnlyList<ClipboardEntry> DraggedEntries { get; private set; } = [];
    public const string EntryDragFormat = "VNotch.ClipboardEntries";
    public event Action<ClipboardCapture, long>? ApprovalRequired;
    public event Action<string>? StatusChanged;
    public event Action? DropImported;
    public event Action? PersonalLocked;
    public event Action<double>? ImportProgress;

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

    public ClipboardHistoryController(Dispatcher dispatcher, ClipboardHistoryStore? store = null)
    {
        _dispatcher = dispatcher;
        Store = store ?? new ClipboardHistoryStore();
        _consumer = ConsumeAsync();
        _retentionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += RetentionTick;
        _retentionTimer.Start();
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = ClipboardSettleDelay };
        _debounceTimer.Tick += DebounceTick;
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    private async Task ConsumeAsync()
    {
        try
        {
            try { await Store.InitializeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Report("clipboard.saveError"); }
            await foreach (var pending in _captures.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { await ImportAsync(await pending.ConfigureAwait(false)).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
                    Report("clipboard.saveError");
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { RuntimeLog.Error("CLIPBOARD", ex.GetType().Name); Report("clipboard.error"); }
    }

    public void NotifyClipboardUpdated()
    {
        if (_disposed || _isWritingClipboard) return;
        uint sequence = GetClipboardSequenceNumber();
        if (VNotch.Services.Translation.SelectionClipboardCapture.IgnoreClipboardCapture(sequence)) return;
        if (sequence != 0 && sequence != _ownSequence)
        {
            _personalClipboardSequence = 0;
            ReleaseClipboardExports();
        }
        if (IsPaused) return;
        if (sequence == 0 || sequence == _lastSequence || sequence == _ownSequence) return;
        _pendingSequence = sequence;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void DebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        if (_disposed || IsPaused || _isWritingClipboard) return;
        uint sequence = _pendingSequence;
        if (VNotch.Services.Translation.SelectionClipboardCapture.IgnoreClipboardCapture(sequence)) return;
        if (sequence == 0 || sequence == _lastSequence || sequence == _ownSequence) return;
        _lastSequence = sequence;
        CaptureAsync(sequence).SafeFireAndForget("CLIPBOARD-CAPTURE");
    }

    internal static bool ShouldIgnoreClipboardData(IDataObject data)
    {
        // Check privacy markers before reading any text, image, or file payload.
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)
            || data.GetDataPresent("Clipboard Viewer Ignore", false)) return true;
        const string historyFormat = "CanIncludeInClipboardHistory";
        if (!data.GetDataPresent(historyFormat, false)) return false;

        // Windows defines this format as a serialized, little-endian DWORD.
        // WPF exposes native registered formats as streams; in-process producers
        // can also supply byte arrays or integers. Unreadable markers fail closed.
        var value = data.GetData(historyFormat, false);
        if (value is int signed) return signed == 0;
        if (value is uint unsigned) return unsigned == 0;
        if (value is byte[] bytes)
            return bytes.Length < sizeof(uint) || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0;
        if (value is not Stream stream || !stream.CanRead || !stream.CanSeek) return true;

        long position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> flag = stackalloc byte[sizeof(uint)];
            stream.ReadExactly(flag);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(flag) == 0;
        }
        catch (EndOfStreamException) { return true; }
        finally { stream.Position = position; }
    }

    private async Task CaptureAsync(uint sequence)
    {
        for (int retry = 0; ; retry++)
        {
            if (_disposed || IsPaused || _isWritingClipboard || sequence == _ownSequence || GetClipboardSequenceNumber() != sequence) return;
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data == null || ShouldIgnoreClipboardData(data)) return;
                string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string ?? "";
                string[] paths = data.GetData(DataFormats.FileDrop) as string[] ?? [];
                bool isScreenshotTempFile = paths.Length == 1 && IsTemporaryScreenshotPath(paths[0]);
                BitmapSource? image = (paths.Length == 0 || isScreenshotTempFile) ? data.GetData(DataFormats.Bitmap) as BitmapSource : null;
                if (isScreenshotTempFile && image != null)
                {
                    paths = [];
                }
                // Filter the original payload type; disabled file/image captures must not
                // fall through into an incidental text representation of the same copy.
                if (paths.Length > 0 && !_captureFiles || image != null && !_captureImages) return;
                if (!_captureText) text = "";
                if (image != null)
                {
                    image = image.Clone();
                    image.Freeze();
                }
                var source = ClipboardSource();
                var capture = new ClipboardCapture
                {
                    Text = text,
                    Html = _captureText ? data.GetData(DataFormats.Html) as string ?? "" : "",
                    Rtf = _captureText ? data.GetData(DataFormats.Rtf) as string ?? "" : "",
                    FilePaths = paths,
                    SourceApp = source.Name,
                    SourceExecutable = source.Executable
                };
                if (GetClipboardSequenceNumber() != sequence) return;
                if (paths.Length != 0 || image != null || text.Length != 0 || capture.Html.Length != 0 || capture.Rtf.Length != 0)
                    _captures.Writer.TryWrite(image == null ? Task.FromResult(capture) : EncodeImageAsync(capture, image));
                return;
            }
            catch (ExternalException) when (retry < ClipboardRetry.RetryCount)
            {
                await Task.Delay(ClipboardRetry.DelayMilliseconds(retry));
            }
            catch (Exception ex)
            {
                // Passive monitoring must not interrupt the user with a copy error.
                // A later clipboard update starts a fresh capture automatically.
                RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
                return;
            }
        }
    }

    private static Task<ClipboardCapture> EncodeImageAsync(ClipboardCapture capture, BitmapSource image) => Task.Run(() =>
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return capture with { ImagePng = stream.ToArray() };
    });

    internal async Task ImportDropAsync(System.Windows.IDataObject data, string? category = null)
    {
        try
        {
            var payload = ClipboardDropReader.Read(data);
            if (payload != null) await ImportDropPayloadAsync(payload, category);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
        { Report("clipboard.readError"); }
    }

    internal async Task ImportDropPayloadAsync(ClipboardDropReader.Payload payload, string? category = null)
    {
        try
        {
            var capture = await ClipboardDropReader.ResolveAsync(payload);
            await ImportAsync(capture with { TargetCategory = category }, notifyDrop: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException or
            NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        { Report("clipboard.readError"); }
    }

    public async Task ImportAsync(ClipboardCapture capture, bool approved = false, bool notifyDrop = false)
    {
        // Keep this choice on the capture so approving a large drop retains
        // one card per file. Passive clipboard captures still represent one copy.
        if (notifyDrop && capture.FilePaths.Length > 1) capture = capture with { SeparateFiles = true };
        try
        {
            await Store.InitializeAsync();
            if (capture.TargetCategory == "Personal" && !Store.IsPersonalUnlocked)
                throw new InvalidOperationException("Unlock Personal before importing.");
            var progress = new Progress<double>(p => Dispatch(() => ImportProgress?.Invoke(p)));
            var result = await Store.ImportAsync(capture, approved, progress);
            Dispatch(() => ImportProgress?.Invoke(1.0));
            IReadOnlyList<ClipboardEntry> entries = result.Entries.Count != 0 ? result.Entries : result.Entry != null ? [result.Entry] : [];
            if (capture.TargetCategory is string category)
                foreach (var entry in entries) await AssignCategoryAsync(entry, category);
            if (result.NeedsApproval) Dispatch(() => ApprovalRequired?.Invoke(capture, result.ByteSize));
            if ((notifyDrop || capture.SeparateFiles) && entries.Count != 0)
                Dispatch(() => DropImported?.Invoke());
            if (!result.NeedsApproval && result.FailedCount > 0)
                Dispatch(() => StatusChanged?.Invoke(Loc.Get("clipboard.partialImport", result.ImportedCount,
                    result.ImportedCount + result.FailedCount, result.FailedCount)));
        }
        catch (ClipboardImportLimitException)
        {
            Dispatch(() => ImportProgress?.Invoke(1.0));
            Report("clipboard.tooManyFiles");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dispatch(() => ImportProgress?.Invoke(1.0));
            Report("clipboard.saveError");
        }
    }

    public async Task CopyAsync(ClipboardEntry entry)
    {
        string[] exported = [];
        bool written = false;
        try
        {
            var content = await Store.GetContentAsync(entry.Id);
            entry = content.Entry;
            var data = new DataObject();
            if (content.Files.Length != 0)
            {
                exported = await Store.ExportFilesAsync(entry.Id);
                var collection = new StringCollection(); collection.AddRange(exported);
                data.SetFileDropList(collection);
            }
            else if (entry.Kind == ClipboardKind.Image)
            {
                byte[]? png = await Store.GetImageAsync(entry.Id);
                if (png == null) return;
                try
                {
                    using var stream = new MemoryStream(png);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    data.SetImage(bitmap);
                }
                finally { if (entry.IsPersonal) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
            }
            else
            {
                SetTextFormats(data, content);
            }
            var current = Store.GetEntry(entry.Id) ?? throw new InvalidOperationException("Clipboard item is unavailable.");
            await WriteClipboardAsync(data, entry.IsPersonal || current.IsPersonal);
            _clipboardExportPaths = exported;
            written = true;
        }
        finally { if (!written && exported.Length > 0) await Store.ReleasePersonalExportsAsync(exported); }
    }

    private async Task<(DataObject Data, bool Personal)> CreateSelectionDataAsync(IReadOnlyList<ClipboardEntry> entries, List<string> paths)
    {
        var texts = new List<string>();
        ClipboardContent? singleText = null;
        bool personal = false;
        foreach (var entry in entries)
        {
            var current = Store.GetEntry(entry.Id) ?? throw new KeyNotFoundException("Clipboard item is unavailable.");
            personal |= current.IsPersonal;
            if (entry.Kind is ClipboardKind.File or ClipboardKind.Image)
                paths.AddRange(await Store.ExportFilesAsync(entry.Id));
            else
            {
                var content = await Store.GetContentAsync(entry.Id);
                texts.Add(content.Text ?? string.Empty);
                if (entries.Count == 1) singleText = content;
            }
            personal |= Store.GetEntry(entry.Id)?.IsPersonal != false;
        }
        var data = new DataObject();
        if (paths.Count > 0) { var files = new StringCollection(); files.AddRange(paths.ToArray()); data.SetFileDropList(files); }
        if (singleText != null) SetTextFormats(data, singleText);
        else if (texts.Count > 0) data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine + Environment.NewLine, texts));
        return (data, personal);
    }

    internal static void SetTextFormats(DataObject data, ClipboardContent content)
    {
        // SetText rejects empty strings; rich-text-only clips still need their original formats.
        data.SetData(DataFormats.UnicodeText, content.Text ?? string.Empty);
        if (!string.IsNullOrEmpty(content.Html)) data.SetData(DataFormats.Html, content.Html);
        if (!string.IsNullOrEmpty(content.Rtf)) data.SetData(DataFormats.Rtf, content.Rtf);
    }

    public async Task CopyManyAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries.Count == 0) return;
        if (entries.Count == 1) { await CopyAsync(entries[0]); return; }
        var paths = new List<string>();
        bool written = false;
        try
        {
            var selection = await CreateSelectionDataAsync(entries, paths);
            await WriteClipboardAsync(selection.Data, selection.Personal);
            _clipboardExportPaths = paths.ToArray();
            written = true;
        }
        finally { if (!written) await Store.ReleasePersonalExportsAsync(paths); }
    }

    private async Task WriteClipboardAsync(DataObject data, bool personal)
    {
        if (personal) data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
        await _clipboardWriteGate.WaitAsync();
        try
        {
            _isWritingClipboard = true;
            await ClipboardRetry.RunAsync(() =>
            {
                var previousExports = _clipboardExportPaths;
                System.Windows.Clipboard.SetDataObject(data, true);
                _ownSequence = GetClipboardSequenceNumber();
                _personalClipboardSequence = personal ? _ownSequence : 0;
                _clipboardExportPaths = [];
                if (previousExports.Length > 0) _ = ReleaseClipboardExportsAsync(previousExports);
            }, () =>
            {
                if (_disposed || personal && !Store.IsPersonalUnlocked)
                    throw new InvalidOperationException("Clipboard is unavailable or Personal is locked.");
            });
        }
        finally
        {
            _isWritingClipboard = false;
            _clipboardWriteGate.Release();
            if (!_disposed) NotifyClipboardUpdated();
        }
    }

    public Task DragAsync(FrameworkElement source, ClipboardEntry entry) => DragManyAsync(source, [entry]);

    public async Task DragManyAsync(FrameworkElement source, IReadOnlyList<ClipboardEntry> entries)
    {
        if (IsDragging || entries.Count == 0) return;
        IsDragging = true;
        DraggedEntries = entries.ToArray();
        var paths = new List<string>();
        try
        {
            var selection = await CreateSelectionDataAsync(entries, paths);
            var data = selection.Data;
            data.SetData(EntryDragFormat, true);
            bool personal = selection.Personal;
            if (_disposed || personal && !Store.IsPersonalUnlocked) return;
            if (personal) data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        finally
        {
            IsDragging = false; DraggedEntries = [];
            await Store.ReleasePersonalExportsAsync(paths);
        }
    }

    public async Task AssignCategoryAsync(ClipboardEntry entry, string category)
    {
        if (category == "Personal") { await Store.MovePersonalAsync(entry.Id, true); return; }
        if (entry.IsPersonal) await Store.MovePersonalAsync(entry.Id, false);
        if (category == "Pin") await Store.UpdateAsync(entry.Id, pinned: true);
        else if (category == "Archive") await Store.UpdateAsync(entry.Id, archived: true);
        else if (ClipboardClassifier.GroupNames.Contains(category))
            await Store.UpdateAsync(entry.Id, groups: entry.Groups.Append(category).Distinct().ToArray(), archived: false);
        else if (category == "All") await Store.UpdateAsync(entry.Id, archived: false);
    }

    public async Task LockPersonalAsync()
    {
        var pending = Store.LockPersonalAsync();
        ClearPersonalClipboard();
        PersonalLocked?.Invoke();
        try { await pending; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { Report("clipboard.error"); }
    }
    private async void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (!_disposed && e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            await _dispatcher.InvokeAsync(LockPersonalAsync).Task.Unwrap();
    }
    /// <summary>
    /// Locks the Personal Vault when the system suspends (lid close / Modern Standby / hibernate).
    /// Windows fires <see cref="Microsoft.Win32.PowerModes.Suspend"/> instead of SessionLock in these cases.
    /// </summary>
    private async void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (!_disposed && e.Mode == PowerModes.Suspend)
            await _dispatcher.InvokeAsync(LockPersonalAsync).Task.Unwrap();
    }
    private async void RetentionTick(object? sender, EventArgs e)
    {
        try { await Store.PruneAsync(DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { Report("clipboard.error"); }
    }
    private static bool IsTemporaryScreenshotPath(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith('{') && name.EndsWith("}.png", StringComparison.OrdinalIgnoreCase))
            return true;
        string dir = Path.GetDirectoryName(path) ?? "";
        return dir.Contains("ScreenClip", StringComparison.OrdinalIgnoreCase)
            || dir.Contains("Screenshots", StringComparison.OrdinalIgnoreCase);
    }
    private static (string Name, string Executable) ClipboardSource()
    {
        try
        {
            IntPtr owner = GetClipboardOwner();
            Win32Interop.GetWindowThreadProcessId(owner != IntPtr.Zero ? owner : Win32Interop.GetForegroundWindow(), out uint processId);
            using var process = Process.GetProcessById((int)processId);
            string executable = "";
            try { executable = process.MainModule?.FileName ?? ""; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            return (process.ProcessName, executable);
        }
        catch { return ("", ""); }
    }
    private void Report(string key) => Dispatch(() => StatusChanged?.Invoke(Loc.Get(key)));
    private void ReleaseClipboardExports()
    {
        var paths = _clipboardExportPaths;
        _clipboardExportPaths = [];
        if (paths.Length > 0) _ = ReleaseClipboardExportsAsync(paths);
    }
    private async Task ReleaseClipboardExportsAsync(string[] paths)
    {
        try { await Store.ReleasePersonalExportsAsync(paths); }
        catch (ObjectDisposedException) { }
    }
    private void ClearPersonalClipboard()
    {
        if (_personalClipboardSequence != 0 && GetClipboardSequenceNumber() == _personalClipboardSequence)
        {
            try { System.Windows.Clipboard.Clear(); }
            catch (ExternalException) { Report("clipboard.error"); }
        }
        _personalClipboardSequence = 0;
        ReleaseClipboardExports();
    }
    private void Dispatch(Action action) { if (!_disposed) _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposeTask != null) return new ValueTask(_disposeTask);
        ClearPersonalClipboard();
        _disposed = true;
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _retentionTimer.Stop();
        _retentionTimer.Tick -= RetentionTick;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTick;
        _captures.Writer.TryComplete();
        return new ValueTask(_disposeTask = DrainAsync());
    }

    private async Task DrainAsync()
    {
        try { await _consumer.ConfigureAwait(false); }
        finally { await Store.DisposeAsync().ConfigureAwait(false); }
    }
}
