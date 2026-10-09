using System.Windows;

namespace VNotch.Services.Translation;

// This flag checks numbers, currencies, codes and links; it is not semantic validation.
internal sealed record TranslationResult(string Text, string SourceLanguage, string TargetLanguage, bool LiteralsPreserved, bool WeekdaysPreserved = true);
internal enum TranslationStage { Waiting, CheckingModel, LoadingModel, Translating }

internal interface ILocalTranslationEngine : IDisposable
{
    void Configure(TranslationOptions options) { }
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    TranslationStage Stage => TranslationStage.Translating;
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
    void ReleaseIdleResources();
}

internal sealed class TranslationException(string messageKey) : Exception(messageKey)
{
    internal string MessageKey { get; } = messageKey;
}

// Native UIA references stay on the selection worker. The UI receives only this snapshot.
internal sealed record TranslationSelection(long Id, string Text, Rect Bounds, IntPtr Window, bool CanReplace);
