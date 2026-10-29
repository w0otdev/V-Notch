using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationGiveawayRegressionTests
{
    private const string Source = """
Post xin lỗi mng vì bài GA hno nhiều số quá nên mình không có lọc được hết số 1 cách chính xác [😭](https://static.xx.fbcdn.net/images/emoji.php/v9/t40/1/16/1f62d.png)&#x20;
Nên hôm nay mình xin phép làm lại giveaway thông qua hình thức điền form, vì lỗi của mình nên sẽ có thêm giải thưởng là 1 con chuột Viper V3 Green Edition mình sử dụng thi đấu ở stage 2 vừa qua nhé.&#x20;
Chi tiết phần thưởng :
\- 1 chuột Pulsar BuZz-X
\- 1 chuột Razer Viper V3 Green Edition
Bạn nào trúng đầu tiên sẽ được chọn trước 1 trong 2
Thể lệ tham gia :
\- Điền form ( [**https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform...**](https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform) )
\- Thả tim bài viết&#x20;
Kết quả sẽ roll trực tiếp trên live lúc 18h ngày 09/10
Hy vọng mọi người thông cảm [😣](https://static.xx.fbcdn.net/images/emoji.php/v9/t8f/1/16/1f623.png)
""";

    private const string Output = """
Sorry everyone, there were so many numbers in the previous giveaway post that I couldn't filter them all accurately [😭](https://static.xx.fbcdn.net/images/emoji.php/v9/t40/1/16/1f62d.png)
Today I'm redoing the giveaway using a form. Since it was my mistake, I'm adding a Viper V3 Green Edition mouse that I used during stage 2.
Prizes:
- 1 Pulsar BuZz-X mouse
- 1 Razer Viper V3 Green Edition mouse
The first winner gets to choose one of the two.
How to enter:
- Fill in the form ( [**https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform...**](https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform) )
- React with a heart to this post
The draw will be live at 6 PM on October 9.
I hope you understand [😣](https://static.xx.fbcdn.net/images/emoji.php/v9/t8f/1/16/1f623.png)
""";

    [Fact]
    public async Task ExactGiveawayPostAcceptsDecodedSpacesListsAndWrittenNumbers()
    {
        using var engine = new Engine(Output);
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync(Source, "vi", "en", default);
        Assert.Equal(Output, result.Text);
        Assert.False(result.LiteralsPreserved);
    }

    [Theory]
    [InlineData("Hello&#x20;world", "Xin chào thế giới")]
    [InlineData("Hello &#128557;", "Xin chào 😭")]
    [InlineData("Hello\n\\- 1 item\n\\- 2 items", "Xin chào\n- 1 mục\n- 2 mục")]
    public async Task EquivalentClipboardRepresentationsAreNotChangedFacts(string source, string output)
    {
        using var service = new LocalTranslationService(new Engine(output));
        Assert.Equal(output, (await service.TranslateAsync(source, "en", "vi", default)).Text);
    }

    [Fact]
    public void BoldUrlDelimitersAreNotPartOfTheProtectedUrl()
    {
        const string url = "https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform";
        var input = TranslationTextIntegrity.Protect($"Open [**{url}**]({url})", protectNumbers: false);
        Assert.All(input.Literals.Values, value => Assert.Equal(url, value));
        Assert.Equal(2, input.Literals.Count);
    }

    [Fact]
    public void ClipboardEntitiesDoNotBecomeNumericPlaceholders()
    {
        var input = TranslationTextIntegrity.Protect(Source, protectNumbers: false);
        Assert.Contains("&#x20;", input.Text);
        Assert.DoesNotContain(input.Literals.Values, value => value == "20" || value == "&#x20;");
        Assert.Equal(Source, input.Restore(input.Text, allowUnverifiedNumbers: true));
        Assert.True(TranslationTextIntegrity.Verify("Hello&#x20;world", "Hello world"));
    }

    [Theory]
    [InlineData("https://docs.google.com/.../1FAIpQLSesaOLMmvd.../viewform", "https://evil.test/form")]
    [InlineData("😣", "")]
    [InlineData("Viper V3", "Viper V4")]
    public async Task GiveawayStillRejectsChangedLinksEmojiAndProductIds(string original, string changed)
    {
        using var service = new LocalTranslationService(new Engine(Output.Replace(original, changed)));
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(Source, "vi", "en", default));
    }

    private sealed class Engine(string output) : ILocalTranslationEngine
    {
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) => Task.FromResult(output);
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
