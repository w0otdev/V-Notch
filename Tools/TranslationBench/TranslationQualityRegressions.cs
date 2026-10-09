using System.Diagnostics;
using System.Text.Json;
using VNotch.Services.Translation;

internal static class TranslationQualityRegressions
{
    internal static async Task RunAsync()
    {
        var store = TranslationModelStore.For("translategemma-4b");
        using var engine = new QwenTranslationEngine(store);
        using var service = new LocalTranslationService(engine);
        foreach (var (source, target, text) in new[]
        {
            ("auto", "vi", "Champions"),
            ("en", "vi", "Champions"),
            ("auto", "vi", "Hello"),
            ("auto", "vi", "Refund"),
            ("auto", "vi", "Bonjour"),
            ("auto", "en", "Các bạn đang theo dõi giải đấu VALORANT Champions Shanghai, giải đấu quy tụ 16 đội xuất sắc nhất Thế giới"),
            ("vi", "en", "Các bạn đang theo dõi giải đấu VALORANT Champions Shanghai, giải đấu quy tụ 16 đội xuất sắc nhất Thế giới"),
            ("vi", "en", "Các bạn có đang theo dõi giải đấu này không?"),
        })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await service.TranslateAsync(text, source, target, timeout.Token);
                if (string.IsNullOrWhiteSpace(result.Text) || (text == "Champions" && source == "auto" && !result.Text.Contains("vô địch", StringComparison.OrdinalIgnoreCase)) ||
                    (text.StartsWith("Các bạn đang", StringComparison.Ordinal) && (!result.Text.StartsWith("You ", StringComparison.Ordinal) || result.Text.Contains('?') || !result.Text.Contains("16"))) ||
                    (text.StartsWith("Các bạn có", StringComparison.Ordinal) && !result.Text.Contains('?')))
                    throw new InvalidOperationException("Translation regression failed.");
                Console.WriteLine(JsonSerializer.Serialize(new { source, target, text, result = result.Text, seconds = timer.Elapsed.TotalSeconds }));
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.WriteLine(JsonSerializer.Serialize(new { source, target, text, error = ex is TranslationException e ? e.MessageKey : ex.GetType().Name, seconds = timer.Elapsed.TotalSeconds }));
            }
        }
    }
}
