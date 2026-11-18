using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Transformers;

namespace VNotch.Services.Translation;

internal sealed partial class QwenTranslationEngine
{
    private volatile bool _enabled = true;
    private bool _preparedForInference;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) _ = Task.Run(ReleaseDisabledAsync);
    }

    private async Task ReleaseDisabledAsync()
    {
        await _previousDisposal.ConfigureAwait(false);
        await _store.Gate.WaitAsync().ConfigureAwait(false);
        try { if (!_enabled || _disposed) UnloadUnderGate(); }
        finally { _store.Gate.Release(); }
    }

    private async Task PrepareInferenceUnderGateAsync(CancellationToken ct)
    {
        if (_preparedForInference || _gpuLayers == 0 || !NativeApi.llama_supports_gpu_offload()) return;
        _stage = TranslationStage.LoadingModel;
        try
        {
            await Task.Run(async () =>
            {
                // No selected text or cached result: exercise both prefill and single-token
                // GPU kernels so shader creation and lazy allocations happen in the background.
                const string text = "Please translate this short sentence.";
                string prompt;
                if (_store.Profile.PromptKind == TranslationPromptKind.General)
                {
                    var template = new LLamaTemplate(_weights!.NativeHandle) { AddAssistant = true };
                    template.Add("system", "Translate the text into Vietnamese. Return only the translation.");
                    template.Add("user", text);
                    prompt = PromptTemplateTransformer.ToModelPrompt(template);
                }
                else prompt = TranslationModelPrompts.Build(_store.Profile, text, "en", "vi", _options.EnableHumanizer);
                int context = _store.Profile.PromptKind == TranslationPromptKind.TranslateGemma
                    ? Math.Min(2048, _options.ContextSize) : _options.ContextSize;
                await TranslationInference.PrepareAsync(_weights!, Parameters(context), prompt, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _preparedForInference = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Preparing the GPU must retain the same CPU recovery as real inference.
            _backendRecovery.Failed();
            await Task.Run(UnloadUnderGate).ConfigureAwait(false);
            _gpuLayers = 0;
            await LoadUnderGateAsync(ct).ConfigureAwait(false);
        }
    }
}
