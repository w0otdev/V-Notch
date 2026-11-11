using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace VNotch.Services.Translation;

internal sealed partial class TranslationSelectionService : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_selection_can_send_unicode_input(
        ReadOnlySpan<char> text,
        int textLen,
        out byte outCanSend);

    [LibraryImport(NativeDll)]
    private static partial int vn_translation_selection_can_preserve_recent_snapshot(
        byte manual,
        byte protectedInput,
        byte sameWindow,
        long nowTick,
        long lastValidTick,
        out byte outCanPreserve);

    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _manualRequestVersion;
    private UiScheduledTimer? _debounce;
    private AutomationElement? _root;
    private NativeSelectionState? _nativeSelection;
    private TranslationSelection? _snapshot;
    private long _nextId;
    private IntPtr _watchedWindow;
    private volatile bool _disposed;
    private int _queued;
    private readonly Action<uint, IntPtr> _foregroundChanged;
    private readonly Action<uint, IntPtr> _objectEventChanged;
    private Win32Interop.WinEventHook? _foregroundHook, _focusHook, _selectionHook;
    private IntPtr _lastExternalWindow;
    private readonly Func<IntPtr, bool>? _isBlocked;
    private volatile bool _automaticWatching;
    internal event Action<TranslationSelection?>? SelectionChanged;
    internal event Action<TranslationSelection?>? ManualTranslationRequested;
    internal event Action<IntPtr, long>? ManualSelectionUnavailable;
    internal bool IsManualRequestCurrent(long version) => !_disposed && version == Volatile.Read(ref _manualRequestVersion);

    internal void RequestManualTranslation()
    {
        if (_disposed) return;
        var foreground = Win32Interop.GetForegroundWindow();
        long version = Interlocked.Increment(ref _manualRequestVersion);
        _ = RequestManualTranslationAsync(foreground, version);
    }

    private async Task RequestManualTranslationAsync(IntPtr foreground, long version)
    {
        try
        {
            // Preserve a shortcut pressed while the accessibility worker is starting.
            var dispatcher = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (_disposed || dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(() => CaptureManualAsync(
                () => !_disposed && version == Volatile.Read(ref _manualRequestVersion) && foreground != IntPtr.Zero &&
                    !IsOwnWindow(foreground) && foreground == Win32Interop.GetForegroundWindow() && _isBlocked?.Invoke(foreground) != true,
                () =>
                {
                    if ((Win32Interop.GetAsyncKeyState(1) & 0x8000) != 0) return null;
                    _debounce?.Stop();
                    Capture(manual: true);
                    return _snapshot?.Window == foreground ? _snapshot : null;
                },
                selection => ManualTranslationRequested?.Invoke(selection),
                unavailable: () => ManualSelectionUnavailable?.Invoke(foreground, version))).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A hung accessibility provider must not disable the explicit shortcut.
            // Retire its queued result before offering manual text input.
            if (!_disposed && foreground == Win32Interop.GetForegroundWindow() && _isBlocked?.Invoke(foreground) != true &&
                Interlocked.CompareExchange(ref _manualRequestVersion, version + 1, version) == version)
                ManualSelectionUnavailable?.Invoke(foreground, version + 1);
        }
        catch (OperationCanceledException) { /* Manual selection request was superseded or canceled. */ }
        catch (InvalidOperationException) { /* Dispatcher shut down before manual capture completed. */ }
    }

    internal static async Task CaptureManualAsync(Func<bool> isCurrent, Func<TranslationSelection?> capture,
        Action<TranslationSelection> deliver, Func<Task>? settle = null, Action? unavailable = null)
    {
        // Try a ready selection immediately; only empty snapshots need to settle
        // after Shift key-up. Retries stay bounded for slow providers.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await (settle?.Invoke() ?? Task.Delay(80));
            if (!isCurrent()) return;
            var selection = capture();
            if (!isCurrent()) return;
            if (selection == null) continue;
            deliver(selection);
            return;
        }
        if (isCurrent()) unavailable?.Invoke();
    }

    internal TranslationSelectionService(Func<IntPtr, bool>? isBlocked = null, bool automaticWatching = true)
    {
        _isBlocked = isBlocked;
        _automaticWatching = automaticWatching;
        _foregroundChanged = (_, hwnd) =>
        {
            if (hwnd == IntPtr.Zero || IsOwnWindow(hwnd)) return;
            _lastExternalWindow = hwnd;
            if (_snapshot != null && _snapshot.Window != hwnd) Publish(null);
            QueueCapture();
        };
        _objectEventChanged = (_, hwnd) =>
        {
            if (hwnd == IntPtr.Zero || IsOwnWindow(hwnd)) return;
            QueueCapture();
        };
        _thread = new Thread(Watch) { IsBackground = true, Name = "VNotch translation selection" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Watch()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Volatile.Write(ref _dispatcher, dispatcher);
        if (_disposed) { _ready.TrySetCanceled(); return; }
        _debounce = new UiScheduledTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            if (!_disposed) Capture();
        }, dispatcher);
        _debounce.Stop();
        try
        {
            if (_disposed) return;
            // EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_FOCUS, EVENT_OBJECT_TEXTSELECTIONCHANGED
            _foregroundHook = new Win32Interop.WinEventHook(3, 3, _foregroundChanged);
            _focusHook = new Win32Interop.WinEventHook(0x8005, 0x8005, _objectEventChanged);
            _selectionHook = new Win32Interop.WinEventHook(0x8014, 0x8014, _objectEventChanged);
            UpdateAutomaticHooks();
            if (_disposed) return;
            _ready.TrySetResult(dispatcher);
            QueueCapture();
            dispatcher.BeginInvoke(() =>
            {
                if (_disposed && !dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, DispatcherPriority.Send);
            Dispatcher.Run();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { /* Selection worker shutting down or UIA provider unavailable. */ }
        finally
        {
            _ready.TrySetCanceled();
            _debounce.Stop();
            _foregroundHook?.Dispose();
            _focusHook?.Dispose();
            _selectionHook?.Dispose();
            DetachSelection();
            _nativeSelection?.Dispose(); _nativeSelection = null; _snapshot = null;
        }
    }

    private void QueueCapture()
    {
        if (!_automaticWatching) return;
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed || !_automaticWatching) return;
            _debounce!.Stop(); _debounce.Start();
        }, DispatcherPriority.Background);
    }

    internal void SetAutomaticWatching(bool enabled)
    {
        if (_automaticWatching == enabled) return;
        _automaticWatching = enabled;
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (!_disposed && dispatcher != null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(UpdateAutomaticHooks, DispatcherPriority.Background);
    }

    private void UpdateAutomaticHooks()
    {
        if (_disposed) return;
        // Manual shortcuts retain their worker, but install no global UIA / WinEvent
        // watchers and perform no background selection scans in manual mode.
        _foregroundHook?.Dispose(); _focusHook?.Dispose(); _selectionHook?.Dispose();
        if (_automaticWatching)
        {
            _foregroundHook?.Install(); _focusHook?.Install(); _selectionHook?.Install();
            QueueCapture();
        }
        else
        {
            _debounce?.Stop();
            DetachSelection();
            _nativeSelection?.Dispose(); _nativeSelection = null; _snapshot = null;
        }
    }

    private TranslationSelection? _lastValidSnapshot;
    private long _lastValidTick;

    private void Capture(bool manual = false)
    {
        try
        {
            IntPtr window = Win32Interop.GetForegroundWindow();
            if (window == IntPtr.Zero || IsOwnWindow(window)) return;
            if (_isBlocked?.Invoke(window) == true)
            {
                DetachSelection(); _lastValidSnapshot = null; Publish(null, !manual); return;
            }
            _lastExternalWindow = window;
            if (!manual && !_automaticWatching) return;
            if ((Win32Interop.GetAsyncKeyState(1) & 0x8000) != 0) { if (_automaticWatching) _debounce!.Start(); return; }
            if (window != _watchedWindow)
            {
                DetachSelection();
                _lastValidSnapshot = null;
                Publish(null, !manual);
                _root = AutomationElement.FromHandle(window);
                _watchedWindow = window;
            }
            var result = SelectionTextReader.Read(window, null, manual, out bool protectedInput, rootElement: _root, previousSelection: _nativeSelection);
            if (Win32Interop.GetForegroundWindow() != window) { result?.Dispose(); return; }
            if (result == null)
            {
                var native = protectedInput ? null : NativeSelectionReader.Read(window);
                if (Win32Interop.GetForegroundWindow() != window) return;
                if (native == null &&
                    vn_translation_selection_can_preserve_recent_snapshot(
                        manual ? (byte)1 : (byte)0,
                        protectedInput ? (byte)1 : (byte)0,
                        _lastValidSnapshot?.Window == window ? (byte)1 : (byte)0,
                        Environment.TickCount64,
                        _lastValidTick,
                        out byte canPreserve) == 0 && canPreserve != 0)
                {
                    // Preserve a selection captured moments before Shift key-down cleared or unfocused the provider.
                    native = _lastValidSnapshot;
                }
                if (native != null)
                {
                    _lastValidSnapshot = native;
                    _lastValidTick = Environment.TickCount64;
                }
                if (native != null && _snapshot?.Window == native.Window && _snapshot.Text == native.Text && !_snapshot.CanReplace) return;
                _nativeSelection?.Dispose(); _nativeSelection = null;
                Publish(native, !manual);
                return;
            }
            if (_snapshot?.Text == result.Text && _snapshot.Window == window && _nativeSelection != null &&
                _nativeSelection.SameSelection(result.Native))
            {
                result.Dispose();
                _lastValidSnapshot = _snapshot;
                _lastValidTick = Environment.TickCount64;
                return;
            }
            _nativeSelection?.Dispose();
            _nativeSelection = result.Native;
            var published = new TranslationSelection(++_nextId, result.Text, result.Bounds, window, result.Editable) { InputTruncated = result.InputTruncated };
            _lastValidSnapshot = published;
            _lastValidTick = Environment.TickCount64;
            Publish(published, !manual);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or ArgumentException)
        { Publish(null, !manual); }
    }

    private void Publish(TranslationSelection? snapshot, bool notify = true)
    {
        if (_snapshot == null && snapshot == null) return;
        _snapshot = snapshot;
        if (snapshot == null) { _nativeSelection?.Dispose(); _nativeSelection = null; }
        if (!_disposed && notify) SelectionChanged?.Invoke(snapshot);
    }

    internal async Task<bool> ReplaceAsync(TranslationSelection selection, string text, CancellationToken ct)
    {
        var dispatcher = _dispatcher;
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted) return false;
        var operation = dispatcher.InvokeAsync(() => Replace(selection, text, ct), DispatcherPriority.Normal, ct);
        return await operation.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private bool Replace(TranslationSelection selection, string text, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || _snapshot?.Id != selection.Id || _nativeSelection == null || !selection.CanReplace ||
                _lastExternalWindow != selection.Window) return false;
            int outcome = _nativeSelection.PrepareReplace(selection.Text, text, ct);
            if (outcome == 1) { Publish(null); return true; }
            if (outcome != 2) { ct.ThrowIfCancellationRequested(); return false; }
            // Unicode insertion leaves the clipboard untouched. Newlines require a reliable ValuePattern;
            // sending Enter to an unknown chat control could submit the message.
            if (vn_translation_selection_can_send_unicode_input(text.AsSpan(), text.Length, out byte canSend) != 0 || canSend == 0) return false;
            if (Win32Interop.GetForegroundWindow() != selection.Window) return false;
            ct.ThrowIfCancellationRequested();
            bool inserted = Win32Interop.SendUnicodeText(text);
            Publish(null);
            return inserted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { return false; }
    }

    private void DetachSelection()
    {
        _root = null;
        _watchedWindow = IntPtr.Zero;
        _nativeSelection?.Dispose(); _nativeSelection = null;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _manualRequestVersion);
        _ready.TrySetCanceled();
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
