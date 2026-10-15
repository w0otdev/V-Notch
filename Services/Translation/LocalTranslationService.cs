namespace VNotch.Services.Translation;

internal sealed class LocalTranslationService(ILocalTranslationEngine engine) : IDisposable
{
    private sealed record CacheEntry((string Text, string Source, string Target) Key, TranslationResult Result, DateTime ExpiresUtc);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Text, string Source, string Target), LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _order = new();
    private TranslationOptions _requestedOptions = new();
    private TranslationOptions _options = new();
    internal void Configure(TranslationOptions options) => Volatile.Write(ref _requestedOptions, options);
    private void ApplyOptions()
    {
        var options = Volatile.Read(ref _requestedOptions);
        engine.Configure(options);
        if (_options == options) return;
        _options = options;
        _cache.Clear(); _order.Clear();
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

    internal async Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken ct)
    {
        string prepared = TranslationInput.Prepare(text);
        bool truncated = prepared.Length < text.Length;
        text = prepared;
        if (TranslationCurrencies.HasConflictingCurrency(text, source)) throw new TranslationException("translation.currencyAmbiguous");
        if (!TranslationLanguages.IsSupported(target)) throw new TranslationException("translation.languageUnsupported");
        if (source != "auto" && !TranslationLanguages.IsSupported(source)) throw new TranslationException("translation.languageUnsupported");
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (TranslationStructuredText.IsOnlyIcons(text))
            return new(text, source, target, true) { InputTruncated = truncated };
        if (TrySameLanguage(text, source, target) is { } unchanged)
        {
            ct.ThrowIfCancellationRequested();
            return unchanged with { InputTruncated = truncated };
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyOptions();
            var key = (text, source, target);
            if (_cache.TryGetValue(key, out var existingNode))
            {
                _order.Remove(existingNode);
                if (existingNode.Value.ExpiresUtc > DateTime.UtcNow)
                {
                    _order.AddLast(existingNode);
                    return existingNode.Value.Result with { InputTruncated = existingNode.Value.Result.InputTruncated || truncated };
                }
                _cache.Remove(key);
            }
            string fitted = await engine.FitInputAsync(text, ct).ConfigureAwait(false);
            bool tokenTruncated = fitted.Length < text.Length;
            text = fitted;
            string translated = await TranslationStructuredText.TranslateAsync(text,
                (prose, token) => engine.TranslateAsync(prose, source, target, token), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            translated = TranslationTextIntegrity.Clean(translated);
            if (!TranslationMeaningIntegrity.PreservesStatement(text, source, translated))
                throw new TranslationException("translation.meaningChanged");
            // Numeric mismatches remain readable for review, without replacement or caching.
            bool literalsPreserved = TranslationTextIntegrity.Verify(text, translated);
            if (!literalsPreserved && !TranslationTextIntegrity.CanPresentUnverifiedNumbers(text, translated))
                throw new TranslationException("translation.literalsChanged");
            var result = new TranslationResult(translated, source, target, literalsPreserved,
                TranslationDateIntegrity.PreservesWeekdays(text, source == "auto" ? TranslationLanguages.Detect(text) ?? source : source, translated, target))
            { InputTruncated = tokenTruncated };
            if (literalsPreserved && _options.CacheEntries > 0)
            {
                while (_cache.Count >= _options.CacheEntries && _order.First is { } oldest)
                {
                    _order.RemoveFirst();
                    _cache.Remove(oldest.Value.Key);
                }
                var node = new LinkedListNode<CacheEntry>(new(key, result, DateTime.UtcNow.AddMinutes(_options.CacheMinutes)));
                _cache[key] = node;
                _order.AddLast(node);
            }
            return result with { InputTruncated = tokenTruncated || truncated };
        }
        finally { _gate.Release(); }
    }

    internal static TranslationResult? TrySameLanguage(string text, string source, string target)
    {
        if (!TranslationLanguages.IsSupported(target)) return null;
        string? language = source == "auto" ? TranslationLanguages.DetectForPassthrough(text) : source;
        return language == target ? new(text, language, target, true) : null;
    }

    internal void ReleaseIdleResources()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            ApplyOptions();
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
            engine.ReleaseIdleResources();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        engine.Dispose();
        _ = ClearCacheAsync();
    }

    private async Task ClearCacheAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _cache.Clear(); _order.Clear(); }
        finally { _gate.Release(); }
    }
}
