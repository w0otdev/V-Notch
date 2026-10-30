using System;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Effects;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SettingsDiffingTests
{
    [Fact]
    public void LiquidGlassConfig_ValueEquals_IdenticalInstances_ReturnsTrue()
    {
        var a = new LiquidGlassConfig();
        var b = a.Clone();

        Assert.True(a.ValueEquals(b));
        Assert.True(b.ValueEquals(a));
        Assert.True(a.ValueEquals(a));
    }

    [Fact]
    public void LiquidGlassConfig_ValueEquals_NullComparison_ReturnsFalse()
    {
        var a = new LiquidGlassConfig();

        Assert.False(a.ValueEquals(null));
    }

    [Fact]
    public void LiquidGlassConfig_ValueEquals_ModifiedProperty_ReturnsFalse()
    {
        var a = new LiquidGlassConfig { BlurAmount = 0.3 };
        var b = a.Clone();

        Assert.True(a.ValueEquals(b));

        b.BlurAmount = 0.5;
        Assert.False(a.ValueEquals(b));

        b.BlurAmount = a.BlurAmount;
        Assert.True(a.ValueEquals(b));

        b.Refraction = 2.5;
        Assert.False(a.ValueEquals(b));

        b.Refraction = a.Refraction;
        b.UseGpuRefraction = !a.UseGpuRefraction;
        Assert.False(a.ValueEquals(b));

        b.UseGpuRefraction = a.UseGpuRefraction;
        b.UseUltimateGpu = !a.UseUltimateGpu;
        Assert.False(a.ValueEquals(b));
    }

    [Fact]
    public void NotchSettings_Clone_ProducesEqualValuesAndResetsIsDirty()
    {
        var settings = new NotchSettings
        {
            AnimationFps = 144,
            Width = 300,
            Height = 40,
            DebugWindowX = 120.5,
            NotchStyle = "liquid_glass",
            IsDirty = true,
            LiquidGlass = new LiquidGlassConfig { BlurAmount = 0.7, Refraction = 1.2 },
            LiquidGlassCustom = new LiquidGlassConfig { BlurAmount = 0.45, Refraction = 0.9 }
        };

        var clone = settings.Clone();

        Assert.False(clone.IsDirty);
        Assert.True(settings.ValueEquals(clone));
        Assert.Equal(settings.AnimationFps, clone.AnimationFps);
        Assert.Equal(settings.Width, clone.Width);
        Assert.Equal(settings.DebugWindowX, clone.DebugWindowX);
        Assert.NotSame(settings.LiquidGlass, clone.LiquidGlass);
        Assert.NotSame(settings.LiquidGlassCustom, clone.LiquidGlassCustom);
        Assert.True(settings.LiquidGlass.ValueEquals(clone.LiquidGlass));

        clone.LiquidGlass.BlurAmount = 0.9;
        Assert.False(settings.LiquidGlass.ValueEquals(clone.LiquidGlass));
        Assert.False(settings.ValueEquals(clone));
    }

    [Fact]
    public void NotchSettings_ValueEquals_DetectsScalarAndNullableDoubleDifferences()
    {
        var a = new NotchSettings();
        var b = a.Clone();

        Assert.True(a.ValueEquals(b));

        b.Opacity = a.Opacity + 0.00001;
        Assert.True(a.ValueEquals(b));

        b.Opacity = a.Opacity + 0.01;
        Assert.False(a.ValueEquals(b));
        b.Opacity = a.Opacity;

        b.DebugWindowX = 42.0;
        Assert.False(a.ValueEquals(b));
        a.DebugWindowX = 42.00001;
        Assert.True(a.ValueEquals(b));
        b.DebugWindowX = 43.0;
        Assert.False(a.ValueEquals(b));
        b.DebugWindowX = a.DebugWindowX;

        b.Language = "vi";
        Assert.False(a.ValueEquals(b));
        b.Language = a.Language;

        b.EnableDynamicIslandMode = !a.EnableDynamicIslandMode;
        Assert.False(a.ValueEquals(b));
    }

    [Fact]
    public void NotchSettings_Defaults_EnableYouTubeSubtitlesIsTrue()
    {
        var settings = new NotchSettings();
        Assert.True(settings.EnableYouTubeSubtitles);
    }

    [Fact]
    public void ClipboardCardViewModel_FileBrushes_AreFrozenAndCached()
    {
        SharedStaTestRunner.Run(() =>
        {
            var entryA = new ClipboardEntry
            {
                Kind = ClipboardKind.File,
                Title = "archive.zip",
                Preview = "archive.zip",
                FileCount = 1,
                PrimaryExtension = ".zip"
            };
            var entryB = entryA with { Id = Guid.NewGuid(), Title = "backup.7z", PrimaryExtension = ".7z" };

            var vmA = new ClipboardCardViewModel(entryA);
            var vmB = new ClipboardCardViewModel(entryB);

            Assert.True(vmA.FileAccentBrush.IsFrozen);
            Assert.True(vmA.FileBadgeBackgroundBrush.IsFrozen);
            Assert.Same(vmA.FileAccentBrush, vmB.FileAccentBrush);
            Assert.Same(vmA.FileBadgeBackgroundBrush, vmB.FileBadgeBackgroundBrush);
        });
    }

    [Fact]
    public void DustParticleOverlay_DeactivateAndDissolve_ResetSiblingBlurWithoutBreakingBinding()
    {
        SharedStaTestRunner.Run(() =>
        {
            var entry = new ClipboardEntry
            {
                Kind = ClipboardKind.Text,
                Preview = "secret-token-123",
                IsSensitive = true
            };
            var vm = new ClipboardCardViewModel(entry, isMasked: true);

            var grid = new Grid { DataContext = vm };
            var blur = new BlurEffect();
            BindingOperations.SetBinding(blur, BlurEffect.RadiusProperty, new Binding(nameof(ClipboardCardViewModel.BlurRadius)) { Source = vm });
            var textBlock = new TextBlock { Effect = blur };
            var overlay = new DustParticleOverlay
            {
                IsActive = true,
                IsDissolved = false
            };

            grid.Children.Add(textBlock);
            grid.Children.Add(overlay);

            Assert.Equal(12.0, blur.Radius);
            Assert.Equal(0.0, textBlock.Opacity);

            // Dissolving via property change (e.g. header eye button) clears sibling blur and preserves binding
            vm.IsRevealed = true;
            overlay.IsDissolved = true;
            Assert.Equal(0.0, blur.Radius);
            Assert.Equal(1.0, textBlock.Opacity);
            Assert.NotNull(BindingOperations.GetBinding(blur, BlurEffect.RadiusProperty));

            // Re-masking restores blur via intact binding; deactivating overlay (container recycling) resets blur to 0
            vm.IsRevealed = false;
            overlay.IsDissolved = false;
            Assert.Equal(12.0, blur.Radius);
            Assert.Equal(0.0, textBlock.Opacity);

            overlay.IsActive = false;
            Assert.Equal(0.0, blur.Radius);
            Assert.Equal(1.0, textBlock.Opacity);
        });
    }

    [Fact]
    public void DustParticleOverlay_RendersDenseUniformTelegramSpoilerParticlesAcrossSurface()
    {
        SharedStaTestRunner.Run(() =>
        {
            var overlay = new DustParticleOverlay
            {
                IsActive = true,
                IsDissolved = false,
                Width = 180,
                Height = 22
            };

            overlay.Measure(new System.Windows.Size(180, 22));
            overlay.Arrange(new System.Windows.Rect(0, 0, 180, 22));

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                typeof(DustParticleOverlay)
                    .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(overlay, [dc]);
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                180, 22, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);

            byte[] pixels = new byte[180 * 22 * 4];
            bitmap.CopyPixels(pixels, 180 * 4, 0);

            int leftLit = 0, centerLit = 0, rightLit = 0;
            for (int y = 0; y < 22; y++)
            {
                for (int x = 0; x < 180; x++)
                {
                    byte alpha = pixels[(y * 180 + x) * 4 + 3];
                    if (alpha > 20)
                    {
                        if (x < 60) leftLit++;
                        else if (x < 120) centerLit++;
                        else rightLit++;
                    }
                }
            }

            // Stratified R2 distribution guarantees particles across all horizontal thirds without empty gaps
            Assert.True(leftLit > 20, $"Expected left third to contain dense dust pixels, got {leftLit}");
            Assert.True(centerLit > 20, $"Expected center third to contain dense dust pixels, got {centerLit}");
            Assert.True(rightLit > 20, $"Expected right third to contain dense dust pixels, got {rightLit}");
        });
    }

    [Fact]
    public void ProcessNameResolver_TryGetNameAndExecutable_ResolvesCurrentProcessAndHandlesZeroPid()
    {
        var (zeroName, zeroExe) = Services.ProcessNameResolver.TryGetNameAndExecutable(0);
        Assert.Equal("", zeroName);
        Assert.Equal("", zeroExe);

        uint currentPid = (uint)Environment.ProcessId;
        var (name, executable) = Services.ProcessNameResolver.TryGetNameAndExecutable(currentPid);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(string.IsNullOrWhiteSpace(executable));
        Assert.True(System.IO.File.Exists(executable));
    }

    [Fact]
    public void MediaPinBadge_OnRender_DrawsPinAtBottomRightCorner()
    {
        SharedStaTestRunner.Run(() =>
        {
            var badge = new MediaPinBadge
            {
                Width = 48,
                Height = 48
            };
            badge.SetPinned(true, animate: false);
            badge.Measure(new System.Windows.Size(48, 48));
            badge.Arrange(new System.Windows.Rect(0, 0, 48, 48));

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                typeof(MediaPinBadge)
                    .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(badge, [dc]);
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                48, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);

            byte[] pixels = new byte[48 * 48 * 4];
            bitmap.CopyPixels(pixels, 48 * 4, 0);

            int bottomRightLit = 0;
            for (int y = 32; y < 48; y++)
            {
                for (int x = 32; x < 48; x++)
                {
                    if (pixels[(y * 48 + x) * 4 + 3] > 20)
                        bottomRightLit++;
                }
            }

            Assert.True(bottomRightLit > 10, $"Expected pinned badge pixels in bottom-right corner, got {bottomRightLit}");
        });
    }

    [Fact]
    public void ClipboardClassifier_Classify_FastPathReturnsEmptyAndPreservesRuleMatching()
    {
        Assert.Same(Array.Empty<string>(), Services.Clipboard.ClipboardClassifier.Classify("", ClipboardKind.Text));
        Assert.Same(Array.Empty<string>(), Services.Clipboard.ClipboardClassifier.Classify("just a normal sentence", ClipboardKind.Text));

        var workAndEmail = Services.Clipboard.ClipboardClassifier.Classify(
            "Project deadline tomorrow, contact dev@example.com", ClipboardKind.Text);
        Assert.Contains("Work", workAndEmail);
        Assert.Contains("Email", workAndEmail);

        var contextPair = Services.Clipboard.ClipboardClassifier.Classify(
            "Review the agenda and task list", ClipboardKind.Text);
        Assert.Contains("Work", contextPair);
    }
}
