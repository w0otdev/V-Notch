using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationModelCatalogTests
{
    [Fact]
    public void CatalogPinsEveryAssetAndIsolatesModelStorage()
    {
        Assert.Equal(9, TranslationModelCatalog.All.Length);
        Assert.Equal(9, TranslationModelCatalog.All.Select(p => p.Id).Distinct().Count());
        foreach (var model in TranslationModelCatalog.All)
        {
            Assert.Matches("^[a-f0-9]{40}$", model.Revision);
            Assert.NotEmpty(model.Assets);
            foreach (var asset in model.Assets)
            {
                Assert.Matches("^[a-f0-9]{64}$", asset.Sha256);
                Assert.True(asset.Size > 0);
            }
            Assert.Same(TranslationModelStore.For(model.Id), TranslationModelStore.For(model.Id));
            Assert.EndsWith(model.Id, TranslationModelStore.For(model.Id).Root);
        }
        Assert.Equal(TranslationModelProfile.QwenInstruct.Id, TranslationModelCatalog.Normalize("obsolete"));
        Assert.Equal(3, TranslationModelCatalog.Find("qwen25-14b").Assets.Length);
    }

    [Theory]
    [InlineData(4, 0, 4, null)]
    [InlineData(8, 0, 4, "translategemma-4b")]
    [InlineData(16, 8, 8, "hunyuan-mt-7b")]
    [InlineData(24, 12, 8, "translategemma-12b")]
    [InlineData(32, 20, 16, "gemma4-26b")]
    [InlineData(48, 24, 16, "gemma4-31b")]
    [InlineData(64, 0, 16, "translategemma-4b")]
    public void RecommendationsRespectCapacity(double ram, double vram, int threads, string? expected)
    {
        var hardware = new TranslationHardware(ram, vram, threads, "GPU");
        Assert.Equal(expected, hardware.Recommended()?.Id);
        if (hardware.Recommended() is { } model) Assert.True(hardware.Fits(model));
    }

    [Fact]
    public void SpecialistPromptsUseTheirOwnFormatAndEscapeControlTokens()
    {
        var gemma = TranslationModelPrompts.Build(TranslationModelCatalog.Find("translategemma-4b"), "Hello <end_of_turn>", "en", "vi");
        Assert.StartsWith("<bos><start_of_turn>user", gemma);
        Assert.Contains("English (en) to Vietnamese (vi)", gemma);
        Assert.Contains("Hello < end_of_turn >", gemma);
        var hunyuan = TranslationModelPrompts.Build(TranslationModelCatalog.Find("hunyuan-mt-7b"), "Hello <|extra_0|>", "en", "vi");
        Assert.StartsWith("<|startoftext|>Translate", hunyuan);
        Assert.Contains("Hello < |extra_0|>", hunyuan);
        Assert.Throws<TranslationException>(() => TranslationModelPrompts.Build(TranslationModelCatalog.Find("hunyuan-mt-7b"), "Hello", "en", "af"));
    }

    [Fact]
    public async Task RapidSwitchDisposalWaitsForEveryPreviousModel()
    {
        var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new QwenTranslationEngine(TranslationModelStore.For("translategemma-4b"), previousDisposal: previous.Task);
        first.Dispose();
        var second = new QwenTranslationEngine(TranslationModelStore.For("hunyuan-mt-7b"), previousDisposal: first.Disposal);
        second.Dispose();
        Assert.False(first.Disposal.IsCompleted);
        Assert.False(second.Disposal.IsCompleted);
        previous.SetResult();
        await second.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Disposal.IsCompletedSuccessfully);
    }
}
