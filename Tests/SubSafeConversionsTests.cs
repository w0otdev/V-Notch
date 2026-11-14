using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services.AiUsage;
using VNotch.Services.Clipboard;
using VNotch.Services.Spotlight;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class SubSafeConversionsTests
{
    [Fact]
    public void AiUsageParsing_Codex_PreservesFractionalPercentBucketOrderAndBounds()
    {
        using var doc = JsonDocument.Parse("""
            {
              "rateLimitsByLimitId": {
                "gpt-4o": {
                  "primary": { "usedPercent": 12.75, "windowDurationMins": 60, "resetsAt": 1735689600 }
                },
                "codex": {
                  "primary": { "usedPercent": 42.5, "windowDurationMins": 300, "resetsAt": 1735693200 },
                  "secondary": { "usedPercent": 1e3000, "windowDurationMins": 10080 }
                },
                "bad bucket!": {
                  "secondary": { "usedPercent": 88.125 }
                }
              }
            }
            """);

        var now = DateTimeOffset.FromUnixTimeSeconds(1735680000);
        var usage = AiUsageParsing.Codex(doc.RootElement, now, "pro", "dev@example.com");

        Assert.Equal(AiUsageState.Ready, usage.State);
        Assert.NotNull(usage.Windows);
        Assert.Equal(3, usage.Windows.Count);

        Assert.Equal("codex", usage.Windows[0].Bucket);
        Assert.Equal("aiUsage.primary", usage.Windows[0].LabelKey);
        Assert.Equal(42.5, usage.Windows[0].UsedPercent);
        Assert.Equal(300, usage.Windows[0].DurationMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735693200), usage.Windows[0].ResetsAt);

        Assert.Equal("gpt-4o", usage.Windows[1].Bucket);
        Assert.Equal(12.75, usage.Windows[1].UsedPercent);

        Assert.Null(usage.Windows[2].Bucket);
        Assert.Equal("aiUsage.secondary", usage.Windows[2].LabelKey);
        Assert.Equal(88.125, usage.Windows[2].UsedPercent);
        Assert.Null(usage.Windows[2].DurationMinutes);
    }

    [Fact]
    public void AiUsageParsing_Claude_ParsesWindowsAndWaitingFallback()
    {
        var observed = DateTimeOffset.FromUnixTimeSeconds(1735680000);
        using var readyDoc = JsonDocument.Parse("""
            {
              "rate_limits": {
                "five_hour": { "used_percentage": 33.5, "resets_at": 1735698000 },
                "seven_day": { "used_percentage": 71.25, "resets_at": null }
              }
            }
            """);
        var ready = AiUsageParsing.Claude(readyDoc.RootElement, observed);
        Assert.Equal(AiUsageState.Ready, ready.State);
        Assert.NotNull(ready.Windows);
        Assert.Equal(2, ready.Windows.Count);
        Assert.Equal("aiUsage.fiveHour", ready.Windows[0].LabelKey);
        Assert.Equal(300, ready.Windows[0].DurationMinutes);
        Assert.Equal(33.5, ready.Windows[0].UsedPercent);
        Assert.Equal("aiUsage.sevenDay", ready.Windows[1].LabelKey);
        Assert.Equal(10080, ready.Windows[1].DurationMinutes);
        Assert.Null(ready.Windows[1].ResetsAt);

        using var emptyDoc = JsonDocument.Parse("{}");
        Assert.Equal(AiUsageState.Waiting, AiUsageParsing.Claude(emptyDoc.RootElement, observed).State);
    }

    [Theory]
    [InlineData(@"C:\Users\dev\AppData\Local\V-Notch\ai-usage")]
    [InlineData(@"C:\Users\O'Connor & Sons\$env\ai'usage")]
    [InlineData(@"D:\Thư mục Tiếng Việt\ai-usage")]
    public void ClaudeUsageBridge_Command_MatchesManagedUtf16Base64Encoding(string dir)
    {
        var bridge = new ClaudeUsageBridge(dataDirectory: dir, settingsPath: @"C:\temp\settings.json");
        string script = System.IO.Path.Combine(dir, "claude-statusline.ps1");
        string invoke = "& '" + script.Replace("'", "''") + "'";
        string expected = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(invoke));

        Assert.Equal(expected, bridge.Command);
    }

    [Fact]
    public void QwenTranslationEngine_TryReadOutput_EnforcesSingleTranslationProperty()
    {
        Assert.True(QwenTranslationEngine.TryReadOutput("  { \"translation\" : \"  Xin chào thế giới! \\n \" } ", out string ok));
        Assert.Equal("Xin chào thế giới!", ok);

        Assert.True(QwenTranslationEngine.TryReadOutput("{\"translation\":\"Emoji \\uD83D\\uDE80 ok\"}", out string emoji));
        Assert.Equal("Emoji 🚀 ok", emoji);

        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":\"a\",\"extra\":\"b\"}", out _));
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":\"a\",\"translation\":\"b\"}", out _));
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"t\":\"wrong key\"}", out _));
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":123}", out _));
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":\"ok\"} trailing", out _));
    }

    [Fact]
    public void TranslationTextIntegrity_ProtectAndInspect_HandlesUnicodeNumbersEntitiesAndIdioms()
    {
        // HTML entities stay unmasked in Protect, while URLs/tickets/identifiers are masked.
        var protectedText = TranslationTextIntegrity.Protect(
            "See &amp; https://example.com/path)* ticket VN-2048 item12 Chờ 1 tí và giá １２ USD.");
        Assert.Contains("&amp;", protectedText.Text);
        Assert.Contains("1 tí", protectedText.Text);
        Assert.DoesNotContain("https://example.com/path", protectedText.Text);
        Assert.DoesNotContain("VN-2048", protectedText.Text);

        string restored = protectedText.Restore(protectedText.Text);
        Assert.True(TranslationTextIntegrity.Verify(
            "See &amp; https://example.com/path)* ticket VN-2048 item12 Chờ 1 tí và giá １２ USD.",
            restored));

        // 1 tỷ (billion) is NOT idiomatic one and must be preserved.
        Assert.False(TranslationTextIntegrity.Verify("Doanh thu 1 tỷ đồng", "Revenue billion dong"));

        // Identifier-attached numbers ("item12") are protected facts even when unverified numbers are allowed.
        Assert.False(TranslationTextIntegrity.CanPresentUnverifiedNumbers("Check item12 now", "Check item now"));
        Assert.True(TranslationTextIntegrity.CanPresentUnverifiedNumbers("Buy 12 apples", "Buy twelve apples"));
    }

    [Fact]
    public async Task TranslationStructuredText_Shortcodes_SkipsShortcodeOnlyAndVerifiesSignature()
    {
        bool called = false;
        string skipped = await TranslationStructuredText.TranslateAsync(
            ":rocket: :tada:",
            (_, _) => { called = true; return Task.FromResult("bad"); },
            CancellationToken.None);
        Assert.False(called);
        Assert.Equal(":rocket: :tada:", skipped);

        await Assert.ThrowsAsync<TranslationException>(() =>
            TranslationStructuredText.TranslateAsync(
                "Hello :rocket: world",
                (_, _) => Task.FromResult("Xin chào :sparkles: thế giới"),
                CancellationToken.None));
    }

    [Fact]
    public void SelectedPrice_TryParseAndConflict_PreservesExactCurrencyRules()
    {
        Assert.True(TranslationCurrencies.TryParse("US$ 1,250.50", "en", out var usPrice));
        Assert.Equal(1250.50m, usPrice.Amount);
        Assert.Equal("USD", usPrice.Currency);
        Assert.False(usPrice.AssumedCurrency);

        Assert.True(TranslationCurrencies.TryParse("$100 CAD", "en", out var cadPrice));
        Assert.Equal(100m, cadPrice.Amount);
        Assert.Equal("CAD", cadPrice.Currency);
        Assert.False(cadPrice.AssumedCurrency);

        Assert.True(TranslationCurrencies.HasConflictingCurrency("Total is USD 50 EUR today", "en"));
        Assert.False(TranslationCurrencies.HasConflictingCurrency("Total is $50 CAD today", "en"));
        Assert.False(TranslationCurrencies.HasConflictingCurrency("Total is ¥500 JPY today", "ja"));
    }

    [Fact]
    public async Task SpotlightAiService_SendAsync_ExtractsProviderResponsesViaNative()
    {
        using var geminiClient = CreateMockSpotlightClient("""
            {
              "candidates": [{
                "content": {
                  "parts": [
                    { "text": "internal chain", "thought": true },
                    { "text": "Hello " },
                    { "text": "Gemini!" }
                  ]
                }
              }]
            }
            """);
        string geminiReply = await new SpotlightAiService(geminiClient).SendAsync(
            CreateSpotlightSettings("Gemini"), [new SpotlightAiMessage("user", "hi")], CancellationToken.None);
        Assert.Equal("Hello Gemini!", geminiReply);

        using var claudeClient = CreateMockSpotlightClient("""
            {
              "content": [
                { "type": "thinking", "thinking": "hmm" },
                { "type": "text", "text": "Claude response" }
              ]
            }
            """);
        string claudeReply = await new SpotlightAiService(claudeClient).SendAsync(
            CreateSpotlightSettings("Claude"), [new SpotlightAiMessage("user", "hi")], CancellationToken.None);
        Assert.Equal("Claude response", claudeReply);

        using var emptyClient = CreateMockSpotlightClient("{\"choices\":[{\"message\":{\"content\":\"   \"}}]}");
        var err = await Assert.ThrowsAsync<SpotlightAiException>(() =>
            new SpotlightAiService(emptyClient).SendAsync(
                CreateSpotlightSettings("OpenAI"), [new SpotlightAiMessage("user", "hi")], CancellationToken.None));
        Assert.Equal("spotlight.ai.emptyError", err.Message);
    }

    [Fact]
    public void ClipboardClassifier_DetectsCodeAndEmailViaNativeTraits()
    {
        Assert.Equal(ClipboardKind.Code, ClipboardClassifier.Detect(new ClipboardCapture { Text = "using System.Text.Json;" }));
        Assert.Equal(ClipboardKind.Code, ClipboardClassifier.Detect(new ClipboardCapture { Text = "const items = x => { return x; }" }));
        Assert.Equal(ClipboardKind.Code, ClipboardClassifier.Detect(new ClipboardCapture { Text = "SELECT id, email FROM accounts" }));
        Assert.Equal(ClipboardKind.Code, ClipboardClassifier.Detect(new ClipboardCapture { Text = "Console.WriteLine(value);" }));
        Assert.Equal(ClipboardKind.Text, ClipboardClassifier.Detect(new ClipboardCapture { Text = "Please select an item from our menu today." }));

        string[] groups = ClipboardClassifier.Classify("Contact alice.smith+ops@sub.domain.org for details", ClipboardKind.Text);
        Assert.Contains("Email", groups);
    }

    [Fact]
    public void ClipboardDropReader_PublicIpAndDevicePathGuards_MatchSecurityRules()
    {
        Assert.True(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("1.1.1.1")));
        Assert.True(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.True(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")));

        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("127.0.0.1")));
        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("100.64.12.1")));
        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("169.254.169.254")));
        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("2002:db8::1")));
        Assert.False(ClipboardDropReader.IsPublicAddress(IPAddress.Parse("2001:0000:4136:e378:8000:63bf:3fff:fdd2")));

        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"\\.\PhysicalDrive0"));
        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"\\?\C:\Windows\System32\cmd.exe"));
        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"\??\C:\secret.txt"));
        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"C:\Users\dev\CON"));
        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"C:\Users\dev\nul.png"));
        Assert.True(ClipboardDropReader.IsBlockedDevicePath(@"C:\Users\dev\COM1.txt"));
        Assert.False(ClipboardDropReader.IsBlockedDevicePath(@"C:\Users\dev\console.png"));
    }

    [Fact]
    public void ClipboardSensitiveData_IsSensitiveFileName_MatchesExactSecretSet()
    {
        Assert.True(ClipboardSensitiveData.IsSensitiveFileName(@"C:\Users\me\.ssh\id_ed25519"));
        Assert.True(ClipboardSensitiveData.IsSensitiveFileName("/home/me/.env"));
        Assert.True(ClipboardSensitiveData.IsSensitiveFileName("prod-cert.PFX"));
        Assert.True(ClipboardSensitiveData.IsSensitiveFileName("corp.ovpn"));

        Assert.False(ClipboardSensitiveData.IsSensitiveFileName(@"C:\Users\me\notes.txt"));
        Assert.False(ClipboardSensitiveData.IsSensitiveFileName(@"C:\Users\me\.ssh\"));
        Assert.False(ClipboardSensitiveData.IsSensitiveFileName("   "));
    }

    [Fact]
    public void ClipboardHistoryStore_BuildPublicBlobNonceAndAad_MatchesBinaryLayout()
    {
        byte[] streamId = [0x10, 0x20, 0x30, 0x40, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        ulong chunkIndex = 0x1122334455667788UL;
        int plainLen = 65536;

        Span<byte> actualNonce = stackalloc byte[12];
        Span<byte> actualAad = stackalloc byte[33];
        ClipboardHistoryStore.BuildPublicBlobNonceAndAad(streamId, chunkIndex, plainLen, true, actualNonce, actualAad);

        Span<byte> expectedNonce = stackalloc byte[12];
        streamId.CopyTo(expectedNonce);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(expectedNonce[4..12]);
        BinaryPrimitives.WriteUInt64LittleEndian(expectedNonce[4..12], low ^ chunkIndex);

        Span<byte> expectedAad = stackalloc byte[33];
        "VNPBLOB1"u8.CopyTo(expectedAad[..8]);
        streamId.CopyTo(expectedAad.Slice(8, 12));
        BinaryPrimitives.WriteUInt64LittleEndian(expectedAad.Slice(20, 8), chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(expectedAad.Slice(28, 4), plainLen);
        expectedAad[32] = 1;

        Assert.True(actualNonce.SequenceEqual(expectedNonce));
        Assert.True(actualAad.SequenceEqual(expectedAad));
    }

    private static NotchSettings CreateSpotlightSettings(string provider)
    {
        var settings = new NotchSettings { SpotlightAiProvider = provider };
        SpotlightAiService.Configure(settings, provider, "test-secret", "model-id");
        return settings;
    }

    private static HttpClient CreateMockSpotlightClient(string jsonBody) =>
        new(new StubHttpMessageHandler(jsonBody));

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
