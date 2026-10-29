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
    private readonly Action<string, string>? _saveLanguages;
    private readonly Func<IntPtr> _getForegroundWindow;
    private LocalTranslationService _service;
    private TranslationModelStore _store;
    private readonly bool _injected;
    private readonly UiScheduledTimer _idle;
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
    private Task? _preparation;
    private Task? _idleCleanup;
    private readonly CurrencyRateService _currencyRates;
    private readonly TranslationAppPolicy _appPolicy = new();
    private string? _sourceCurrencyOverride, _targetCurrencyOverride;
    private bool _inputWasTruncated;

    internal LiveTranslationController(Dispatcher dispatcher, Action manageModel, TranslationModelStore? store = null, ILocalTranslationEngine? engine = null, Func<IntPtr>? getForegroundWindow = null, CurrencyRateService? currencyRates = null, Action<string, string>? saveLanguages = null)
    {
        _dispatcher = dispatcher; _manageModel = manageModel; _saveLanguages = saveLanguages;
        _currencyRates = currencyRates ?? CurrencyRateService.Shared;
        _getForegroundWindow = getForegroundWindow ?? Win32Interop.GetForegroundWindow;
        _injected = store != null || engine != null;
        _store = store ?? TranslationModelStore.Shared;
        _service = new(engine ?? new QwenTranslationEngine(_store));
        _store.Changed += ModelChanged;
        _idle = new UiScheduledTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => { if (_window?.IsPresentationAnimating != true) ScheduleIdleResourceRelease(); }, dispatcher);
        _idle.Stop();
    }

    internal void ApplySettings(NotchSettings settings)
    {
        _dispatcher.VerifyAccess();
        bool languagesChanged = _settings.TranslationSourceLanguage != TranslationLanguages.Normalize(settings.TranslationSourceLanguage, true) ||
            _settings.TranslationTargetLanguage != TranslationLanguages.Normalize(settings.TranslationTargetLanguage);
        bool optionsChanged = TranslationOptions.From(_settings) != TranslationOptions.From(settings);
        bool changed = _settings.EnableLiveTranslation != settings.EnableLiveTranslation || _settings.AutoLiveTranslation != settings.AutoLiveTranslation || optionsChanged ||
            _settings.TranslationIgnoreFullscreen != settings.TranslationIgnoreFullscreen || _settings.TranslationExcludedApps != settings.TranslationExcludedApps ||
            _settings.EnableCurrencyConversion != settings.EnableCurrencyConversion || _settings.TranslationTargetCurrency != settings.TranslationTargetCurrency ||
            _settings.EnableLocalOnlyMode != settings.EnableLocalOnlyMode;
        string modelId = TranslationModelCatalog.Normalize(settings.TranslationModelId);
        if (!_injected && _store.Profile.Id != modelId)
        {
            changed = true;
            CancelRequest();
            _store.Changed -= ModelChanged;
            _service.Dispose();
            Task previousDisposal = _service.Disposal;
            _store = TranslationModelStore.For(modelId);
            _service = new(new QwenTranslationEngine(_store, previousDisposal: previousDisposal));
            _preparation = null;
            _store.Changed += ModelChanged;
        }
        _service.Configure(TranslationOptions.From(settings));
        _settings = settings.Clone();
        _appPolicy.Configure(_settings);
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
        if (_settings.EnableLiveTranslation)
        {
            _idle.Start();
            if (!_injected) _ = PrepareModelAsync();
            if (_selectionService == null)
            {
                _selectionService = new(_appPolicy.IsBlocked);
                _selectionService.SelectionChanged += SelectionChanged;
                _selectionService.ManualTranslationRequested += ManualTranslationRequested;
                _selectionService.ManualSelectionUnavailable += ManualSelectionUnavailable;
            }
            _shiftShortcut ??= new TranslationShiftShortcut(_selectionService.RequestManualTranslation, _appPolicy.IsBlocked);
        }
        else
        {
            StopWatcher();
            ScheduleIdleResourceRelease();
        }
    }

    private void ScheduleIdleResourceRelease()
    {
        if (_disposed || _idleCleanup?.IsCompleted == false) return;
        var service = _service;
        // Cache pruning, option changes and native weight disposal can be costly.
        // Coalesce timer/settings requests and keep them away from the dispatcher.
        _idleCleanup = Task.Run(() =>
        {
            try { service.ReleaseIdleResources(); }
            catch (ObjectDisposedException) { /* Intentionally ignored. */ }
            catch (Exception)
            {
                RuntimeLog.Error("TRANSLATION", "Idle cleanup failed; exception payload omitted for privacy.");
            }
        }, CancellationToken.None);
    }

    internal Task PrepareModelAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_store.IsInstalled) return Task.CompletedTask;
        // Startup also prepares manual translation when selection watching is off.
        // Share an in-flight load with settings changes and retain idle eviction.
        _idle.Start();
        if (_preparation?.IsCompleted != false)
            _preparation = PrepareInBackgroundAsync(_service);
        return _preparation;
    }

    private static async Task PrepareInBackgroundAsync(LocalTranslationService service)
    {
        try { await Task.Run(() => service.PrepareAsync(CancellationToken.None)).ConfigureAwait(false); }
        catch (Exception)
        {
            // Warm-up is opportunistic. A foreground request reports model errors
            // through the normal status UI; never leave an unobserved native fault.
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
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_settings.EnableLiveTranslation || _selectionService?.IsManualRequestCurrent(captureVersion) != true || _getForegroundWindow() != foreground || _appPolicy.IsBlocked(foreground)) return;
            CancelRequest();
            long version = _requestVersion;
            bool Current() => !_disposed && version == _requestVersion && _selectionService?.IsManualRequestCurrent(captureVersion) == true && _getForegroundWindow() == foreground && !_appPolicy.IsBlocked(foreground);
            // Unsupported selection providers leave input to the user. Never send
            // Copy shortcuts to another application or borrow its clipboard.
            if (Current()) PresentSelection(null, true, Current);
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
            if (selection != null && _appPolicy.IsBlocked(selection.Window)) { CancelRequest(); _window?.Dismiss(); return; }
            if (_window?.IsVisible == true)
            {
                // UIA may issue a new snapshot ID for the same text/range after Shift.
                // Keep the result and the existing animation instead of restarting it.
                if (selection != null && _selection != null && selection.Window == _selection.Window &&
                    selection.Text == _selection.Text && selection.Bounds == _selection.Bounds &&
                    selection.CanReplace == _selection.CanReplace && selection.InputTruncated == _selection.InputTruncated) return;
                // Empty Shift key-up snapshots must not close an explicitly opened
                // popup while its source application is still in the foreground.
                if (selection == null && _manualPresentationWindow != IntPtr.Zero &&
                    _getForegroundWindow() == _manualPresentationWindow) return;
            }
            CancelRequest();
            _sourceCurrencyOverride = _targetCurrencyOverride = null;
            _inputWasTruncated = selection?.InputTruncated == true;
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
        window.ManageModelRequested += () => { window.Dismiss(); _manageModel(); };
        window.CurrenciesChanged += (source, target) =>
        {
            _sourceCurrencyOverride = source; _targetCurrencyOverride = target;
            if (_text.Length > 0 && window.IsVisible) _ = TranslateAsync();
        };
        window.Dismissed += () => { CancelRequest(); _text = ""; _selection = null; _manualPresentationWindow = IntPtr.Zero; };
        window.Closed += (_, _) =>
        {
            CancelRequest(); _window = null;
            _text = ""; _selection = null; _manualPresentationWindow = IntPtr.Zero;
        };
        window.LanguagesChanged += (source, target) =>
        {
            CancelRequest();
            _settings.TranslationSourceLanguage = source; _settings.TranslationTargetLanguage = target;
            _saveLanguages?.Invoke(source, target);
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
        _sourceCurrencyOverride = _targetCurrencyOverride = null;
        _text = "";
        _inputWasTruncated = false;
        try
        {
            string raw = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            _inputWasTruncated = raw.Length > TranslationInput.MaxCharacters;
            _text = TranslationInput.Prefix(raw, TranslationInput.MaxCharacters);
            var window = EnsureWindow();
            window.ShowClipboard(_text, _inputWasTruncated);
            if (_text.Length == 0) window.SetStatus("translation.noSelection");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _text = "";
            _inputWasTruncated = false;
            var window = EnsureWindow();
            window.ShowClipboard("");
            window.SetStatus("translation.clipboardBusy");
        }
    }

    private async Task TranslateAsync()
    {
        if (_disposed || _window == null) return;
        CancelRequest();
        long version = _requestVersion;
        using var request = new CancellationTokenSource();
        _request = request;
        var window = _window;
        bool convertingCurrency = false;
        window.SetStatus("translation.working");
        var elapsed = new System.Diagnostics.Stopwatch();
        var activity = new UiScheduledTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        activity.Tick += (_, _) =>
        {
            if (_disposed || version != _requestVersion) { activity.Stop(); return; }
            window.ShowActivity(convertingCurrency ? TranslationStage.FetchingRates : _service.Stage, (int)elapsed.Elapsed.TotalSeconds);
        };
        try
        {
            if (_disposed || version != _requestVersion || !window.IsVisible) return;
            request.CancelAfter(TimeSpan.FromSeconds(_settings.TranslationTimeoutSeconds));
            elapsed.Start();
            _idle.Start();
            activity.Start();
            // Snapshot UI state before dispatching. Inference overlaps the entrance;
            // only presentation waits so a fast cache hit cannot resize the opening popup.
            var service = _service;
            var store = _store;
            string text = TranslationInput.Prepare(_text), source = window.SourceLanguage, target = window.TargetLanguage;
            bool inputTruncated = _inputWasTruncated || text.Length < _text.Length;
            window.SetInputTruncated(inputTruncated);
            if (TranslationCurrencies.HasConflictingCurrency(text, source)) throw new TranslationException("translation.currencyAmbiguous");
            var token = request.Token;
            TranslationResult? result = null;
            bool hasPrice = TranslationCurrencies.TryParse(text, source, out var price, _sourceCurrencyOverride);
            bool unresolvedPrice = TranslationCurrencies.HasUnresolvedSymbol(text, source);
            string? currency = _targetCurrencyOverride ?? (TranslationCurrencies.Normalize(_settings.TranslationTargetCurrency) is var choice && choice != "auto"
                ? choice : TranslationCurrencies.ForLanguage(target));
            window.ConfigureCurrencies(_settings.EnableCurrencyConversion && (hasPrice || unresolvedPrice), hasPrice ? price.Currency : null, currency);
            if (_settings.EnableCurrencyConversion && unresolvedPrice && !hasPrice) throw new TranslationException("translation.currencyChooseSource");
            if (_settings.EnableCurrencyConversion && hasPrice && currency != null)
            {
                convertingCurrency = true;
                try
                {
                    var converted = await Task.Run(() => _currencyRates.ConvertAsync(price, currency, token), token).WaitAsync(token);
                    string amount = converted.Amount.ToString("N" + TranslationCurrencies.DecimalPlaces(currency), Loc.GetCulture());
                    result = new TranslationResult((converted.Rate == null ? "" : "≈ ") + amount + " " + currency, source, target, false) { Currency = converted };
                }
                catch (TranslationException ex) when (ex.MessageKey == "translation.currencyUnavailable") { /* Fall back to text translation. */ }
                finally { convertingCurrency = false; }
            }
            // A cache hit needs neither language-profile initialization nor model work.
            if (result == null && store.IsInstalled)
                result = service.TryGetCachedResult(text, source, target, token);
            if (result == null)
                result = await LocalTranslationService.TrySameLanguageAsync(text, source, target, token);
            if (result == null)
            {
                token.ThrowIfCancellationRequested();
                if (!store.IsInstalled) throw new TranslationException("translation.modelMissing");
                // Dismissal cancels this request, while the shared model preparation finishes.
                Task preparation = PrepareModelAsync();
                result = await Task.Run(async () =>
                {
                    await preparation.WaitAsync(token).ConfigureAwait(false);
                    return await service.TranslateAfterLanguageCheckAsync(text, source, target, token).ConfigureAwait(false);
                }, token).WaitAsync(token);
            }
            result = result with { InputTruncated = result.InputTruncated || inputTruncated };
            await window.WaitForEntranceAsync(token);
            if (!_disposed && version == _requestVersion && window.IsVisible) window.ShowResult(result);
        }
        catch (OperationCanceledException) { if (!_disposed && version == _requestVersion && request.IsCancellationRequested) window.SetStatus("translation.timedOut"); }
        catch (TranslationException ex) { if (!_disposed && version == _requestVersion) window.SetStatus(ex.MessageKey); }
        catch (Exception ex)
        {
            var translationEx = (ex as AggregateException)?.Flatten().InnerExceptions.OfType<TranslationException>().FirstOrDefault();
            if (translationEx != null)
            {
                if (!_disposed && version == _requestVersion) window.SetStatus(translationEx.MessageKey);
                return;
            }
            RuntimeLog.Error("TRANSLATION", "Translation failed; exception payload omitted for privacy.");
            if (TranslationNativeRuntime.IsLoadFailure(ex))
            {
                if (!_disposed && version == _requestVersion) window.SetStatus("translation.runtimeMissing");
                return;
            }
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
            if (!window.CanReplaceResult) return;
            bool replaced = await _selectionService.ReplaceAsync(selection, window.OutputText, timeout.Token);
            if (!_disposed) { if (replaced) window.Dismiss(); else window.SetStatus("translation.replaceUnavailable"); }
        }
        catch (Exception) { if (!_disposed) window.SetStatus("translation.replaceUnavailable"); }
    }

    private void CancelRequest()
    {
        _requestVersion++;
        try { _request?.Cancel(); } catch (ObjectDisposedException) { /* Intentionally ignored. */ }
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
