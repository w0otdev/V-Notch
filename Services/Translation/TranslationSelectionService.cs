using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Threading;

namespace VNotch.Services.Translation;

internal sealed class TranslationSelectionService : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _manualRequestVersion;
    private DispatcherTimer? _debounce;
    private readonly AutomationFocusChangedEventHandler _focusChanged;
    private readonly AutomationEventHandler _selectionChanged;
    private AutomationElement? _root, _element, _eventElement;
    private TextPatternRange? _range;
    private TranslationSelection? _snapshot;
    private long _nextId;
    private IntPtr _watchedWindow;
    private volatile bool _disposed;
    private int _queued;
    private bool _manualCopyAllowed = true;
    private readonly WinEventCallback _foregroundChanged;
    private IntPtr _foregroundHook, _lastExternalWindow;
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
                    !IsOwnWindow(foreground) && foreground == Win32Interop.GetForegroundWindow(),
                () =>
                {
                    if ((GetAsyncKeyState(1) & 0x8000) != 0) return null;
                    _debounce?.Stop();
                    Capture(manual: true);
                    return _snapshot?.Window == foreground ? _snapshot : null;
                },
                selection => ManualTranslationRequested?.Invoke(selection),
                unavailable: () => { if (_manualCopyAllowed) ManualSelectionUnavailable?.Invoke(foreground, version); else ManualTranslationRequested?.Invoke(null); })).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A hung accessibility provider must not disable the explicit shortcut.
            // Retire its queued result before starting clipboard capture.
            if (!_disposed && foreground == Win32Interop.GetForegroundWindow() &&
                Interlocked.CompareExchange(ref _manualRequestVersion, version + 1, version) == version)
                ManualSelectionUnavailable?.Invoke(foreground, version + 1);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    internal static async Task CaptureManualAsync(Func<bool> isCurrent, Func<TranslationSelection?> capture,
        Action<TranslationSelection> deliver, Func<Task>? settle = null, Action? unavailable = null)
    {
        // Providers can report a transient empty selection on Shift key-up.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await (settle?.Invoke() ?? Task.Delay(80));
            if (!isCurrent()) return;
            var selection = capture();
            if (!isCurrent()) return;
            if (selection == null) continue;
            deliver(selection);
            return;
        }
        if (isCurrent()) unavailable?.Invoke();
    }

    internal TranslationSelectionService()
    {
        _focusChanged = (_, _) => QueueCapture();
        _selectionChanged = (sender, _) => { _eventElement = sender as AutomationElement; QueueCapture(); };
        _foregroundChanged = (_, _, hwnd, _, _, _, _) =>
        {
            if (hwnd == IntPtr.Zero || IsOwnWindow(hwnd)) return;
            _lastExternalWindow = hwnd;
            if (_snapshot != null && _snapshot.Window != hwnd) Publish(null);
            QueueCapture();
        };
        _thread = new Thread(Watch) { IsBackground = true, Name = "VNotch translation selection" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Watch()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_disposed) return;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            if (!_disposed) Capture();
        }, _dispatcher);
        _debounce.Stop();
        try
        {
            try { Automation.AddAutomationFocusChangedEventHandler(_focusChanged); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { }
            _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundChanged, 0, 0, 2);
            _ready.TrySetResult(_dispatcher);
            QueueCapture();
            Dispatcher.Run();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { }
        finally
        {
            _ready.TrySetCanceled();
            _debounce.Stop();
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            DetachSelection();
            try { Automation.RemoveAutomationFocusChangedEventHandler(_focusChanged); } catch (Exception) { }
            _element = null; _range = null; _snapshot = null;
        }
    }

    private void QueueCapture()
    {
        var dispatcher = _dispatcher;
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            _debounce!.Stop(); _debounce.Start();
        }, DispatcherPriority.Background);
    }

    private void Capture(bool manual = false)
    {
        try
        {
            IntPtr window = Win32Interop.GetForegroundWindow();
            if (window == IntPtr.Zero || IsOwnWindow(window)) return;
            _lastExternalWindow = window;
            if ((GetAsyncKeyState(1) & 0x8000) != 0) { _debounce!.Start(); return; }
            if (window != _watchedWindow)
            {
                DetachSelection();
                Publish(null, !manual);
                _root = AutomationElement.FromHandle(window);
                _watchedWindow = window;
                try { Automation.AddAutomationEventHandler(TextPattern.TextSelectionChangedEvent, _root, TreeScope.Subtree, _selectionChanged); }
                catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { }
            }
            var eventElement = _eventElement;
            _eventElement = null;
            var result = SelectionTextReader.Read(window, eventElement, manual, out bool protectedInput);
            if (manual) _manualCopyAllowed = !protectedInput;
            if (Win32Interop.GetForegroundWindow() != window) return;
            if (result == null)
            {
                var native = protectedInput ? null : NativeSelectionReader.Read(window);
                if (Win32Interop.GetForegroundWindow() != window) return;
                if (native != null && _snapshot?.Window == native.Window && _snapshot.Text == native.Text && !_snapshot.CanReplace) return;
                _element = null; _range = null;
                Publish(native, !manual);
                return;
            }
            if (_snapshot?.Text == result.Text && _snapshot.Window == window && _element != null &&
                Automation.Compare(_element, result.Element) && _range != null && result.Range != null && SameRange(_range, result.Range)) return;
            _element = result.Element; _range = result.Range;
            Publish(new(++_nextId, result.Text, result.Bounds, window, result.Editable), !manual);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or ArgumentException)
        { Publish(null, !manual); }
    }

    private void Publish(TranslationSelection? snapshot, bool notify = true)
    {
        if (_snapshot == null && snapshot == null) return;
        _snapshot = snapshot;
        if (snapshot == null) { _range = null; _element = null; }
        if (!_disposed && notify) SelectionChanged?.Invoke(snapshot);
    }

    private static bool SameRange(TextPatternRange a, TextPatternRange b) =>
        a.CompareEndpoints(TextPatternRangeEndpoint.Start, b, TextPatternRangeEndpoint.Start) == 0 &&
        a.CompareEndpoints(TextPatternRangeEndpoint.End, b, TextPatternRangeEndpoint.End) == 0;

    private static bool IsWithinWindow(AutomationElement element, IntPtr window)
    {
        AutomationElement? ancestor = element;
        for (int depth = 0; depth < 64 && ancestor != null; depth++)
        {
            if (new IntPtr(ancestor.Current.NativeWindowHandle) == window) return true;
            ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
        }
        return false;
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
            if (_disposed || _snapshot?.Id != selection.Id || _element == null || _range == null || !selection.CanReplace ||
                _lastExternalWindow != selection.Window ||
                _element.Current.IsPassword || _range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false ||
                !_element.TryGetCurrentPattern(TextPattern.Pattern, out var found) || found is not TextPattern pattern) return false;
            var current = pattern.GetSelection();
            if (current.Length != 1 || !SameRange(_range, current[0]) || current[0].GetText(2001) != selection.Text) return false;
            var foreground = Win32Interop.GetForegroundWindow();
            if (foreground != selection.Window && !IsOwnWindow(foreground)) return false;
            if (new[] { 0x10, 0x11, 0x12, 0x5B }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0) ||
                (GetAsyncKeyState(0x5C) & 0x8000) != 0) return false;
            if (!Win32Interop.SetForegroundWindow(selection.Window)) return false;
            _element.SetFocus();
            if (Win32Interop.GetForegroundWindow() != selection.Window) return false;
            current = pattern.GetSelection();
            if (current.Length != 1 || !SameRange(_range, current[0]) || current[0].GetText(2001) != selection.Text) return false;
            ct.ThrowIfCancellationRequested();
            if (_element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) && valueObject is ValuePattern value && !value.Current.IsReadOnly)
            {
                var document = pattern.DocumentRange;
                string original = document.GetText(20001);
                if (original.Length <= 20000 && value.Current.Value == original)
                {
                    var prefix = document.Clone();
                    prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, current[0], TextPatternRangeEndpoint.Start);
                    int offset = prefix.GetText(20001).Length;
                    if (offset + selection.Text.Length <= original.Length && original.Substring(offset, selection.Text.Length) == selection.Text)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Win32Interop.GetForegroundWindow() != selection.Window || value.Current.Value != original) return false;
                        value.SetValue(original[..offset] + text + original[(offset + selection.Text.Length)..]);
                        Publish(null);
                        return true;
                    }
                }
            }
            // Unicode insertion leaves the clipboard untouched. Newlines require a reliable ValuePattern;
            // sending Enter to an unknown chat control could submit the message.
            if (text.Contains('\n') || text.Contains('\r') || text.Length > 4000) return false;
            _range.Select();
            if (Win32Interop.GetForegroundWindow() != selection.Window) return false;
            var inputs = text.SelectMany(character => new[]
            {
                new Input { Type = 1, Keyboard = new KeyboardInput { Scan = character, Flags = 4 } },
                new Input { Type = 1, Keyboard = new KeyboardInput { Scan = character, Flags = 6 } }
            }).ToArray();
            ct.ThrowIfCancellationRequested();
            bool inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
            Publish(null);
            return inserted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException) { return false; }
    }

    private void DetachSelection()
    {
        if (_root != null)
            try { Automation.RemoveAutomationEventHandler(TextPattern.TextSelectionChangedEvent, _root, _selectionChanged); } catch (Exception) { }
        _root = null; _eventElement = null; _watchedWindow = IntPtr.Zero;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _manualRequestVersion);
        _ready.TrySetCanceled();
        var dispatcher = _dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
