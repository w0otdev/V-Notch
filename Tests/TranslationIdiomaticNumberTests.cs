using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationIdiomaticNumberTests
{
    [Theory]
    [InlineData("Vọc vạch 1 tí.")]
    [InlineData("Vọc vạch 1 tý.")]
    [InlineData("Thử nghiệm 1 chút.")]
    [InlineData("1 TÍ thôi.")]
    public void NonQuantitativeOneCanBecomeALittle(string source)
    {
        const string translated = "Experiment a little.";
        var protectedText = TranslationTextIntegrity.Protect(source);
        Assert.Equal(source, protectedText.Text);
        Assert.Empty(protectedText.Literals);
        Assert.Equal(translated, protectedText.Restore(translated));
        Assert.True(TranslationTextIntegrity.Verify(source, translated));
        Assert.True(TranslationTextIntegrity.Verify(translated, source));
    }

    [Theory]
    [InlineData("1 tỷ")]
    [InlineData("1 VND")]
    [InlineData("$1")]
    [InlineData("¥1 tí")]
    [InlineData("-1 tí")]
    [InlineData("+1 tí")]
    [InlineData("1.1 tí")]
    [InlineData("11 tí")]
    [InlineData("2 tí")]
    [InlineData("item1 tí")]
    [InlineData("AB-1 tí")]
    [InlineData("https://example.com/1 tí")]
    public void RealNumbersAndIdentifiersCannotBeDropped(string source)
    {
        Assert.False(TranslationTextIntegrity.Verify(source, "A little."));
        var protectedText = TranslationTextIntegrity.Protect(source);
        Assert.NotEmpty(protectedText.Literals);
        Assert.Throws<TranslationException>(() => protectedText.Restore("A little."));
    }

    [Theory]
    [InlineData("Experiment a little with 16 GB of RAM for 80 USD.", true)]
    [InlineData("Experiment a little with 8 GB of RAM for 80 USD.", false)]
    [InlineData("Experiment a little with 16 GB of RAM for 80 VND.", false)]
    [InlineData("Experiment a little with 16 GB of RAM.", false)]
    [InlineData("Experiment a little with 16 GB of RAM for 80 USD and 42 more.", false)]
    public void IdiomsDoNotWeakenProtectionOfOtherFacts(string translated, bool safe)
    {
        const string source = "Vọc 1 tí với 16 GB RAM, giá 80 USD.";
        Assert.Equal(safe, TranslationTextIntegrity.Verify(source, translated));
        var protectedText = TranslationTextIntegrity.Protect(source);
        Assert.Contains("1 tí", protectedText.Text);
        Assert.Equal(2, protectedText.Literals.Count);
        Assert.Equal(source, protectedText.Restore(protectedText.Text));
    }
}
