using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardClassificationLanguageTests
{
    [Theory]
    [InlineData("using System.Text;")]
    [InlineData("import numpy as np")]
    [InlineData("from pathlib import Path")]
    [InlineData("public class Example")]
    [InlineData("private static void Run()")]
    [InlineData("internal Task Run()")]
    [InlineData("const answer = 42")]
    [InlineData("let answer = 42")]
    [InlineData("var answer = 42")]
    [InlineData("def hello(name):")]
    [InlineData("function hello(name)")]
    [InlineData("SELECT name FROM users")]
    [InlineData("<div>Hello</div>")]
    [InlineData("</html>")]
    [InlineData("<script src='app.js'>")]
    [InlineData("<style scoped>")]
    [InlineData("<body class='main'>")]
    [InlineData("x => { return x }")]
    [InlineData("intro\n    public static class Example")]
    [InlineData("SELECT id, name FROM accounts WHERE active = 1")]
    public void CommonLanguageSnippetsAreRecognizedAsCode(string text) =>
        Assert.Equal(ClipboardKind.Code, ClipboardClassifier.Detect(new ClipboardCapture { Text = text }));

    [Theory]
    [InlineData("use this sentence")]
    [InlineData("importantly, this is prose")]
    [InlineData("from here to there")]
    [InlineData("public transport")]
    [InlineData("private note")]
    [InlineData("internal memo")]
    [InlineData("constant value")]
    [InlineData("let us go")]
    [InlineData("various items")]
    [InlineData("definitely useful")]
    [InlineData("function of time")]
    [InlineData("SELECT something without a table")]
    [InlineData("<division>")]
    [InlineData("<scripture>")]
    [InlineData("line one\n  ordinary prose")]
    public void SimilarWordsInProseDoNotBecomeCode(string text) =>
        Assert.Equal(ClipboardKind.Text, ClipboardClassifier.Detect(new ClipboardCapture { Text = text }));

    [Theory]
    [InlineData("rgb(0, 255, 127)", 0xFF00FF7Fu)]
    [InlineData("rgba(12, 34, 56, .5)", 0x800C2238u)]
    [InlineData("rgba(255, 0, 0, 0)", 0x00FF0000u)]
    [InlineData("#1234", 0x44112233u)]
    [InlineData("#11223344", 0x44112233u)]
    public void ColorFormatsPreserveAlphaAndChannels(string text, uint expected)
    {
        Assert.True(ClipboardClassifier.TryParseColor(text, out uint actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("rgb(256, 0, 0)")]
    [InlineData("rgb(0, 256, 0)")]
    [InlineData("rgb(0, 0, 256)")]
    [InlineData("rgba(0, 0, 0, 1.1)")]
    [InlineData("rgba(0, 0, 0, ..)")]
    [InlineData("rgb(0, 0)")]
    [InlineData("rgb(x, 0, 0)")]
    [InlineData("rgb(0; 0; 0)")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    public void MalformedColorsAreRejected(string text) =>
        Assert.False(ClipboardClassifier.TryParseColor(text, out _));

    [Theory]
    [InlineData("person+tag@sub.example.com")]
    [InlineData("Contact: first.last@example.co.uk!")]
    public void EmailAddressAddsEmailGroup(string text) =>
        Assert.Contains("Email", ClipboardClassifier.Classify(text, ClipboardKind.Text));
}

