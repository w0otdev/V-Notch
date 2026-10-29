using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationMeaningIntegrityTests
{
    [Theory]
    [InlineData("it's about $80 VND")]
    [InlineData("$80 VND")]
    [InlineData("It costs EUR 80 USD.")]
    public async Task ConflictingSourceCurrencyIsRejectedBeforeInference(string input)
    {
        using var engine = new WrongMeaningEngine();
        using var service = new LocalTranslationService(engine);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(input, "auto", "vi", default));
        Assert.Equal("translation.currencyAmbiguous", error.MessageKey);
        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData("It's about 80 VND", "Giá khoảng 80.000 đồng Việt Nam.")]
    [InlineData("It's about $80", "Giá khoảng 80.000 VND.")]
    [InlineData("80 USD", "80 VND")]
    [InlineData("Invoice 80", "Hóa đơn 80000")]
    public async Task UnverifiedNumbersAreFlaggedAndNotCached(string source, string output)
    {
        using var engine = new LiteralEngine(output);
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var result = await service.TranslateAsync(source, "auto", "vi", default);
            Assert.Equal(output, result.Text);
            Assert.False(result.LiteralsPreserved);
        }
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData("It's about $80 CAD", "Giá khoảng $80 CAD")]
    [InlineData("It's about 80 VND", "Giá khoảng 80 VND")]
    [InlineData("It's about $80", "Giá khoảng $80")]
    public async Task PreservedPricesInSentencesRemainValid(string source, string output)
    {
        using var engine = new LiteralEngine(output);
        using var service = new LocalTranslationService(engine);
        Assert.Equal(output, (await service.TranslateAsync(source, "auto", "vi", default)).Text);
    }

    [Fact]
    public void RestoredPlaceholderCannotAddAnInventedAmount()
    {
        var input = TranslationTextIntegrity.Protect("About $80");
        Assert.Throws<TranslationException>(() => input.Restore(input.Text + " 80000 VND"));
    }

    private sealed class LiteralEngine(string output) : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(output); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }

    private const string Announcement = "Các bạn đang theo dõi giải đấu VALORANT Champions Shanghai, giải đấu quy tụ 16 đội xuất sắc nhất Thế giới";

    [Theory]
    [InlineData("Champions")]
    [InlineData("Hello")]
    [InlineData("Refund")]
    [InlineData("Bonjour")]
    [InlineData("商品発送")]
    public void TranslateGemmaAutoDoesNotRejectShortWordsOrPretendTheyAreAllEnglish(string text)
    {
        string prompt = TranslationModelPrompts.Build(TranslationModelCatalog.Find("translategemma-4b"), text, "auto", "vi");
        Assert.Contains(text, prompt);
        Assert.Contains("Identify the language", prompt);
        Assert.DoesNotContain("English (en) to", prompt);
    }

    [Fact]
    public void AnnouncementKeepsItsAddresseeAndCannotAcquireAQuestionMark()
    {
        Assert.Equal("You", TranslationMeaningIntegrity.EnglishStatementSubject(Announcement, "auto", "en"));
        Assert.False(TranslationMeaningIntegrity.PreservesStatement(Announcement, "auto", "Are you following the tournament?"));
        Assert.True(TranslationMeaningIntegrity.PreservesStatement(Announcement, "auto", "You are watching the tournament."));
    }

    [Theory]
    [InlineData("Các bạn có đang theo dõi giải đấu này không?")]
    [InlineData("Các bạn có đang theo dõi giải đấu này không")]
    [InlineData("Bạn đang làm gì")]
    [InlineData("Bạn đã xem chưa")]
    public void GenuineInformalQuestionsAreNeverConstrainedIntoStatements(string text)
    {
        Assert.Null(TranslationMeaningIntegrity.EnglishStatementSubject(text, "vi", "en"));
        Assert.True(TranslationMeaningIntegrity.PreservesStatement(text, "vi", "Are you watching?"));
    }

    [Fact]
    public async Task WrongSentenceTypeIsNotCachedOrPresentedAsACompletedTranslation()
    {
        using var engine = new WrongMeaningEngine();
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(Announcement, "vi", "en", default));
            Assert.Equal("translation.meaningChanged", error.MessageKey);
        }
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public void InstructionIncludesEmojiMarkdownAndHumanizerGuidelines()
    {
        string instruction = TranslationMeaningIntegrity.Instruction("Quá là đ@u đớn rồi đấy sếp ơi 😭", "vi");
        Assert.Contains("emojis", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Markdown", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("human writer", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI chatbot", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstructionOmitsHumanizerGuidelinesWhenDisabled()
    {
        string instruction = TranslationMeaningIntegrity.Instruction("Quá là đ@u đớn rồi đấy sếp ơi 😭", "vi", enableHumanizer: false);
        Assert.Contains("emojis", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Markdown", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human writer", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AI chatbot", instruction, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WrongMeaningEngine : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult("Are you following this tournament?"); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
