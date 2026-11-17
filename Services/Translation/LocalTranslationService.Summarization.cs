namespace VNotch.Services.Translation;

internal sealed partial class LocalTranslationService
{
    internal async Task<TranslationResult> SummarizeAsync(TranslationResult input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input.Text)) throw new TranslationException("translation.summaryNoSelection");
        if (input.InputTruncated) throw new TranslationException("translation.summaryIncompleteInput");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplyOptions();
            // Summary inference must never use translation's 512-token prefix,
            // same-language passthrough, structured-text extraction or cache.
            string summary = await engine.SummarizeAsync(input.Text, input.TargetLanguage, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(summary)) throw new TranslationException("translation.summaryFailed");
            return input with
            {
                Text = summary.Trim(), IsSummary = true,
                SummaryReduced = summary.Length < input.Text.Length,
                IsCached = false, UsedCpuFallback = engine.IsCpuFallback,
                InstantPresentation = engine.InstantPresentation, Currency = null
            };
        }
        finally { _gate.Release(); }
    }
}
