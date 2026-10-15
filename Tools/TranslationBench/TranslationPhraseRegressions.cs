using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VNotch.Services.Translation;

internal static class TranslationPhraseRegressions
{
    internal static async Task RunAsync()
    {
        const string distanceMeaning = @"long[ -]distance|love (?:from|at) a distance|love from afar|distant love";
        var store = TranslationModelStore.For("translategemma-4b");
        using var engine = new QwenTranslationEngine(store);
        using var service = new LocalTranslationService(engine);
        var cases = new (string Source, string Target, string Text, string Expected)[]
        {
            ("auto", "en", "YÊU XA", distanceMeaning),
            ("vi", "en", "YÊU XA", distanceMeaning),
            ("auto", "en", "yêu xa", distanceMeaning),
            ("auto", "en", "Yêu xa!", distanceMeaning),
            ("auto", "en", "yêu xa".Normalize(NormalizationForm.FormD), distanceMeaning),
            ("auto", "en", "amour à distance", distanceMeaning),
            ("auto", "en", "amor a distancia", distanceMeaning),
            ("auto", "en", "Fernbeziehung", @"long[ -]distance"),
            ("auto", "en", "遠距離恋愛", @"long[ -]distance"),
            ("auto", "en", "장거리 연애", @"long[ -]distance"),
            ("auto", "en", "异地恋", @"long[ -]distance"),
            ("auto", "en", "любовь на расстоянии", distanceMeaning),
            ("auto", "en", "حب عن بعد", distanceMeaning + "|remote love"),
            ("vi", "fr", "YÊU XA", @"distance|éloigné"),
            ("vi", "de", "YÊU XA", @"Fern|Distanz|Entfernung"),
            ("vi", "es", "YÊU XA", @"distancia"),
            ("vi", "ja", "YÊU XA", @"遠距離"),
            ("vi", "ko", "YÊU XA", @"장거리|원거리"),
            ("vi", "zh", "YÊU XA", @"异地|異地|远距离|遠距離"),
            ("en", "vi", "I love you", @"(?:tôi|anh|mình|tớ).*yêu.*(?:bạn|em|cậu)"),
            ("en", "ja", "I love you", @"愛|好き"),
        };
        try
        {
            foreach (var (source, target, text, expected) in cases)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var timer = Stopwatch.StartNew();
                try
                {
                    var result = await service.TranslateAsync(text, source, target, timeout.Token);
                    bool passed = Regex.IsMatch(result.Text.Normalize(), expected, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (target == "en" && Regex.IsMatch(result.Text, @"\b(?:I (?:love|miss) you|my love for you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        passed = false;
                    if (!passed) Environment.ExitCode = 1;
                    Console.WriteLine(JsonSerializer.Serialize(new { source, target, text, result = result.Text, passed, seconds = timer.Elapsed.TotalSeconds }));
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    Console.WriteLine(JsonSerializer.Serialize(new { source, target, text, error = ex is TranslationException e ? e.MessageKey : ex.GetType().Name, seconds = timer.Elapsed.TotalSeconds }));
                }
            }
        }
        finally { service.Dispose(); await engine.Disposal; }
    }
}
