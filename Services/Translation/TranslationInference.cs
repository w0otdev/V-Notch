using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Native;

namespace VNotch.Services.Translation;

/// <summary>One bounded request owns exactly one native context and its decode buffers.</summary>
internal static class TranslationInference
{
    internal static async Task PrepareAsync(LLamaWeights weights, ModelParams parameters, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var context = weights.CreateContext(parameters);
        var tokens = context.Tokenize(prompt, special: true).ToList();
        if (tokens.Count == 0 || tokens.Count + 1 > context.ContextSize)
            throw new TranslationException("translation.textTooLong");
        var batch = new LLamaBatch();
        var (result, _, position) = await context.DecodeAsync(tokens, LLamaSeqId.Zero, batch, 0, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result != DecodeResult.Ok) throw new TranslationException("translation.failed");
        batch.Clear();
        batch.Add(tokens[^1], position, LLamaSeqId.Zero, true);
        result = await context.DecodeAsync(batch, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result != DecodeResult.Ok) throw new TranslationException("translation.failed");
    }

    internal static async IAsyncEnumerable<string> RunAsync(LLamaWeights weights, ModelParams parameters,
        string prompt, InferenceParams options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var context = weights.CreateContext(parameters);
        var batch = new LLamaBatch();
        var tokens = context.Tokenize(prompt, special: true).ToList();
        if (tokens.Count + options.MaxTokens > context.ContextSize)
            throw new TranslationException("translation.textTooLong");
        options.SamplingPipeline.Reset();
        var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = options.DecodeSpecialTokens };
        var stops = new AntipromptProcessor(options.AntiPrompts);
        var (result, _, position) = await context.DecodeAsync(tokens, LLamaSeqId.Zero, batch, 0, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != DecodeResult.Ok) throw new TranslationException("translation.failed");
        for (int i = 0; i < options.MaxTokens; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = options.SamplingPipeline.Sample(context.NativeHandle, batch.TokenCount - 1);
            if (token.IsEndOfGeneration(weights.Vocab)) yield break;
            decoder.Add(token);
            string text = decoder.Read();
            yield return text;
            if (stops.Add(text)) yield break;
            batch.Clear();
            batch.Add(token, position++, LLamaSeqId.Zero, true);
            var decode = await context.DecodeAsync(batch, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (decode != DecodeResult.Ok)
                throw new TranslationException("translation.failed");
        }
    }
}
