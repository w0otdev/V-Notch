using System.Text;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationPhraseTests
{
    public static IEnumerable<object[]> PhraseInputs => new[]
    {
        "YÊU XA", "yêu xa", "Yêu xa!", "yêu xa".Normalize(NormalizationForm.FormD),
        "amour à distance", "amor a distancia", "Fernbeziehung", "حب عن بعد",
        "любовь на расстоянии", "遠距離恋愛", "장거리 연애", "异地恋", "รักทางไกล",
        "प्यार", "ভালোবাসা", "ආදරය", "I love you", "Je t’aime", "爱你", "❤️"
    }.Select(text => new object[] { text });

    [Theory]
    [MemberData(nameof(PhraseInputs))]
    public void MeaningRulesApplyAcrossScriptsCasingAndHumanizerSettings(string text)
    {
        foreach (string source in TranslationLanguages.Codes.Prepend("auto"))
            foreach (bool humanizer in new[] { false, true })
            {
                string instruction = TranslationMeaningIntegrity.Instruction(text, source, humanizer);
                Assert.Contains("Preserve compound meanings", instruction);
                Assert.Contains("every meaning-bearing modifier", instruction);
                Assert.Contains("without expanding a fragment into a personal declaration", instruction);
                Assert.Contains("supported by the source meaning", instruction);
                Assert.Contains("ALL CAPS", instruction);
            }
    }

    [Fact]
    public void EverySpecialistLanguagePairUsesTheSameMeaningRules()
    {
        foreach (var model in TranslationModelCatalog.All.Where(m => m.PromptKind != TranslationPromptKind.General))
            foreach (string source in TranslationLanguages.Codes.Prepend("auto"))
                foreach (string target in TranslationLanguages.Codes)
                    foreach (bool humanizer in new[] { false, true })
                    {
                        // Unknown short Latin input leaves auto-detection to the model.
                        // Hunyuan intentionally rejects language pairs it does not support.
                        string prompt;
                        try { prompt = TranslationModelPrompts.Build(model, "YÊU XA", source, target, humanizer); }
                        catch (TranslationException ex) when (model.PromptKind == TranslationPromptKind.Hunyuan && ex.MessageKey == "translation.languageUnsupported")
                        { continue; }
                        Assert.Contains("Preserve compound meanings", prompt);
                        Assert.Contains("without expanding a fragment into a personal declaration", prompt);
                        Assert.Contains("YÊU XA", prompt);
                        if (source is "auto" or "vi") Assert.Contains("long-distance relationship", prompt);
                    }
    }

    [Theory]
    [InlineData("cảm ơn", "vi")]
    [InlineData("CẢM ƠN", "vi")]
    [InlineData("biết ơn", "vi")]
    [InlineData("YÊU XA", "vi")]
    [InlineData("Yêu xa!", "vi")]
    [InlineData("**YÊU XA**", "vi")]
    [InlineData("장거리 연애", "ko")]
    [InlineData("รักทางไกล", "th")]
    [InlineData("遠く離れた恋", "ja")]
    public void ComposedAndDecomposedTextHaveTheSameLanguageHint(string text, string expected)
    {
        Assert.Equal(expected, TranslationLanguages.Detect(text));
        Assert.Equal(expected, TranslationLanguages.Detect(text.Normalize(NormalizationForm.FormD)));
    }

    [Theory]
    [InlineData("fără")]
    [InlineData("đi")]
    [InlineData("đak")]
    [InlineData("ọmọ")]
    [InlineData("ẹ káàrọ̀")]
    [InlineData("ịhụnanya")]
    public void SharedLatinDiacriticsDoNotForceVietnamese(string text)
    {
        Assert.Null(TranslationLanguages.Detect(text));
    }

    [Theory]
    [InlineData("YÊU XA")]
    [InlineData("yêu  xa")]
    [InlineData("Yêu xa!")]
    [InlineData("\"YÊU XA\"")]
    [InlineData("**YÊU XA**")]
    public void PhraseMeaningIsSharedAcrossModelsAndHumanizerSettings(string text)
    {
        foreach (string source in new[] { "auto", "vi" })
            foreach (bool humanizer in new[] { false, true })
            {
                string instruction = TranslationMeaningIntegrity.Instruction(text, source, humanizer);
                Assert.Contains("long-distance relationship", instruction);
                Assert.Contains("geographic separation", instruction);
            }
    }

    [Theory]
    [InlineData("I love you", "auto")]
    [InlineData("yêu", "auto")]
    [InlineData("xa", "auto")]
    [InlineData("yêu xanh", "auto")]
    [InlineData("Tôi yêu xa", "auto")]
    [InlineData("The title is yêu xa", "auto")]
    [InlineData("YÊU XA", "fr")]
    public void PhraseHintsDoNotOverrideOtherExpressionsOrAnExplicitSource(string text, string source)
    {
        Assert.Null(TranslationPhraseGlossary.Find(text, source));
        Assert.DoesNotContain("Source meaning clarification", TranslationMeaningIntegrity.Instruction(text, source));
    }
}
