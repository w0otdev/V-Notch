using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationMeaningIntegrityTests
{
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

    private sealed class WrongMeaningEngine : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
        { Calls++; return Task.FromResult("Are you following this tournament?"); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
