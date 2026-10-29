using System.Windows;

namespace VNotch.Services.Translation;

// This flag checks numbers, currencies, codes and links; it is not semantic validation.
internal sealed record TranslationResult(string Text, string SourceLanguage, string TargetLanguage, bool LiteralsPreserved, bool WeekdaysPreserved = true)
{
    internal CurrencyConversion? Currency { get; init; }
    internal bool InputTruncated { get; init; }
}
internal enum TranslationStage { Waiting, CheckingModel, LoadingModel, Translating, FetchingRates }

// Counts are request-local and valid only for the exact tokenizer instance that
// produced them. Reloading/replacing the model requires a fresh count.
internal readonly record struct TranslationEngineInput(string Text, int? TokenCount = null, object? Tokenizer = null);

internal interface ILocalTranslationEngine : IDisposable
{
    void Configure(TranslationOptions options) { }
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task<string> FitInputAsync(string text, CancellationToken cancellationToken) => Task.FromResult(text);
    async Task<TranslationEngineInput> FitForTranslationAsync(string text, CancellationToken cancellationToken) =>
        new(await FitInputAsync(text, cancellationToken).ConfigureAwait(false));
    TranslationStage Stage => TranslationStage.Translating;
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
    Task<string> TranslateFittedAsync(TranslationEngineInput input, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
        TranslateAsync(input.Text, sourceLanguage, targetLanguage, cancellationToken);
    void ReleaseIdleResources();
}

public sealed class TranslationException(string messageKey) : Exception(messageKey)
{
    internal string MessageKey { get; } = messageKey;
}

// Native UIA references stay on the selection worker. The UI receives only this snapshot.
internal sealed record TranslationSelection(long Id, string Text, Rect Bounds, IntPtr Window, bool CanReplace)
{
    internal bool InputTruncated { get; init; }
}
