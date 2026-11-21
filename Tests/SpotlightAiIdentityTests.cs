using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightAiIdentityTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("Sol 6.1", "Sol 6.1")]
    [InlineData("Astra", "Astra")]
    [InlineData("sol-6.1", "Sol 6.1")]
    [InlineData("gpt-4.1-mini-2025-04-14", "GPT-4.1 Mini")]
    [InlineData("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5")]
    [InlineData("claude-3-7-sonnet-latest", "Claude 3.7 Sonnet Latest")]
    [InlineData("gemini-2.5-pro", "Gemini 2.5 Pro")]
    [InlineData("deepseek-reasoner", "DeepSeek Reasoner")]
    [InlineData("o3-mini", "o3 Mini")]
    [InlineData("custom/model-v2", "custom/model-v2")]
    [InlineData("my-custom-model", "my-custom-model")]
    [InlineData("", "OpenAI")]
    public void ModelLabelsPreserveAliasesAndVersionNumbers(string model, string expected) =>
        Assert.Equal(expected, SpotlightAiModelName.Format(model, "OpenAI"));

    [Theory]
    [InlineData("OpenAI", "gpt-4.1", "GPT-4.1")]
    [InlineData("Gemini", "gemini-2.5-pro", "Gemini 2.5 Pro")]
    [InlineData("Claude", "claude-sonnet-4-5-20250929", "Claude Sonnet 4.5")]
    [InlineData("DeepSeek", "deepseek-chat", "DeepSeek Chat")]
    public void SelectedBrandAndModelAppearAcrossSpotlight(string provider, string model, string expected) =>
        SharedStaTestRunner.Run(() =>
        {
            bool previous = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(true);
            try
            {
                var settings = Settings(provider, model);
                using var fixture = new SpotlightWindowFixture(settings);
                var window = fixture.Window;
                window.ShowSpotlight();
                Invoke(window, "ToggleAiMode");
                Assert.Equal(expected, window.AiModeLabel.Text);
                Assert.Same(AiProviderBrand.For(provider), window.AiProviderLogo.Source);
                Assert.Same(AiProviderBrand.For(provider), window.AiWelcomeLogo.Source);
                Assert.Same(AiProviderBrand.GeometryFor(provider), Invoke(window, "GetChatIconGeometry"));
                string identity = provider + " · " + model;
                Assert.Equal(Loc.Get("spotlight.ai.switchProvider") + "\n" + identity, window.AiModelPill.ToolTip);
                Assert.Equal(identity, System.Windows.Automation.AutomationProperties.GetName(window.AiModelPill));

                Set(window, "_aiConversationProvider", provider);
                Set(window, "_aiConversationModel", model);
                var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
                history.Add(new("user", "Which model is selected?"));
                history.Add(new("assistant", "The selected model and its brand appear beside this response."));
                Invoke(window, "RenderAiHistory");
                Invoke(window, "RefreshAiPanel");
                Assert.Same(AiProviderBrand.For(provider), Assert.Single(Descendants<Image>(window.AiTranscript)).Source);
                Assert.Contains(Descendants<TextBlock>(window.AiTranscript), text => text.Text == expected);
                Assert.DoesNotContain(Descendants<TextBlock>(window.AiTranscript), text => text.Text == "✦");
                window.UpdateLayout();
                var titleBounds = window.AiModelPill.TransformToAncestor(window.AiHeader).TransformBounds(new Rect(window.AiModelPill.RenderSize));
                var copyBounds = window.AiCopyButton.TransformToAncestor(window.AiHeader).TransformBounds(new Rect(window.AiCopyButton.RenderSize));
                Assert.True(titleBounds.Right <= copyBounds.Left);
                Capture(window, provider);
            }
            finally { AnimationConfig.SetReduceMotion(previous); }
        });

    [Fact]
    public void SavedChatKeepsItsIdentityAndNewChatUsesCurrentSelection() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new SpotlightWindowFixture(Settings("OpenAI", "Sol 6.1"), saveChatHistory: true);
        var window = fixture.Window;
        Invoke(window, "ToggleAiMode");
        var saved = new SpotlightSavedChat
        {
            Provider = "Gemini", Model = "Astra",
            Messages = [new("user", "Saved prompt"), new("assistant", "Saved answer")]
        };
        await fixture.ChatStore.SaveChatAsync(saved);
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        Invoke(window, "ToggleAiHistoryPage");
        await Field<Task>(window, "_historyPageTask");
        window.UpdateLayout();
        await WpfFrameWaiter.UntilAsync(() => window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(0) != null, "saved identity card realized", ct);
        Assert.Same(AiProviderBrand.For("Gemini"), Assert.Single(Descendants<Image>(window.AiHistoryItems)).Source);
        Assert.Contains(Descendants<TextBlock>(window.AiHistoryItems), text => text.Text.StartsWith("Astra · ", StringComparison.Ordinal));
        Set(window, "_aiConversationProvider", saved.Provider);
        Set(window, "_aiConversationModel", saved.Model);
        Field<List<SpotlightAiMessage>>(window, "_aiHistory").AddRange(saved.Messages);
        Invoke(window, "RenderAiHistory");
        Assert.Equal("Astra", window.AiModeLabel.Text);
        Assert.Same(AiProviderBrand.For("Gemini"), window.AiProviderLogo.Source);
        Invoke(window, "CreateNewAiChat");
        Assert.Equal("Sol 6.1", window.AiModeLabel.Text);
        Assert.Same(AiProviderBrand.For("OpenAI"), window.AiProviderLogo.Source);

        window.ApplySettings(Settings("Claude", "claude-sonnet-4-5-20250929"));
        Assert.Equal("Claude Sonnet 4.5", window.AiModeLabel.Text);
        Assert.Same(AiProviderBrand.For("Claude"), window.AiProviderLogo.Source);
    });

    [Fact]
    public void BundledBrandsAreDistinctFrozenAndRespectSvgFillRule() => SharedStaTestRunner.Run(() =>
    {
        string[] providers = ["OpenAI", "Gemini", "Claude", "DeepSeek", "Codex"];
        Assert.Equal(providers.Length, providers.Select(AiProviderBrand.For).Distinct().Count());
        foreach (string provider in providers)
        {
            Assert.True(AiProviderBrand.For(provider).IsFrozen);
            Assert.True(AiProviderBrand.GeometryFor(provider).IsFrozen);
            Assert.False(AiProviderBrand.GeometryFor(provider).Bounds.IsEmpty);
        }
        Assert.Same(AiProviderBrand.For("OpenAI"), AiProviderBrand.For("openai"));
        Assert.DoesNotContain(AiProviderBrand.For("Unknown"), providers.Select(AiProviderBrand.For));
        Assert.Equal(FillRule.EvenOdd, Assert.IsType<StreamGeometry>(AiProviderBrand.GeometryFor("OpenAI")).FillRule);
    });

    private static NotchSettings Settings(string provider, string model)
    {
        var settings = new NotchSettings { SpotlightAiProvider = provider };
        SpotlightAiService.Configure(settings, provider, "local-test-key", model);
        return settings;
    }

    private static void Capture(SpotlightWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_AI_BRAND_PREVIEW") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Shell.ActualWidth),
            (int)Math.Ceiling(window.Shell.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(window.Shell), null,
            new Rect(0, 0, window.Shell.ActualWidth, window.Shell.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static object? Invoke(SpotlightWindow window, string name) => typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, null);
    private static T Field<T>(SpotlightWindow window, string name) => (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(SpotlightWindow window, string name, object value) => typeof(SpotlightWindow).GetField(name, Private)!.SetValue(window, value);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is System.Windows.Controls.ItemsControl items)
        {
            for (int i = 0; i < items.Items.Count; i++)
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.ContentControl { Content: DependencyObject content })
                    foreach (var child in Descendants<T>(content)) yield return child;
            yield break;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match) yield return match;
            if (child is DependencyObject dependency)
                foreach (var nested in Descendants<T>(dependency)) yield return nested;
        }
    }
}
