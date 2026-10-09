using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace VNotch.Services.Translation;

internal sealed class OnnxTranslationEngine : ILocalTranslationEngine
{
    private readonly TranslationModelStore _store;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private Tokenizer? _tokenizer;
    private Dictionary<string, uint> _languageIds = new();
    private DateTime _lastUsed;
    private bool _disposed;

    internal OnnxTranslationEngine(TranslationModelStore store)
    {
        _store = store;
        _store.Replacing += UnloadUnderGate;
    }

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_store.IsInstalled) throw new TranslationException("translation.modelMissing");
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => Translate(text, sourceLanguage, targetLanguage, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _lastUsed = DateTime.UtcNow; _store.Gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_encoder != null) return;
        await _store.VerifyAsync(ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    InterOpNumThreads = 1,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL
                };
                _tokenizer = TranslationTokenizer.Create(Path.Combine(_store.Root, "tokenizer.json"), _store.Profile.M2M);
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_store.Root, "tokenizer.json")));
                _languageIds = _store.Profile.M2M
                    ? json.RootElement.GetProperty("added_tokens").EnumerateArray()
                        .Where(x => x.GetProperty("content").GetString()?.StartsWith("__", StringComparison.Ordinal) == true)
                        .ToDictionary(x => x.GetProperty("content").GetString()!.Trim('_'), x => x.GetProperty("id").GetUInt32())
                    : json.RootElement.GetProperty("model").GetProperty("vocab").EnumerateArray()
                        .Select((entry, index) => (Token: entry[0].GetString()!, Id: (uint)index))
                        .Where(entry => entry.Token.StartsWith("<2", StringComparison.Ordinal) && entry.Token.EndsWith('>'))
                        .ToDictionary(entry => entry.Token[2..^1], entry => entry.Id);
                _encoder = new InferenceSession(Path.Combine(_store.Root, "encoder.onnx"), options);
                _decoder = new InferenceSession(Path.Combine(_store.Root, "decoder.onnx"), options);
                if (!_decoder.InputMetadata.ContainsKey("use_cache_branch")) throw new TranslationException("translation.modelInvalid");
            }
            catch { UnloadUnderGate(); throw; }
            // A newer selection may cancel while native session creation is running.
            // Keep the successfully loaded sessions so that request can reuse them.
            ct.ThrowIfCancellationRequested();
        }, ct).ConfigureAwait(false);
    }

    private string Translate(string text, string source, string target, CancellationToken ct)
    {
        if (!_store.Profile.M2M) target = TranslationLanguages.ModelTarget(target);
        if ((_store.Profile.M2M && !_languageIds.ContainsKey(source)) || !_languageIds.TryGetValue(target, out uint targetId))
            throw new TranslationException("translation.languageUnsupported");
        var tokens = _store.Profile.M2M ? TranslationTokenizer.Encode(_tokenizer!, text) : _tokenizer!.Encode($"<2{target}> {text}");
        if (tokens.Length < 2 || tokens.Length > 512) throw new TranslationException("translation.textTooLong");
        if (_store.Profile.M2M) tokens[0] = _languageIds[source];
        var ids = new DenseTensor<long>(tokens.Select(x => (long)x).ToArray(), new[] { 1, tokens.Length });
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, tokens.Length).ToArray(), new[] { 1, tokens.Length });
        using var run = new RunOptions();
        using var registration = ct.Register(() => run.Terminate = true);
        ct.ThrowIfCancellationRequested();
        using var encoded = _encoder!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask)
        }, _encoder.OutputMetadata.Keys.ToArray(), run);
        var hidden = encoded.First(x => x.Name == "last_hidden_state").AsTensor<float>();
        var cache = _decoder!.InputMetadata.Keys.Where(name => name.StartsWith("past_key_values.", StringComparison.Ordinal))
            .ToDictionary(name => name, _ => (Tensor<float>)new DenseTensor<float>(new[] { 1, 16, 0, _store.Profile.HeadSize }));
        var search = new TranslationBeamSearch(_store.Profile.M2M ? targetId : 0, _store.Profile.Beams);
        uint[] last = [_store.Profile.M2M ? 2U : 0U];
        bool cached = false;
        bool ended = false;
        for (int step = 0; step < Math.Min(384, tokens.Length * 3 + 32); step++)
        {
            ct.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(last.Select(x => (long)x).ToArray(), new[] { last.Length, 1 })),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", last.Length == 1 ? mask : GatherRows(mask, Enumerable.Repeat(0, last.Length).ToArray())),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", last.Length == 1 ? hidden : GatherRows(hidden, Enumerable.Repeat(0, last.Length).ToArray())),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { cached }, new[] { 1 }))
            };
            inputs.AddRange(cache.Select(pair => NamedOnnxValue.CreateFromTensor(pair.Key, pair.Value)));
            using var output = _decoder.Run(inputs, _decoder.OutputMetadata.Keys.ToArray(), run);
            var logits = output.First(x => x.Name == "logits").AsTensor<float>();
            bool forcedLanguage = _store.Profile.M2M && step == 0;
            int[] parents = forcedLanguage ? [0] : search.Advance(logits.ToArray(), logits.Dimensions[^1]);
            if (!forcedLanguage && search.IsDone) { ended = true; break; }
            last = search.LastTokens;
            foreach (var value in output.Where(x => x.Name.StartsWith("present.", StringComparison.Ordinal)))
            {
                string name = value.Name.Replace("present.", "past_key_values.", StringComparison.Ordinal);
                if (cached && name.Contains(".encoder.", StringComparison.Ordinal)) continue;
                var tensor = value.AsTensor<float>();
                cache[name] = new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
            }
            foreach (string name in cache.Keys.ToArray()) cache[name] = GatherRows(cache[name], parents);
            cached = true;
        }
        ct.ThrowIfCancellationRequested();
        if (!ended) throw new TranslationException("translation.textTooLong");
        return _tokenizer!.Decode(search.Result);
    }

    private static DenseTensor<T> GatherRows<T>(Tensor<T> tensor, int[] rows)
    {
        int[] shape = tensor.Dimensions.ToArray();
        int stride = checked((int)tensor.Length / shape[0]);
        shape[0] = rows.Length;
        T[] source = tensor.ToArray(), result = new T[rows.Length * stride];
        for (int row = 0; row < rows.Length; row++) Array.Copy(source, rows[row] * stride, result, row * stride, stride);
        return new DenseTensor<T>(result, shape);
    }

    public void ReleaseIdleResources()
    {
        if (DateTime.UtcNow - _lastUsed < TimeSpan.FromMinutes(2) || !_store.Gate.Wait(0)) return;
        try { UnloadUnderGate(); }
        finally { _store.Gate.Release(); }
    }

    private void UnloadUnderGate()
    {
        _encoder?.Dispose(); _encoder = null;
        _decoder?.Dispose(); _decoder = null;
        _tokenizer?.Dispose(); _tokenizer = null;
        _languageIds.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Replacing -= UnloadUnderGate;
        // The controller cancels active inference first; a short queued cleanup owns no UI work.
        _ = DisposeSessionsAsync();
    }

    private async Task DisposeSessionsAsync()
    {
        await _store.Gate.WaitAsync().ConfigureAwait(false);
        try { UnloadUnderGate(); }
        finally { _store.Gate.Release(); }
    }
}
