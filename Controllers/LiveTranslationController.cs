using System.Windows;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch.Controllers;

internal sealed class LiveTranslationController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _manageModel;
    private readonly Func<IntPtr> _getForegroundWindow;
    private LocalTranslationService _service;
    private TranslationModelStore _store;
    private readonly bool _injected;
    private readonly DispatcherTimer _idle;
    private TranslationSelectionService? _selectionService;
    private TranslationShiftShortcut? _shiftShortcut;
    private TranslationSelection? _selection;
    private IntPtr _manualPresentationWindow;
    private TranslationWindow? _window;
    private NotchSettings _settings = new();
    private CancellationTokenSource? _request;
    private long _requestVersion;
    private string _text = "";
    private bool _disposed;

    internal LiveTranslationController(Dispatcher dispatcher, Action manageModel, TranslationModelStore? store = null, ILocalTranslationEngine? engine = null, Func<IntPtr>? getForegroundWindow = null)
    {
        _dispatcher = dispatcher; _manageModel = manageModel;
        _getForegroundWindow = getForegroundWindow ?? Win32Interop.GetForegroundWindow;
        _injected = store != null || engine != null;
        _store = store ?? TranslationModelStore.Shared;
        _service = new(engine ?? new QwenTranslationEngine(_store));
        _store.Changed += ModelChanged;
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => { if (_window?.IsPresentationAnimating != true) _service.ReleaseIdleResources(); }, dispatcher);
        _idle.Stop();
    }

    internal void ApplySettings(NotchSettings settings)
    {
        _dispatcher.VerifyAccess();
        bool languagesChanged = _settings.TranslationSourceLanguage != TranslationLanguages.Normalize(settings.TranslationSourceLanguage, true) ||
            _settings.TranslationTargetLanguage != TranslationLanguages.Normalize(settings.TranslationTargetLanguage);
        bool optionsChanged = TranslationOptions.From(_settings) != TranslationOptions.From(settings);
        bool changed = _settings.EnableLiveTranslation != settings.EnableLiveTranslation || _settings.AutoLiveTranslation != settings.AutoLiveTranslation || optionsChanged;
        string modelId = TranslationModelCatalog.Normalize(settings.TranslationModelId);
        if (!_injected && _store.Profile.Id != modelId)
        {
            changed = true;
            CancelRequest(); StopWatcher();
            _store.Changed -= ModelChanged;
            _service.Dispose();
            Task previousDisposal = _service.Disposal;
            _store = TranslationModelStore.For(modelId);
            _service = new(new QwenTranslationEngine(_store, previousDisposal: previousDisposal));
            _store.Changed += ModelChanged;
        }
        _service.Configure(TranslationOptions.From(settings));
        _settings = settings.Clone();
        _settings.TranslationModelId = modelId;
        _settings.TranslationSourceLanguage = TranslationLanguages.Normalize(settings.TranslationSourceLanguage, true);
        _settings.TranslationTargetLanguage = TranslationLanguages.Normalize(settings.TranslationTargetLanguage);
        if (changed) { CancelRequest(); _window?.Dismiss(); }
        _window?.ConfigureLanguages(_settings.TranslationSourceLanguage, _settings.TranslationTargetLanguage);
        _window?.RefreshLocalization();
        _window?.ConfigureFormatting(_settings.FormatTranslationText);
        UpdateWatcher();
        if (!changed && languagesChanged && _window?.IsVisible == true && _text.Length > 0) _ = TranslateAsync();
    }

    private void ModelChanged()
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            CancelRequest(); _window?.Dismiss(); UpdateWatcher();
        });
    }

    private void UpdateWatcher()
    {
        if (_settings.EnableLiveTranslation && _store.IsInstalled)
        {
            _idle.Start();
            if (_selectionService == null)
            {
                _selectionService = new();
                _selectionService.SelectionChanged += SelectionChanged;
                _selectionService.ManualTranslationRequested += ManualTranslationRequested;
                _selectionService.ManualSelectionUnavailable += ManualSelectionUnavailable;
            }
            _shiftShortcut ??= new TranslationShiftShortcut(_selectionService.RequestManualTranslation);
        }
        else
        {
            StopWatcher();
            _service.ReleaseIdleResources();
        }
    }

    private void StopWatcher()
    {
        _shiftShortcut?.Dispose(); _shiftShortcut = null;
        if (_selectionService == null) return;
        _selectionService.SelectionChanged -= SelectionChanged;
        _selectionService.ManualTranslationRequested -= ManualTranslationRequested;
        _selectionService.ManualSelectionUnavailable -= ManualSelectionUnavailable;
        _selectionService.Dispose();
        _selectionService = null;
    }

    private void SelectionChanged(TranslationSelection? selection)
        => PresentSelection(selection, false);

    private void ManualTranslationRequested(TranslationSelection? selection)
        => PresentSelection(selection, true);

    private void ManualSelectionUnavailable(IntPtr foreground, long captureVersion)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || !_settings.EnableLiveTranslation || _selectionService?.IsManualRequestCurrent(captureVersion) != true || _getForegroundWindow() != foreground) return;
            CancelRequest();
            long version = _requestVersion;
            bool Current() => !_disposed && version == _requestVersion && _selectionService?.IsManualRequestCurrent(captureVersion) == true && _getForegroundWindow() == foreground;
            var selection = await SelectionClipboardCapture.CaptureAsync(foreground, Current);
            if (Current()) PresentSelection(selection, true, Current);
        });
    }

    private void PresentSelection(TranslationSelection? selection, bool manual, Func<bool>? isCurrent = null)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_settings.EnableLiveTranslation || _selectionService == null) return;
            if (isCurrent != null && !isCurrent()) return;
            if (manual && selection != null && _getForegroundWindow() != selection.Window) return;
            if (_window?.IsVisible == true)
            {
                // UIA may issue a new snapshot ID for the same text/range after Shift.
                // Keep the result and the existing animation instead of restarting it.
                if (selection != null && _selection != null && selection.Window == _selection.Window &&
                    selection.Text == _selection.Text && selection.Bounds == _selection.Bounds &&
                    selection.CanReplace == _selection.CanReplace) return;
                // Empty Shift key-up snapshots must not close an explicitly opened
                // popup while its source application is still in the foreground.
                if (selection == null && _manualPresentationWindow != IntPtr.Zero &&
                    _getForegroundWindow() == _manualPresentationWindow) return;
            }
            CancelRequest();
            _manualPresentationWindow = manual ? selection?.Window ?? _getForegroundWindow() : IntPtr.Zero;
            if (!manual && !_settings.AutoLiveTranslation)
            {
                _window?.Dismiss();
                _selection = selection;
                _text = selection?.Text ?? "";
                return;
            }
            _selection = selection;
            if (selection == null)
            {
                _text = "";
                if (manual)
                {
                    EnsureWindow().ShowClipboard("");
                    _window!.SetStatus("translation.noSelection");
                }
                else _window?.Dismiss();
                return;
            }
            _text = selection.Text;
            var window = EnsureWindow();
            window.ShowSelection(selection, true);
            _ = TranslateAsync();
        }, DispatcherPriority.Background);
    }

    private TranslationWindow EnsureWindow()
    {
        if (_window != null) return _window;
        var window = new TranslationWindow(_settings.TranslationSourceLanguage, _settings.TranslationTargetLanguage);
        window.ConfigureFormatting(_settings.FormatTranslationText);
        window.TranslateRequested += () => _ = TranslateAsync();
        window.CancelRequested += () => { CancelRequest(); window.SetStatus("translation.cancelled"); };
        window.ReplaceRequested += () => _ = ReplaceAsync();
        window.Dismissed += () => { CancelRequest(); _text = ""; _selection = null; _manualPresentationWindow = IntPtr.Zero; };
        window.LanguagesChanged += (source, target) =>
        {
            CancelRequest();
            _settings.TranslationSourceLanguage = source; _settings.TranslationTargetLanguage = target;
            if (_text.Length > 0 && window.IsVisible) _ = TranslateAsync();
        };
        _window = window;
        return window;
    }

    internal void OpenFromClipboard()
    {
        if (_disposed) return;
        CancelRequest();
        _selection = null;
        _manualPresentationWindow = IntPtr.Zero;
        try
        {
            _text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (_text.Length > 2000) { _text = ""; EnsureWindow().ShowClipboard(""); _window!.SetStatus("translation.textTooLong"); return; }
            EnsureWindow().ShowClipboard(_text);
            if (_text.Length == 0) _window!.SetStatus("translation.noSelection");
        }
        catch (System.Runtime.InteropServices.COMException) { EnsureWindow().SetStatus("translation.clipboardBusy"); }
    }

    private async Task TranslateAsync()
    {
        if (_disposed || _window == null) return;
        CancelRequest();
        long version = _requestVersion;
        using var request = new CancellationTokenSource();
        _request = request;
        var window = _window;
        window.SetStatus("translation.working");
        var elapsed = new System.Diagnostics.Stopwatch();
        var activity = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        activity.Tick += (_, _) =>
        {
            if (_disposed || version != _requestVersion) { activity.Stop(); return; }
            window.ShowActivity(_service.Stage, (int)elapsed.Elapsed.TotalSeconds);
        };
        try
        {
            await window.WaitForEntranceAsync(request.Token);
            if (_disposed || version != _requestVersion || !window.IsVisible) return;
            request.CancelAfter(TimeSpan.FromSeconds(_settings.TranslationTimeoutSeconds));
            elapsed.Start();
            _idle.Start();
            activity.Start();
            if (!_store.IsInstalled) throw new TranslationException("translation.modelMissing");
            var result = await _service.TranslateAsync(_text, window.SourceLanguage, window.TargetLanguage, request.Token).WaitAsync(request.Token);
            if (!_disposed && version == _requestVersion && window.IsVisible) window.ShowResult(result);
        }
        catch (OperationCanceledException) { if (!_disposed && version == _requestVersion) window.SetStatus("translation.timedOut"); }
        catch (TranslationException ex) { if (!_disposed && version == _requestVersion) window.SetStatus(ex.MessageKey); }
        catch (Exception)
        {
            // Native exception payloads can contain text; never write them to diagnostic logs.
            if (!_disposed && version == _requestVersion) window.SetStatus(request.IsCancellationRequested ? "translation.timedOut" : "translation.failed");
        }
        finally { activity.Stop(); if (ReferenceEquals(_request, request)) _request = null; }
    }

    private async Task ReplaceAsync()
    {
        if (_selection == null || _selectionService == null || _window == null) return;
        var window = _window;
        var selection = _selection;
        window.DisableReplacement();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            bool replaced = await _selectionService.ReplaceAsync(selection, window.ResultText.Text, timeout.Token);
            if (!_disposed) { if (replaced) window.Dismiss(); else window.SetStatus("translation.replaceUnavailable"); }
        }
        catch (Exception) { if (!_disposed) window.SetStatus("translation.replaceUnavailable"); }
    }

    private void CancelRequest()
    {
        _requestVersion++;
        try { _request?.Cancel(); } catch (ObjectDisposedException) { }
        _request = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelRequest(); StopWatcher(); _idle.Stop();
        _store.Changed -= ModelChanged;
        _window?.Close(); _window = null;
        _service.Dispose();
    }
}
