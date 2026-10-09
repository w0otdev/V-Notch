namespace VNotch.Services.Translation;

internal sealed class LocalTranslationService(ILocalTranslationEngine engine) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Text, string Source, string Target), TranslationResult> _cache = new();
    private readonly LinkedList<(string Text, string Source, string Target)> _order = new();
    private readonly Dictionary<(string Text, string Source, string Target), DateTime> _expires = new();
    private TranslationOptions _requestedOptions = new();
    private TranslationOptions _options = new();
    internal void Configure(TranslationOptions options) => Volatile.Write(ref _requestedOptions, options);
    private void ApplyOptions()
    {
        var options = Volatile.Read(ref _requestedOptions);
        engine.Configure(options);
        if (_options == options) return;
        _options = options;
        _cache.Clear(); _order.Clear(); _expires.Clear();
    }
    private bool _disposed;
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
        if (string.IsNullOrWhiteSpace(text)) throw new TranslationException("translation.noSelection");
        if (text.Length > 2000) throw new TranslationException("translation.textTooLong");
        if (!TranslationLanguages.IsSupported(target)) throw new TranslationException("translation.languageUnsupported");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyOptions();
            // Auto detection is a model task too: short words and ambiguous scripts
            // must not be rejected or passed through based on a classifier guess.
            if (source != "auto" && !TranslationLanguages.IsSupported(source)) throw new TranslationException("translation.languageUnsupported");
            var key = (text, source, target);
            if (_cache.TryGetValue(key, out var existing))
            {
                _order.Remove(key);
                if (_expires[key] > DateTime.UtcNow) { _order.AddLast(key); return existing; }
                _cache.Remove(key); _expires.Remove(key);
            }
            string translated = source == target ? text : await engine.TranslateAsync(text, source, target, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            translated = TranslationTextIntegrity.Clean(translated);
            if (!TranslationMeaningIntegrity.PreservesStatement(text, source, translated))
                throw new TranslationException("translation.meaningChanged");
            var result = new TranslationResult(translated, source, target, TranslationTextIntegrity.Verify(text, translated),
                TranslationDateIntegrity.PreservesWeekdays(text, source == "auto" ? TranslationLanguages.Detect(text) ?? source : source, translated, target));
            if (_options.CacheEntries > 0)
            {
                while (_cache.Count >= _options.CacheEntries)
                {
                    var oldest = _order.First!.Value;
                    _order.RemoveFirst(); _cache.Remove(oldest); _expires.Remove(oldest);
                }
                _cache[key] = result;
                _expires[key] = DateTime.UtcNow.AddMinutes(_options.CacheMinutes);
                _order.AddLast(key);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    internal void ReleaseIdleResources()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            ApplyOptions();
            foreach (var key in _expires.Where(p => p.Value <= DateTime.UtcNow).Select(p => p.Key).ToArray())
            { _cache.Remove(key); _expires.Remove(key); _order.Remove(key); }
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
        try { _cache.Clear(); _order.Clear(); _expires.Clear(); }
        finally { _gate.Release(); }
    }
}
