using System.Text.Json;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationJsonBoundaryTests
{
    [Theory]
    [InlineData("<end_of_turn>")]
    [InlineData("<|endoftext|>")]
    [InlineData("<|extra_1|>")]
    public void StopMarkersAndAlreadyYieldedPrefixesAreRemoved(string stop)
    {
        for (int split = 1; split <= stop.Length; split++)
            Assert.Equal("Translated text", QwenTranslationEngine.TrimStopSequence("Translated text" + stop[..split]));
        Assert.Equal("Translated text", QwenTranslationEngine.TrimStopSequence("Translated text" + stop + "ignored"));
    }

    [Theory]
    [InlineData("Hello {user}")]
    [InlineData("A \"quoted}\" value and \\ slash")]
    public void BracesAndEscapesInsideStringsDoNotEndGeneration(string translation)
    {
        string json = JsonSerializer.Serialize(new { translation });
        for (int split = 0; split < json.Length; split++)
        {
            var boundary = new TranslationJsonBoundary();
            Assert.False(boundary.Append(json[..split]));
            Assert.True(boundary.Append(json[split..]));
            Assert.Equal(translation, QwenTranslationEngine.ReadOutput(json));
        }
    }
}
