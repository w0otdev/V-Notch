using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationTextFormattingTests
{
    [Fact]
    public void FormattingCanBeToggledWithoutRetranslating() => SharedStaTestRunner.RunAsync(ct =>
    {
        var window = new TranslationWindow("en", "vi");
        try
        {
            string text = new string('a', 400) + "\nSecond paragraph.";
            window.ShowResult(new(text, "en", "vi", true));
            Assert.Equal(15, window.ResultText.FontSize);
            Assert.Contains("\n\n", window.ResultText.Text);
            window.ConfigureFormatting(false);
            Assert.Equal(text, window.ResultText.Text);
            window.ConfigureFormatting(true);
            Assert.Equal(TranslationTextFormatting.Format(text), window.ResultText.Text);
            window.ShowResult(new("Short text", "en", "vi", true));
            Assert.Equal(19, window.ResultText.FontSize);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("First paragraph.\r\nSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    [InlineData("First.\n\n \nSecond.", "First.\n\nSecond.")]
    [InlineData("Heading\r• Item (0.6.1)\r• Item 2", "Heading\n\n• Item (0.6.1)\n\n• Item 2")]
    [InlineData("Single paragraph. No changes.", "Single paragraph. No changes.")]
    [InlineData("", "")]
    public void PreservesContentAndNormalizesParagraphSpacing(string input, string expected)
    {
        Assert.Equal(expected, TranslationTextFormatting.Format(input));
        Assert.Equal(expected, TranslationTextFormatting.Format(expected));
    }
}
