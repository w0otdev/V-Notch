using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal sealed partial class LocalTranslationService(ILocalTranslationEngine engine) : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_local_translation_should_cache_result(
        byte literalsPreserved,
        byte weekdaysPreserved,
        int cacheEntries,
        out byte outShouldCache);

    [LibraryImport(NativeDll)]
    private static partial int vn_local_translation_is_cache_entry_expired(
        long expiresUtcTicks,
        long nowUtcTicks,
        out byte outExpired);

    private sealed record CacheEntry((string Text, string Source, string Target) Key, TranslationResult Result, DateTime ExpiresUtc);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<(string Text, string Source, string Target), LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _order = new();
    private TranslationOptions _requestedOptions = new();
    private TranslationOptions _options = new();
    internal void Configure(TranslationOptions options) => Volatile.Write(ref _requestedOptions, options);
    private void ApplyOptions()
    {
        var options = Volatile.Read(ref _requestedOptions);
        engine.Configure(options);
        lock (_cacheLock)
        {
            if (!options.CanReuseCachedResults(_options))
            {
                _cache.Clear(); _order.Clear();
            }
            _options = options;
        }
    }
    private volatile bool _disposed;
    internal Task Disposal => (engine as QwenTranslationEngine)?.Disposal ?? Task.CompletedTask;
    internal TranslationStage Stage => engine.Stage;

    internal async Task PrepareAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyOptions();
            await engine.PrepareAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken ct) =>
        TranslateCoreAsync(text, source, target, ct, sameLanguageChecked: false);

    // The controller already checked this exact prepared text and language pair.
    internal Task<TranslationResult> TranslateAfterLanguageCheckAsync(string text, string source, string target, CancellationToken ct) =>
        TranslateCoreAsync(text, source, target, ct, sameLanguageChecked: true);

    private async Task<TranslationResult> TranslateCoreAsync(string text, string source, string target, CancellationToken ct, bool sameLanguageChecked)
    {
        string prepared = PrepareRequest(text, source, target, ct);
        bool truncated = prepared.Length < text.Length;
        text = prepared;
        if (TranslationStructuredText.IsOnlyIcons(text))
            return new(text, source, target, true) { InputTruncated = truncated };
        string originalText = text;
        var key = (originalText, source, target);
        if (TryGetCached(key, truncated, ct) is { } cached) return cached;
        if (!sameLanguageChecked)
        {
            var unchanged = await TrySameLanguageAsync(text, source, target, ct).ConfigureAwait(false);
            if (unchanged != null)
            {
                ct.ThrowIfCancellationRequested();
                return unchanged with { InputTruncated = truncated };
            }
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyOptions();
            // Another request may have populated this entry while we waited.
            if (TryGetCached(key, truncated, ct) is { } completed) return completed;
            string fitted = await engine.FitInputAsync(text, ct).ConfigureAwait(false);
            bool tokenTruncated = fitted.Length < text.Length;
            text = fitted;
            var fittedKey = (text, source, target);
            if (fittedKey != key && TryGetCached(fittedKey, tokenTruncated || truncated, ct) is { } fittedCached)
            {
                var cachedInspection = TranslationTextIntegrity.Inspect(originalText, fittedCached.Text);
                if (!cachedInspection.LiteralsPreserved && !cachedInspection.ProtectedFactsPreserved)
                    throw new TranslationException("translation.literalsChanged");
                return fittedCached with
                {
                    LiteralsPreserved = fittedCached.LiteralsPreserved && cachedInspection.LiteralsPreserved,
                    InputTruncated = tokenTruncated || truncated
                };
            }
            string translated = await TranslationStructuredText.TranslateAsync(text,
                (prose, token) => engine.TranslateAsync(prose, source, target, token), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            translated = TranslationTextIntegrity.Clean(translated);
            string hintSource = source == "auto" ? TranslationLanguages.Detect(text) ?? source : source;
            if (!TranslationMeaningIntegrity.PreservesStatement(text, hintSource, translated))
                throw new TranslationException("translation.meaningChanged");
            // Numeric mismatches remain readable for review, without replacement or caching.
            var inspection = TranslationTextIntegrity.Inspect(originalText, translated);
            bool literalsPreserved = inspection.LiteralsPreserved;
            if (!literalsPreserved && !inspection.ProtectedFactsPreserved)
                throw new TranslationException("translation.literalsChanged");
            var result = new TranslationResult(translated, source, target, literalsPreserved,
                TranslationDateIntegrity.PreservesWeekdays(text, hintSource, translated, target))
            { InputTruncated = tokenTruncated };
            if (vn_local_translation_should_cache_result(
                literalsPreserved ? (byte)1 : (byte)0,
                result.WeekdaysPreserved ? (byte)1 : (byte)0,
                _options.CacheEntries,
                out byte shouldCache) == 0 && shouldCache != 0)
            {
                lock (_cacheLock)
                {
                    // Configuration/disposal can change while inference is running.
                    if (!_disposed && Volatile.Read(ref _requestedOptions).CanReuseCachedResults(_options))
                    {
                        if (_cache.Remove(fittedKey, out var replaced)) _order.Remove(replaced);
                        while (_cache.Count >= _options.CacheEntries && _order.First is { } oldest)
                        {
                            _order.RemoveFirst();
                            _cache.Remove(oldest.Value.Key);
                        }
                        var node = new LinkedListNode<CacheEntry>(new(fittedKey, result, DateTime.UtcNow.AddMinutes(_options.CacheMinutes)));
                        _cache[fittedKey] = node;
                        _order.AddLast(node);
                    }
                }
            }
            return result with { InputTruncated = tokenTruncated || truncated };
        }
        finally { _gate.Release(); }
    }

    internal TranslationResult? TryGetCachedResult(string text, string source, string target, CancellationToken ct)
    {
        string prepared = PrepareRequest(text, source, target, ct);
        return TryGetCached((prepared, source, target), prepared.Length < text.Length, ct);
    }

    private string PrepareRequest(string text, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        string prepared = TranslationInput.Prepare(text);
        if (TranslationCurrencies.HasConflictingCurrency(prepared, source)) throw new TranslationException("translation.currencyAmbiguous");
        if (!TranslationLanguages.IsSupported(target)) throw new TranslationException("translation.languageUnsupported");
        if (source != "auto" && !TranslationLanguages.IsSupported(source)) throw new TranslationException("translation.languageUnsupported");
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return prepared;
    }

    private TranslationResult? TryGetCached((string Text, string Source, string Target) key, bool truncated, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Cache reads never wait for model loading/inference. A changed output
            // or retention policy must still pass through ApplyOptions under the gate.
            if (!Volatile.Read(ref _requestedOptions).CanReuseCachedResults(_options) ||
                !_cache.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            if (vn_local_translation_is_cache_entry_expired(node.Value.ExpiresUtc.Ticks, DateTime.UtcNow.Ticks, out byte expired) == 0 && expired != 0)
            {
                _cache.Remove(key);
                return null;
            }
            _order.AddLast(node);
            return node.Value.Result with { InputTruncated = node.Value.Result.InputTruncated || truncated };
        }
    }

    internal static TranslationResult? TrySameLanguage(string text, string source, string target)
    {
        if (!TranslationLanguages.IsSupported(target)) return null;
        string? language = source == "auto" ? TranslationLanguages.DetectForPassthrough(text) : source;
        return language == target ? new(text, language, target, true) : null;
    }

    internal static async ValueTask<TranslationResult?> TrySameLanguageAsync(string text, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TranslationLanguages.IsSupported(target)) return null;
        string? language = source == "auto"
            ? await TranslationLanguages.DetectForPassthroughAsync(text, ct).ConfigureAwait(false)
            : source;
        ct.ThrowIfCancellationRequested();
        return language == target ? new(text, language, target, true) : null;
    }

    internal void ReleaseIdleResources()
    {
        if (_disposed || !_gate.Wait(0)) return;
        try
        {
            if (_disposed) return;
            ApplyOptions();
            lock (_cacheLock)
            {
                DateTime now = DateTime.UtcNow;
                var node = _order.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.ExpiresUtc <= now)
                    {
                        _order.Remove(node);
                        _cache.Remove(node.Value.Key);
                    }
                    node = next;
                }
            }
            engine.ReleaseIdleResources();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        engine.Dispose();
        lock (_cacheLock) { _cache.Clear(); _order.Clear(); }
    }
}
