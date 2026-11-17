using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;

namespace VNotch.Services.Translation;

internal sealed partial class QwenTranslationEngine
{
    private static readonly Grammar SummaryGrammar = new("""
        root ::= "{" ws "\"summary\"" ws ":" ws string ws "}" ws
        string ::= "\"" char* "\""
        char ::= [^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])
        ws ::= [ \t\n\r]*
        """, "root");

    public async Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_store.Profile.IsLite || _store.Profile.PromptKind != TranslationPromptKind.General)
            throw new TranslationException("translation.summaryModelRequired");
        _stage = TranslationStage.Waiting;
        await _previousDisposal.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return await Task.Run(async () =>
            {
                try { return await SummarizeUnderGateAsync(text, language, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (_gpuLayers > 0 && ex is not TranslationException && ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    _backendRecovery.Failed();
                    UnloadUnderGate();
                    _gpuLayers = 0;
                    await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
                    return await SummarizeUnderGateAsync(text, language, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (!_enabled) await Task.Run(UnloadUnderGate).ConfigureAwait(false); }
            finally { _lastUsed = DateTime.UtcNow; _store.Gate.Release(); }
        }
    }

    private Task<string> SummarizeUnderGateAsync(string text, string language, CancellationToken ct)
    {
        _stage = TranslationStage.Translating;
        // Count the complete model template, JSON escaping and output allowance.
        // Each chunk therefore fits without trimming any source text at inference.
        int outputBudget = Math.Min(MaxOutputTokens, _options.ContextSize / 3);
        string instruction = LocalSummarizationService.Instruction(language);
        string Prompt(string input)
        {
            ct.ThrowIfCancellationRequested();
            var template = new LLamaTemplate(_weights!.NativeHandle) { AddAssistant = true };
            template.Add("system", instruction);
            template.Add("user", LocalSummarizationService.Input(input));
            return PromptTemplateTransformer.ToModelPrompt(template);
        }
        bool Fits(string input) => _weights!.Tokenize(Prompt(input), true, true, Encoding.UTF8).Length + outputBudget + 16 <= _options.ContextSize;
        return LocalSummarizationService.SummarizeAsync(text, Fits, async (input, token) =>
        {
            var output = new StringBuilder();
            var boundary = new TranslationJsonBoundary();
            using var sampling = new DefaultSamplingPipeline
            { Temperature = .2f, TopP = .8f, TopK = 20, MinP = 0, Seed = 42, Grammar = SummaryGrammar };
            await foreach (string part in TranslationInference.RunAsync(_weights!, Parameters(), Prompt(input),
                new InferenceParams { MaxTokens = outputBudget, SamplingPipeline = sampling }, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                output.Append(part);
                if (boundary.Append(part)) return LocalSummarizationService.ReadOutput(output.ToString());
            }
            token.ThrowIfCancellationRequested();
            // An unfinished JSON response is not a completed summary.
            throw new TranslationException("translation.summaryFailed");
        }, ct);
    }
}
