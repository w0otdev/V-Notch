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
    private readonly Lazy<Task<Dispatcher>> _clipboardReader = new(StartClipboardReader);

    private static Task<Dispatcher> StartClipboardReader()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        { Name = "VNotch clipboard reader", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
    private readonly Func<byte[], Task<BitmapSource>> _decodeOriginalImage;
    internal const int MaxPendingCaptures = 8;
    private static readonly SemaphoreSlim ImageEncodingGate = new(1, 1);
    private readonly SemaphoreSlim _captureSlots = new(MaxPendingCaptures, MaxPendingCaptures);
    private readonly Channel<Task<ClipboardCapture>> _captures = Channel.CreateBounded<Task<ClipboardCapture>>(
        new BoundedChannelOptions(MaxPendingCaptures) { SingleReader = true, SingleWriter = true });
    private readonly Task _consumer;
    private readonly UiScheduledTimer _retentionTimer;
    private readonly UiScheduledTimer _debounceTimer;
    private uint _lastSequence;
    private uint _ownSequence;
    private uint _pendingSequence;
    private bool _isWritingClipboard;
    private readonly SemaphoreSlim _clipboardWriteGate = new(1, 1);
    private volatile bool _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private string[] _clipboardExportPaths = [];
    private uint _personalClipboardSequence;
    private static readonly TimeSpan ClipboardSettleDelay = TimeSpan.FromMilliseconds(250);
    public ClipboardHistoryStore Store { get; }
    private bool _captureText = true, _captureImages = true, _captureFiles = true;
    public void ApplySettings(NotchSettings settings)
    {
        Store.AutoDeleteSensitive = settings.ClipboardAutoDeleteSensitive;
        _retentionTimer.Interval = settings.ClipboardAutoDeleteSensitive ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(1);
        _captureText = settings.ClipboardCaptureText;
        _captureImages = settings.ClipboardCaptureImages;
        _captureFiles = settings.ClipboardCaptureFiles;
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(settings.ClipboardCaptureDelay);
    }
    public bool IsPaused { get; set; }
    public bool IsDragging { get; private set; }
    public IReadOnlyList<ClipboardEntry> DraggedEntries { get; private set; } = [];
    public const string EntryDragFormat = "VNotch.ClipboardEntries";
    public const int MaxRichTextCaptureChars = 2 * 1024 * 1024;
    public const int MaxTextCaptureChars = 10 * 1024 * 1024;
    public event Action<ClipboardCapture, long>? ApprovalRequired;
    public event Action<string>? StatusChanged;
    public event Action? DropImported;
    public event Action? PersonalLocked;
    public event Action<double>? ImportProgress;

    private static uint GetClipboardSequenceNumber() => Win32Interop.GetClipboardSequenceNumber();
    private static IntPtr GetClipboardOwner() => Win32Interop.GetClipboardOwner();
    private static bool IsPersonalCategory(string? category) =>
        string.Equals(category, "Personal", StringComparison.OrdinalIgnoreCase);

    public ClipboardHistoryController(Dispatcher dispatcher, ClipboardHistoryStore? store = null)
        : this(dispatcher, store, input => ClipboardOriginalImage.DecodeAsync(input))
    {
    }

    internal ClipboardHistoryController(Dispatcher dispatcher, ClipboardHistoryStore? store,
        Func<byte[], Task<BitmapSource>> decodeOriginalImage)
    {
        _dispatcher = dispatcher;
        _decodeOriginalImage = decodeOriginalImage;
        Store = store ?? new ClipboardHistoryStore();
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && System.Windows.Clipboard.ContainsFileDropList())
            {
                _clipboardExportPaths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
                _ownSequence = GetClipboardSequenceNumber();
            }
        }
        catch (ExternalException) { /* An unavailable clipboard can be retried on its next update. */ }
        _consumer = ConsumeAsync();
        _retentionTimer = new UiScheduledTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += RetentionTick;
        _retentionTimer.Start();
        _debounceTimer = new UiScheduledTimer(DispatcherPriority.Normal, dispatcher) { Interval = ClipboardSettleDelay };
        _debounceTimer.Tick += DebounceTick;
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await Store.KeepClipboardExportsAsync(_clipboardExportPaths).ConfigureAwait(false);
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
                finally { _captureSlots.Release(); }
            }
        }
        catch (ObjectDisposedException) { /* Intentionally ignored. */ }
        catch (Exception ex) { RuntimeLog.Error("CLIPBOARD", ex.GetType().Name); Report("clipboard.error"); }
    }

    public void NotifyClipboardUpdated()
    {
        if (_disposed || _isWritingClipboard) return;
        uint sequence = GetClipboardSequenceNumber();
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
        if (!_captureSlots.Wait(0)) { Report("clipboard.saveError"); return; }
        bool queued = false;
        try
        {
            for (int retry = 0; retry <= ClipboardRetry.RetryCount; retry++)
            {
                if (_disposed || IsPaused || _isWritingClipboard || sequence == _ownSequence || GetClipboardSequenceNumber() != sequence) return;
                try
                {
                    var policy = (_captureText, _captureImages, _captureFiles);
                    var reader = await _clipboardReader.Value;
                    var snapshot = await reader.InvokeAsync(() =>
                        ReadSnapshot(sequence, policy._captureText, policy._captureImages, policy._captureFiles));
                    if (snapshot is not { } result || _disposed || IsPaused || _isWritingClipboard ||
                        sequence == _ownSequence || policy != (_captureText, _captureImages, _captureFiles)) return;
                    queued = _captures.Writer.TryWrite(result.Image == null
                        ? Task.FromResult(result.Capture) : EncodeImageAsync(result.Capture, result.Image));
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
        finally { if (!queued) _captureSlots.Release(); }
    }

    private (ClipboardCapture Capture, BitmapSource? Image)? ReadSnapshot(uint sequence, bool textEnabled, bool imagesEnabled, bool filesEnabled)
    {
        if (GetClipboardSequenceNumber() != sequence || !(textEnabled || imagesEnabled || filesEnabled)) return null;
        var data = System.Windows.Clipboard.GetDataObject();
        if (data == null || ShouldIgnoreClipboardData(data)) return null;
        bool hasFiles = data.GetDataPresent(DataFormats.FileDrop);
        if (hasFiles && !filesEnabled && !imagesEnabled) return null;
        string[] paths = hasFiles
            ? data.GetData(DataFormats.FileDrop) as string[] ?? []
            : [];
        bool isScreenshotTempFile = paths.Length == 1 && IsTemporaryScreenshotPath(paths[0]);
        bool hasImage = (paths.Length == 0 || isScreenshotTempFile) && data.GetDataPresent(DataFormats.Bitmap);
        if (hasImage && !imagesEnabled) return null;
        BitmapSource? image = hasImage ? data.GetData(DataFormats.Bitmap) as BitmapSource : null;
        if (isScreenshotTempFile && image != null)
        {
            paths = [];
        }
        // Filter the original payload type; disabled file/image captures must not
        // fall through into an incidental text representation of the same copy.
        if (paths.Length > 0 && !filesEnabled) return null;
        string text = textEnabled
            ? (data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null)
                ?? (data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null)
                ?? ""
            : "";
        if (text.Length > MaxTextCaptureChars) { Report("clipboard.readError"); return null; }
        if (image != null)
        {
            if ((long)image.PixelWidth * image.PixelHeight > 32_000_000) { Report("clipboard.readError"); return null; }
            if (!image.IsFrozen) { image = image.Clone(); image.Freeze(); }
        }
        string html = "";
        string rtf = "";
        if (textEnabled && text.Length <= MaxRichTextCaptureChars)
        {
            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string rawHtml && rawHtml.Length <= MaxRichTextCaptureChars)
                html = rawHtml;
            if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rawRtf && rawRtf.Length <= MaxRichTextCaptureChars)
                rtf = rawRtf;
        }
        var source = ClipboardSource();
        var capture = new ClipboardCapture
        {
            Text = text,
            Html = html,
            Rtf = rtf,
            FilePaths = paths,
            SourceApp = source.Name,
            SourceExecutable = source.Executable
        };
        if (GetClipboardSequenceNumber() != sequence ||
            (paths.Length == 0 && image == null && text.Length == 0 && html.Length == 0 && rtf.Length == 0)) return null;
        return (capture, image);
    }

    internal static async Task<ClipboardCapture> EncodeImageAsync(ClipboardCapture capture, BitmapSource image)
    {
        // Keep capture order and every admitted snapshot, but avoid overlapping
        // full-size WIC encoders and their temporary output buffers.
        await ImageEncodingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                byte[] png;
                using (var stream = new NativeBufferStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                    png = stream.ReadBuffered(static bytes => bytes.ToArray());
                }
                // Release encoded scratch before fingerprinting allocates pixels.
                return capture with
                {
                    ImagePng = png,
                    ImagePixelFingerprint = ClipboardHistoryStore.FingerprintPixels(image)
                };
            }).ConfigureAwait(false);
        }
        finally { ImageEncodingGate.Release(); }
    }

    internal async Task ImportDropAsync(System.Windows.IDataObject data, string? category = null)
    {
        if (IsPersonalCategory(category) && !Store.IsPersonalUnlocked) { Report("clipboard.saveError"); return; }
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
        if (IsPersonalCategory(category) && !Store.IsPersonalUnlocked) { Report("clipboard.saveError"); return; }
        try
        {
            var capture = await ClipboardDropReader.ResolveAsync(payload);
            await ImportAsync(capture with { TargetCategory = IsPersonalCategory(category) ? "Personal" : category }, notifyDrop: true);
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
        if (IsPersonalCategory(capture.TargetCategory) && capture.TargetCategory != "Personal")
            capture = capture with { TargetCategory = "Personal" };
        if (capture.Html.Length > MaxRichTextCaptureChars || capture.Rtf.Length > MaxRichTextCaptureChars)
        {
            capture = capture with
            {
                Html = capture.Html.Length <= MaxRichTextCaptureChars ? capture.Html : "",
                Rtf = capture.Rtf.Length <= MaxRichTextCaptureChars ? capture.Rtf : ""
            };
        }
        try
        {
            await Store.InitializeAsync();
            if (capture.TargetCategory == "Personal" && !Store.IsPersonalUnlocked)
                throw new InvalidOperationException("Unlock Personal before importing.");
            var progress = new Progress<double>(p => Dispatch(() => ImportProgress?.Invoke(p)));
            var result = await Store.ImportAsync(capture, approved, progress);
            Dispatch(() => ImportProgress?.Invoke(1.0));
            IReadOnlyList<ClipboardEntry> singleOrEmpty = result.Entry != null ? [result.Entry] : [];
            IReadOnlyList<ClipboardEntry> entries = result.Entries.Count != 0 ? result.Entries : singleOrEmpty;
            if (capture.TargetCategory is string category && entries.Count > 0)
                await AssignCategoryManyAsync(entries, category);
            if (result.NeedsApproval && !_disposed)
            {
                // Backpressure the capture consumer while the UI is busy: a pending dispatcher
                // callback owns the entire payload just like a queued approval does.
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed) ApprovalRequired?.Invoke(capture, result.ByteSize);
                    }, DispatcherPriority.Background).Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted) { }
            }
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
                    var bitmap = await _decodeOriginalImage(png);
                    var latest = Store.GetEntry(entry.Id) ?? throw new InvalidOperationException("Clipboard item is unavailable.");
                    if (_disposed || (entry.IsPersonal || latest.IsPersonal) && !Store.IsPersonalUnlocked)
                        throw new InvalidOperationException("Clipboard is unavailable or Personal is locked.");
                    data.SetImage(bitmap);
                }
                finally { if (entry.IsPersonal || Store.GetEntry(entry.Id)?.IsPersonal != false) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
            }
            else
            {
                SetTextFormats(data, content);
            }
            var current = Store.GetEntry(entry.Id) ?? throw new InvalidOperationException("Clipboard item is unavailable.");
            await WriteClipboardAsync(data, entry.IsPersonal || current.IsPersonal, exported, entry.IsSensitive || current.IsSensitive);
            // _clipboardExportPaths is set inside WriteClipboardAsync under _clipboardWriteGate.
            written = true;
        }
        finally { if (!written && exported.Length > 0) await Store.ReleasePersonalExportsAsync(exported); }
    }

    private async Task<(DataObject Data, bool Personal)> CreateSelectionDataAsync(IReadOnlyList<ClipboardEntry> entries, List<string> paths)
    {
        var texts = new List<string>();
        ClipboardContent? singleText = null;
        bool personal = false;
        bool sensitive = false;
        foreach (var entry in entries)
        {
            var current = Store.GetEntry(entry.Id) ?? throw new KeyNotFoundException("Clipboard item is unavailable.");
            personal |= entry.IsPersonal | current.IsPersonal;
            sensitive |= entry.IsSensitive | current.IsSensitive;
            if (entry.Kind is ClipboardKind.File or ClipboardKind.Image)
                paths.AddRange(await Store.ExportFilesAsync(entry.Id));
            else
            {
                var content = await Store.GetContentAsync(entry.Id);
                texts.Add(content.Text ?? string.Empty);
                if (entries.Count == 1) singleText = content;
            }
            var latest = Store.GetEntry(entry.Id) ?? throw new KeyNotFoundException("Clipboard item is unavailable.");
            personal |= latest.IsPersonal;
            sensitive |= latest.IsSensitive;
        }
        var data = new DataObject();
        if (paths.Count > 0) { var files = new StringCollection(); files.AddRange(paths.ToArray()); data.SetFileDropList(files); }
        if (singleText != null) SetTextFormats(data, singleText);
        else if (texts.Count > 0) data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine + Environment.NewLine, texts));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (personal || sensitive) SetPrivacyClipboardMarkers(data);
        return (data, personal);
    }

    internal static void SetTextFormats(DataObject data, ClipboardContent content)
    {
        // SetText rejects empty strings; rich-text-only clips still need their original formats.
        data.SetData(DataFormats.UnicodeText, content.Text ?? string.Empty);
        if (!string.IsNullOrEmpty(content.Html)) data.SetData(DataFormats.Html, content.Html);
        if (!string.IsNullOrEmpty(content.Rtf)) data.SetData(DataFormats.Rtf, content.Rtf);
    }

    internal static void SetPrivacyClipboardMarkers(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1, 0, 0, 0]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
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
            await WriteClipboardAsync(selection.Data, selection.Personal, paths.ToArray());
            // _clipboardExportPaths is set inside WriteClipboardAsync under _clipboardWriteGate.
            written = true;
        }
        finally { if (!written) await Store.ReleasePersonalExportsAsync(paths); }
    }

    private async Task WriteClipboardAsync(DataObject data, bool personal, string[]? newExports = null, bool sensitive = false)
    {
        if (personal || sensitive) SetPrivacyClipboardMarkers(data);
        await _clipboardWriteGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isWritingClipboard = true;
            await ClipboardRetry.RunAsync(() =>
            {
                var previousExports = _clipboardExportPaths;
                System.Windows.Clipboard.SetDataObject(data, true);
                _ownSequence = GetClipboardSequenceNumber();
                _personalClipboardSequence = personal ? _ownSequence : 0;
                _clipboardExportPaths = newExports ?? [];
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
            if (personal) SetPrivacyClipboardMarkers(data);
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
        if (IsPersonalCategory(category)) { await Store.MovePersonalAsync(entry.Id, true); return; }
        if (entry.IsPersonal) await Store.MovePersonalAsync(entry.Id, false);
        if (category == "Pin") await Store.UpdateAsync(entry.Id, pinned: true);
        else if (category == "Archive") await Store.UpdateAsync(entry.Id, archived: true);
        else if (Store.IsLabel(category))
            await Store.UpdateAsync(entry.Id, groups: entry.Groups.Append(category).Distinct().ToArray(), archived: false);
        else if (category == "All") await Store.UpdateAsync(entry.Id, archived: false);
    }

    public async Task AssignCategoryManyAsync(IReadOnlyList<ClipboardEntry> entries, string category)
    {
        if (entries.Count == 0) return;
        if (entries.Count == 1)
        {
            await AssignCategoryAsync(entries[0], category);
            return;
        }
        if (IsPersonalCategory(category))
        {
            for (int i = 0; i < entries.Count; i++)
                await Store.MovePersonalAsync(entries[i].Id, true);
            return;
        }
        var ids = new Guid[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsPersonal)
                await Store.MovePersonalAsync(entries[i].Id, false);
            ids[i] = entries[i].Id;
        }
        if (category == "Pin") await Store.UpdateManyAsync(ids, pinned: true);
        else if (category == "Archive") await Store.UpdateManyAsync(ids, archived: true);
        else if (Store.IsLabel(category)) await Store.UpdateManyAsync(ids, archived: false, addGroup: category);
        else if (category == "All") await Store.UpdateManyAsync(ids, archived: false);
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
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (!_disposed && e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            LockPersonalFromLifecycleEventAsync().SafeFireAndForget("CLIPBOARD-SESSION-LOCK");
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (!_disposed && e.Mode == PowerModes.Suspend)
            LockPersonalFromLifecycleEventAsync().SafeFireAndForget("CLIPBOARD-POWER-LOCK");
    }

    private async Task LockPersonalFromLifecycleEventAsync()
    {
        try { await _dispatcher.InvokeAsync(LockPersonalAsync).Task.Unwrap(); }
        catch (OperationCanceledException) { /* Dispatcher shut down while locking personal vault. */ }
    }

    private DateTime _nextHistoryPruneUtc = DateTime.UtcNow.AddHours(1);
    private bool _pruning;

    private void RetentionTick(object? sender, EventArgs e)
    {
        if (_pruning || _disposed) return;
        _pruning = true;
        RunRetentionTickAsync().SafeFireAndForget("CLIPBOARD-RETENTION");
    }

    private async Task RunRetentionTickAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            await Store.ExpireSensitiveAsync(now);
            if (now >= _nextHistoryPruneUtc)
            {
                await Store.PruneAsync(now);
                _nextHistoryPruneUtc = now.AddHours(1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Report("clipboard.error");
        }
        finally
        {
            _pruning = false;
        }
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
            return ProcessNameResolver.TryGetNameAndExecutable(processId);
        }
        catch { return ("", ""); }
    }
    private void Report(string key) => Dispatch(() => StatusChanged?.Invoke(Loc.Get(key)));
    private void ReleaseClipboardExports()
    {
        var paths = _clipboardExportPaths;
        _clipboardExportPaths = [];
        if (paths.Length > 0)
            ReleaseClipboardExportsAsync(paths).SafeFireAndForget("CLIPBOARD-EXPORT-CLEANUP");
    }
    private async Task ReleaseClipboardExportsAsync(string[] paths)
    {
        try { await Store.ReleasePersonalExportsAsync(paths); }
        catch (ObjectDisposedException) { /* Store already disposed during shutdown. */ }
    }
    private void ClearPersonalClipboard()
    {
        if (_personalClipboardSequence == 0) return;
        if (_personalClipboardSequence != 0 && GetClipboardSequenceNumber() == _personalClipboardSequence)
        {
            try { System.Windows.Clipboard.Clear(); }
            catch (ExternalException) { Report("clipboard.error"); }
        }
        _personalClipboardSequence = 0;
        ReleaseClipboardExports();
    }
    private void Dispatch(Action action) { if (!_disposed) _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
    public void Dispose() => DisposeAsync().AsTask().SafeFireAndForget("CLIPBOARD-DISPOSE");

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _retentionTimer.Tick -= RetentionTick;
        _debounceTimer.Tick -= DebounceTick;
        try
        {
            if (_dispatcher.CheckAccess())
                StopOnDispatcher();
            else if (!_dispatcher.HasShutdownStarted)
            {
                try { await _dispatcher.InvokeAsync(StopOnDispatcher, DispatcherPriority.Send).Task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted) { }
            }
        }
        finally
        {
            _captures.Writer.TryComplete();
            await DrainAsync().ConfigureAwait(false);
        }
    }

    private void StopOnDispatcher()
    {
        _retentionTimer.Stop();
        _debounceTimer.Stop();
        ClearPersonalClipboard();
    }

    private async Task DrainAsync()
    {
        try
        {
            if (_clipboardReader.IsValueCreated)
            {
                var reader = await _clipboardReader.Value.ConfigureAwait(false);
                await reader.InvokeAsync(reader.InvokeShutdown, DispatcherPriority.Send).Task.ConfigureAwait(false);
            }
        }
        finally
        {
            try { await _consumer.ConfigureAwait(false); }
            finally
            {
                await _clipboardWriteGate.WaitAsync().ConfigureAwait(false);
                try { await Store.DisposeAsync().ConfigureAwait(false); }
                finally { _clipboardWriteGate.Release(); }
            }
        }
    }
}
