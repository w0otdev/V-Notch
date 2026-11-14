using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PerformancePresetsTests
{
    [Fact]
    public void StructLayout_MatchesPackedNativeAbi()
    {
        Assert.Equal(25, Marshal.SizeOf<VnPerformancePresetState>());
        Assert.Equal(VNotchNative.VN_PERF_PRESET_COUNT, PerformancePresetCatalog.All.Count);
    }

    [Theory]
    [InlineData(VNotchNative.VN_PERF_PRESET_ECO, 0)]
    [InlineData(VNotchNative.VN_PERF_PRESET_EVERYDAY, 0)]
    [InlineData(VNotchNative.VN_PERF_PRESET_BALANCED, 1)]
    [InlineData(VNotchNative.VN_PERF_PRESET_HIGH_END, 1)]
    public void ApplyAndMatch_RoundTripsAllHardwareTiers(int presetId, byte expectedLiquidGlassWhenEnabled)
    {
        var initial = new VnPerformancePresetState
        {
            AutoAnimationFps = 1,
            AnimationFps = 165,
            EnableBlurEffects = 1,
            EnableSubjectBlur = 1,
            EnableSmartCrop = 1,
            ShowMediaArtBackground = 1,
            EnableShadow = 1,
            IsLiquidGlass = 1,
            UseGpuRefraction = 1,
            UseUltimateGpu = 0,
            GlassTargetFps = 60,
            GpuPreference = 0,
            ProcessPriority = 0,
        };

        int applyStatus = VNotchNative.vn_perf_preset_apply(presetId, in initial, out VnPerformancePresetState applied);
        Assert.Equal(VNotchNative.VN_OK, applyStatus);
        Assert.Equal(expectedLiquidGlassWhenEnabled, applied.IsLiquidGlass);

        int matchStatus = VNotchNative.vn_perf_preset_match(in applied, out int matchedId);
        Assert.Equal(VNotchNative.VN_OK, matchStatus);
        Assert.Equal(presetId, matchedId);
    }

    [Fact]
    public void Session_CapturesBackupOncePreservesAcrossPresetsAndRestoresWithoutManualLoad()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vnotch-perf-preset-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string backupPath = Path.Combine(tempDir, PerformancePresetCatalog.BackupFileName);

        try
        {
            var stubAppService = new StubSettingsApplicationService();
            var session = new PerformancePresetSession { BackupFilePathOverride = backupPath };

            var userCustom = new NotchSettings
            {
                AutoAnimationFps = false,
                AnimationFps = 144,
                EnableBlurEffects = true,
                EnableSubjectBlur = false,
                EnableSmartCrop = true,
                ShowMediaArtBackground = true,
                EnableShadow = true,
                NotchStyle = "liquid-glass",
                GpuPreference = 0,
                ProcessPriority = ProcessPriorityPolicy.Normal,
                SpotlightOpenAIApiKey = "sk-live-secret",
            };

            Assert.Equal(VNotchNative.VN_PERF_SELECTION_CUSTOM, session.ResolveActiveSelection(userCustom));

            // 1. Selecting Eco backs up userCustom immediately and writes .vns file.
            Assert.True(session.TryPreparePresetSelection(
                VNotchNative.VN_PERF_PRESET_ECO,
                stubAppService,
                userCustom,
                out NotchSettings ecoSnapshot,
                out bool capturedFirst));
            session.CommitPresetApplied(ecoSnapshot);

            Assert.True(capturedFirst);
            Assert.True(session.HasUserBackup);
            Assert.True(File.Exists(backupPath));
            Assert.Equal(VNotchNative.VN_PERF_PRESET_ECO, session.ResolveActiveSelection(ecoSnapshot));
            Assert.Equal(60, ecoSnapshot.AnimationFps);
            Assert.False(ecoSnapshot.EnableBlurEffects);

            // 2. Switching directly to HighEnd keeps original userCustom backup intact.
            Assert.True(session.TryPreparePresetSelection(
                VNotchNative.VN_PERF_PRESET_HIGH_END,
                stubAppService,
                ecoSnapshot,
                out NotchSettings highSnapshot,
                out bool capturedSecond));
            session.CommitPresetApplied(highSnapshot);

            Assert.False(capturedSecond);
            Assert.Equal(VNotchNative.VN_PERF_PRESET_HIGH_END, session.ResolveActiveSelection(highSnapshot));

            // 3. Reopening a fresh session loads the .vns backup automatically and rolls back while keeping live API keys.
            var reopenedSession = new PerformancePresetSession { BackupFilePathOverride = backupPath };
            reopenedSession.EnsureDiskBackupLoaded(stubAppService, highSnapshot);
            Assert.True(reopenedSession.HasUserBackup);

            Assert.True(reopenedSession.TryPrepareRollback(stubAppService, highSnapshot, out NotchSettings restored));
            reopenedSession.CommitRollbackApplied(restored);

            Assert.Equal(144, restored.AnimationFps);
            Assert.Equal("liquid-glass", restored.NotchStyle);
            Assert.Equal("sk-live-secret", restored.SpotlightOpenAIApiKey);
            Assert.Equal(VNotchNative.VN_PERF_SELECTION_BACKUP, reopenedSession.ResolveActiveSelection(restored));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RecommendHardwareTier_ReturnsValidPresetIndex()
    {
        int tier = PerformancePresetCatalog.QueryRecommendedHardwareTier();
        Assert.InRange(tier, 0, VNotchNative.VN_PERF_PRESET_COUNT - 1);
    }

    private sealed class StubSettingsApplicationService : ISettingsApplicationService
    {
        public event EventHandler<NotchSettings>? SettingsApplied;

        public NotchSettings Load() => new();

        public Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            SettingsApplied?.Invoke(this, settings.Clone());
            return Task.CompletedTask;
        }

        public Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(
            string filePath,
            NotchSettings? currentSettings = null,
            CancellationToken ct = default)
        {
            string json = File.ReadAllText(filePath);
            return Task.FromResult(SettingsService.ImportSettingsFromString(json, currentSettings));
        }

        public void Export(string filePath, NotchSettings settings)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(filePath, SettingsService.ExportSettingsToString(settings));
        }

        public bool IsAutoStartEnabled() => false;
    }
}
