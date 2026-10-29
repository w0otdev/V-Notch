using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationWrittenNumberTests
{
    [Theory]
    [InlineData("1 Tuần chỉ có 2 ngày, trong ngày chỉ có 1 khung giờ tốt nhất. Thuộc nhà 10 và nhà 1.",
        "There are only two days a week, with one best time slot per day. It belongs to the tenth and first houses.")]
    [InlineData("Có 2 ngày và 10 người.", "There are two days and 10 people.")]
    [InlineData("Có 2 ngày và 2 giờ.", "There are two days and 2 hours.")]
    [InlineData("Có 16 người.", "There are sixteen people.")]
    [InlineData("Có 2 ngày.", "There are 3 days.")]
    [InlineData("Có 2 ngày.", "There are 2 days and 2 hours.")]
    [InlineData("Có 80 USD.", "There are eighty dollars.")]
    [InlineData("Giá -2.", "Price two.")]
    [InlineData("Giá 2.5.", "Price two point five.")]
    public async Task WrittenQuantitiesRemainReadableButUnverifiedAndUncached(string source, string output)
    {
        using var engine = new Engine(output);
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var result = await service.TranslateAsync(source, "vi", "en", default);
            Assert.Equal(output, result.Text);
            Assert.False(result.LiteralsPreserved);
        }
        Assert.Equal(2, engine.Calls);
        Assert.Equal(output, TranslationTextIntegrity.Protect(source).Restore(output, allowUnverifiedNumbers: true));
    }

    [Theory]
    [InlineData("Mã AB-12.", "Code twelve.")]
    [InlineData("Mã item12.", "Code twelve.")]
    [InlineData("Mở https://example.com/2", "Open the second page.")]
    public async Task ProtectedLiteralsStillFail(string source, string output)
    {
        Assert.False(TranslationTextIntegrity.CanPresentUnverifiedNumbers(source, output));
        Assert.Throws<TranslationException>(() => TranslationTextIntegrity.Protect(source).Restore(output, allowUnverifiedNumbers: true));
        using var engine = new Engine(output);
        using var service = new LocalTranslationService(engine);
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(source, "vi", "en", default));
    }

    [Fact]
    public void KnownPlaceholdersRestoreAndUnknownPlaceholdersCannotReachTheUser()
    {
        var input = TranslationTextIntegrity.Protect("Có 2 ngày.");
        Assert.Equal("There are 2 days.", input.Restore("There are __VNT_LITERAL0__ days.", allowUnverifiedNumbers: true));
        Assert.Throws<TranslationException>(() => input.Restore("There are __VNT_LITERAL99__ days.", allowUnverifiedNumbers: true));
        Assert.False(TranslationTextIntegrity.CanPresentUnverifiedNumbers("Có 2 ngày.", "There are __VNT_LITERAL0__ days."));
    }

    [Fact]
    public void UnverifiedResultCanBeReadAndCopiedButNotReplaced() => SharedStaTestRunner.RunAsync(ct =>
    {
        var window = new TranslationWindow("vi", "en");
        try
        {
            window.ShowResult(new("There are two days a week.", "vi", "en", false));
            Assert.Equal("There are two days a week.", window.ResultText.Text);
            Assert.True(window.CopyButton.IsEnabled);
            Assert.False(window.ReplaceButton.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(window.StatusText.Text));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(100)]
    public async Task DenseNumericPassagesDoNotExpandIntoPlaceholdersOrBlockTheResult(int count)
    {
        string source = "Các số đo: " + string.Join("; ", Enumerable.Range(1, count).Select(i => $"{i},5%")) + ".";
        string output = "Measurements: " + string.Join("; ", Enumerable.Range(1, count).Select(i => $"{i}.5 percent")) + ".";
        var input = TranslationTextIntegrity.Protect(source, protectNumbers: false);
        Assert.Equal(source, input.Text);
        Assert.Empty(input.Literals);
        Assert.Equal(output, input.Restore(output, allowUnverifiedNumbers: true));
        using var engine = new Engine(output);
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync(source, "vi", "en", default);
        Assert.Equal(output, result.Text);
        Assert.False(result.LiteralsPreserved);
    }

    [Theory]
    [InlineData("Ngày 11/10/2026 lúc 08:30, giá 1.234,56 EUR, giảm 12,5%.",
        "On October 11, 2026 at 8:30 AM, the price is EUR 1,234.56, discounted by 12.5 percent.")]
    [InlineData("Có hai ngày, ba giờ và mười người.", "There are 2 days, 3 hours and 10 people.")]
    [InlineData("Nhiệt độ −12,5 đến +20,5 độ.", "Temperatures from -12.5 to +20.5 degrees.")]
    [InlineData("共有12人，费用80元，持续3天。", "There are twelve people, costing eighty yuan, over three days.")]
    [InlineData("Có １２ người và ٣ ngày.", "There are 12 people and 3 days.")]
    [InlineData("Có 12 người và 3 ngày.", "There are １２ people and ٣ days.")]
    [InlineData("Giá ١٢٫٥.", "Price 12.5.")]
    public async Task NumericRepresentationsAcrossLanguagesAreReviewable(string source, string output)
    {
        using var engine = new Engine(output);
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var result = await service.TranslateAsync(source, "vi", "en", default);
            Assert.Equal(output, result.Text);
            Assert.False(result.LiteralsPreserved);
        }
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public void UnchangedUnicodeNumeralsRemainVerifiable() =>
        Assert.True(TranslationTextIntegrity.Verify("Có １２ người và ٣ ngày.", "There are １２ people and ٣ days."));

    [Fact]
    public void NumericInputStillProtectsLinksAndIdentifiers()
    {
        const string source = "Có 10 người, 20 ngày, mã AB-12, item34, https://123.example.com/56 và user78@example.com.";
        var input = TranslationTextIntegrity.Protect(source, protectNumbers: false);
        Assert.Contains("10 người, 20 ngày", input.Text);
        Assert.Equal(4, input.Literals.Count);
        Assert.Equal(source, input.Restore(input.Text, allowUnverifiedNumbers: true));
        Assert.False(TranslationTextIntegrity.CanPresentUnverifiedNumbers(source, "There are 10 people and 20 days."));
        Assert.False(TranslationTextIntegrity.Verify("Có 2 ngày.", "There are __VNT_LITERAL2__ days."));
    }

    private sealed class Engine(string output) : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(output); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
