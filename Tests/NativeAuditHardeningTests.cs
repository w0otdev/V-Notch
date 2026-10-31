using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeAuditHardeningTests
{
    private const string NativeDll = "VNotch.Native.dll";

    private const int VN_OK = 0;
    private const int VN_ERR_INVALID_ARG = -1;
    private const int VN_ERR_BUFFER_TOO_SMALL = -2;
    private const int VN_ERR_FAIL = -3;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnGlassBackdropOpticsOut
    {
        public byte Red;
        public byte Green;
        public byte Blue;
        public byte Reserved;
        public double LightX;
        public double LightY;
        public double Contrast;
        public int OutsideRgb;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnGlassMapParams
    {
        public int OutW;
        public int OutH;
        public int SrcW;
        public int SrcH;
        public int Margin;
        public int NotchW;
        public int NotchH;
        public int NotchOffX;
        public int NotchOffY;
        public int CaptureShiftX;
        public int CaptureShiftY;
        public double OutScale;
        public double TopCornerRadius;
        public double BottomCornerRadius;
        public double EdgeBend;
        public double ChromaticAberration;
        public double Distortion;
        public double FPower;
        public double RefractionA;
        public double RefractionB;
        public double RefractionC;
        public double RefractionD;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnComplexFloat
    {
        public float Real;
        public float Imag;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSettingsNormalizationParams
    {
        public int Width;
        public int Height;
        public int CornerRadius;
        public int DynamicIslandWidth;
        public int DynamicIslandHeight;
        public int NotificationDuration;
        public int AnimationFps;
        public int MinFps;
        public int MaxFps;
        public double SpotifyCanvasBrightness;
        public double Opacity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnDetection
    {
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public float Confidence;
        public int ClassId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnCropRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnCategoryPillMotionOut
    {
        public double StartScaleX;
        public double PeakScaleX;
        public double SettleOvershootScaleX;
        public double DipScaleY;
        public double ReboundScaleY;
        public double ContentPopScale;
        public double GlowPeakOpacity;
        public int PeakTimeMs;
        public int SettleOvershootTimeMs;
        public int DurationMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnProgressSpringState
    {
        public double DisplayRatio;
        public double TargetRatio;
        public double SpringTargetRatio;
        public double Velocity;
        public int SettleFrames;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudLayoutOut
    {
        public int Percentage;
        public int ActiveTicks;
        public int HighlightStartTick;
        public int IconCodepoint;
        public double LabelOffsetX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudTransitionParamsOut
    {
        public double HudFromScaleX;
        public double HudFromScaleY;
        public double HudToScaleX;
        public double HudToScaleY;
        public double HudFromTranslateY;
        public double HudToTranslateY;
        public double HudFromBlur;
        public double HudToBlur;
        public double MediaToScale;
        public double MediaToBlur;
        public double TargetWidth;
        public double TargetHeight;
        public double TargetCornerRadius;
        public int MorphDurationMs;
        public int FadeDurationMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSettingsSwitchMotionOut
    {
        public double TargetX;
        public double PeakScaleX;
        public double DipScaleY;
        public double ReboundScaleX;
        public double ReboundScaleY;
        public double PressScaleX;
        public double PressScaleY;
        public double PressShiftX;
        public int PeakTimeMs;
        public int ReboundTimeMs;
        public int DurationMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnNavTabMotionOut
    {
        public double TargetY;
        public double TargetHeight;
        public double PeakScaleY;
        public double DipScaleX;
        public double ReboundScaleY;
        public double ReboundScaleX;
        public double ContentShiftX;
        public int PeakTimeMs;
        public int ReboundTimeMs;
        public int DurationMs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnQuickActionsSlideMotionOut
    {
        public double ResultsFromX;
        public double ResultsToX;
        public double ActionsFromX;
        public double ActionsToX;
        public double ItemSlideX;
        public int ExitDurationMs;
        public int EnterDurationMs;
        public int StaggerStepMs;
    }

    // 1. vn_glass_analyze_backdrop_samples
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_glass_analyze_backdrop_samples(
        ReadOnlySpan<int> rgbSamples,
        int sampleLen,
        int columns,
        int rows,
        out VnGlassBackdropOpticsOut outOptics);

    // 2. vn_clipboard_store_validate_archive_extract_step
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_store_validate_archive_extract_step(
        int entryIndex,
        int maxEntries,
        long declaredLength,
        long accumulatedBytes,
        long maxTotalBytes,
        ReadOnlySpan<char> rootPrefix,
        int rootPrefixLen,
        ReadOnlySpan<char> resolvedPath,
        int resolvedPathLen,
        out byte outIsValid);

    // 3. vn_glass_build_refraction_rows
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_glass_build_refraction_rows(
        in VnGlassMapParams parms,
        ReadOnlySpan<double> lut,
        int lutLen,
        int yStart,
        int yEnd,
        Span<int> outMap);

    // 4. vn_visualizer_spectrum_compute & vn_visualizer_spectrum_update_ranges
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_visualizer_spectrum_compute(
        ReadOnlySpan<VnComplexFloat> fft,
        int fftLength,
        ReadOnlySpan<int> starts,
        ReadOnlySpan<int> ends,
        int lastBin,
        double powerScale,
        Span<double> powersScratch,
        Span<double> outEnergies);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_visualizer_spectrum_update_ranges(
        int sampleRate,
        int fftLength,
        int powersLength,
        Span<int> outStarts,
        Span<int> outEnds,
        out int outLastBin);

    // 5. vn_settings_double_equals & vn_settings_service_normalize_numeric
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_settings_double_equals(
        double a,
        double b,
        byte allowNanEqual,
        out byte outEqual);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_service_normalize_numeric(
        ref VnSettingsNormalizationParams inoutParams,
        out byte outChanged);

    // 6. vn_webcam_convert_nv12_to_bgra8
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_webcam_convert_nv12_to_bgra8(
        ReadOnlySpan<byte> src,
        int width,
        int height,
        Span<byte> dst);

    // 7. vn_local_file_search_*
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_local_file_search_create(out long handle);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_local_file_search_close(long handle);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_local_file_search_run(
        long handle,
        ReadOnlySpan<char> query,
        int queryLength,
        ReadOnlySpan<char> roots,
        int rootsLength,
        ReadOnlySpan<char> hidden,
        int hiddenLength,
        int limit,
        int budgetMs,
        out int count);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_local_file_search_get_result(
        long handle,
        int index,
        Span<char> path,
        int capacity,
        out int length,
        out int kind,
        out double score);

    // 8. vn_glass_frame_hash_128
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_glass_frame_hash_128(
        ReadOnlySpan<byte> data,
        int length,
        out ulong outLo,
        out ulong outHi);

    // 9. vn_settings_search_is_normalized_match
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_search_is_normalized_match(
        ReadOnlySpan<char> candidatePtr,
        int candidateLen,
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        out byte outResult);

    // 10. vn_pip_try_find_pip_window
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_pip_try_find_pip_window(
        ReadOnlySpan<char> targetProcPtr,
        int targetProcLen,
        ReadOnlySpan<char> currentTrackPtr,
        int currentTrackLen,
        out nint outHwnd,
        Span<char> titleBuf,
        int titleCap,
        out int outTitleLen,
        out byte outFound);

    // 11. vn_sensitive_data_scrub
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_sensitive_data_scrub(
        ReadOnlySpan<char> inputPtr,
        int inputLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outChanged);

    // 12. vn_smartcrop_parse_yolox_and_nms & vn_smartcrop_compute_saliency_crop_rect
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_smartcrop_parse_yolox_and_nms(
        ReadOnlySpan<float> buffer,
        int imgWidth,
        int imgHeight,
        float scale,
        Span<VnDetection> outDetections,
        int outCap,
        out int outRawCount,
        out int outNmsCount);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_smartcrop_compute_saliency_crop_rect(
        ReadOnlySpan<byte> pixels,
        int w,
        int h,
        int stride,
        int imgWidth,
        int imgHeight,
        int targetSize,
        out VnCropRect outRect);

    // 13. vn_calculator_try_evaluate
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_calculator_try_evaluate(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        out double outValue,
        out byte outValid);

    // 14. vn_qwen_engine_compute_output_budget
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_qwen_engine_compute_output_budget(
        int contextSize,
        int usedTokens,
        int reserveTokens,
        int inputTokens,
        int maxOutputTokens,
        out int outBudget,
        out byte outOk);

    // 15. vn_selection_text_reader_sanitize
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_text_reader_sanitize(
        ReadOnlySpan<char> part,
        int partLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    // 16. vn_spotlight_usage_compute_boost
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_usage_compute_boost(
        int count,
        long lastLaunchedUtcTicks,
        long nowUtcTicks,
        byte remembered,
        out double outBoost);

    // 17. vn_fast_blur_darken_pixels
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_fast_blur_darken_pixels(
        Span<byte> pixels,
        int length,
        float factor);

    // 18. vn_http_create / vn_http_destroy / vn_http_get_headers
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_http_create(out long outHandle);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_http_destroy(long handle);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_http_get_headers(
        long handle,
        Span<char> buffer,
        int capacity,
        out int outLength);

    // 19. vn_ui_smooth_scroll_step & vn_ui_scheduled_timer_compute_due_timestamp
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_ui_smooth_scroll_step(
        double currentOffset,
        double targetOffset,
        double velocity,
        double maxScroll,
        double friction,
        double minVelocity,
        double dtSeconds,
        out double outNextOffset,
        out double outNextVelocity,
        out byte outSettled);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ui_scheduled_timer_compute_due_timestamp(
        long nowTimestamp,
        double intervalSeconds,
        long stopwatchFrequency,
        out long outDueTimestamp);

    // 20. AnimationMath exports
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_progress_spring_step(
        ref VnProgressSpringState state,
        double frameSeconds,
        double playbackRate,
        double elapsedMs,
        out byte outFinished);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_volume_hud_state(
        double volume,
        byte muted,
        int tickCount,
        double trackWidth,
        double labelWidth,
        double minLeftX,
        out VnVolumeHudLayoutOut outLayout,
        Span<byte> outTickStates,
        int tickCap);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_volume_hud_transition(
        byte entering,
        byte islandMode,
        byte enableBlur,
        double collapsedWidth,
        double collapsedHeight,
        double collapsedRadius,
        out VnVolumeHudTransitionParamsOut outParams);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_category_pill_motion(
        double currentX,
        double currentWidth,
        double targetX,
        double targetWidth,
        byte reducedMotion,
        out VnCategoryPillMotionOut outMotion);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_settings_switch_motion(
        double trackWidth,
        double thumbWidth,
        double marginX,
        byte isChecked,
        byte isPressed,
        byte reducedMotion,
        out VnSettingsSwitchMotionOut outMotion);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_nav_tab_motion(
        double currentY,
        double targetY,
        double itemHeight,
        byte reducedMotion,
        out VnNavTabMotionOut outMotion);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_elastic_slider_overscroll(
        double overflowPx,
        double usableWidth,
        byte reducedMotion,
        out double outScaleX,
        out double outScaleY);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_animation_compute_spotlight_quick_actions_motion(
        byte entering,
        double viewportWidth,
        byte reducedMotion,
        out VnQuickActionsSlideMotionOut outMotion);

    // 21. SystemInterop & AppIntegrityService exports
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_update_verify_authenticode(
        ReadOnlySpan<char> path,
        int pathLength,
        out byte outTrusted);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_file_create_delete_on_close(
        ReadOnlySpan<char> path,
        int pathLength,
        out long outHandle,
        out int outLastError);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_win32_find_window(
        ReadOnlySpan<char> className,
        int classLength,
        out long outHwnd);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_process_image_path_equals(
        uint processId,
        ReadOnlySpan<char> expected,
        int expectedLength,
        out byte outEqual);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int vn_app_integrity_is_release_asset_host(
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        out byte outAssetHost);

    // 22. Thumbprint / City / Process Name normalizers
    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_update_security_policy_normalize_thumbprint(
        ReadOnlySpan<char> value,
        int valueLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_settings_service_clean_manual_city(
        ReadOnlySpan<char> city,
        int cityLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_extract_process_name(
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [Fact]
    public void Finding01_GlassAnalyzeBackdropSamples_OverflowingDimensions_ReturnsDefaultOpticsWithoutCrash()
    {
        Span<int> samples = stackalloc int[4] { 0x112233, 0x445566, 0x778899, 0xAABBCC };

        int status = vn_glass_analyze_backdrop_samples(
            samples,
            sampleLen: 4,
            columns: 4,
            rows: 0x40000001,
            out VnGlassBackdropOpticsOut optics);

        Assert.Equal(VN_OK, status);
        Assert.Equal(128, optics.Red);
        Assert.Equal(128, optics.Green);
        Assert.Equal(128, optics.Blue);
        Assert.Equal(0.0, optics.Contrast);
    }

    [Fact]
    public void Finding02_ClipboardStoreValidateArchiveExtractStep_EnforcesDirectoryBoundary()
    {
        ReadOnlySpan<char> rootPrefix = @"C:\Temp\dir";
        ReadOnlySpan<char> siblingPath = @"C:\Temp\dir2\file.txt";
        ReadOnlySpan<char> validChild = @"C:\Temp\dir\file.txt";
        ReadOnlySpan<char> exactRoot = @"C:\Temp\dir";

        int statusSibling = vn_clipboard_store_validate_archive_extract_step(
            entryIndex: 0,
            maxEntries: 100,
            declaredLength: 64,
            accumulatedBytes: 0,
            maxTotalBytes: 1024 * 1024,
            rootPrefix,
            rootPrefix.Length,
            siblingPath,
            siblingPath.Length,
            out byte isValidSibling);
        Assert.Equal(VN_OK, statusSibling);
        Assert.Equal(0, isValidSibling);

        int statusChild = vn_clipboard_store_validate_archive_extract_step(
            entryIndex: 0,
            maxEntries: 100,
            declaredLength: 64,
            accumulatedBytes: 0,
            maxTotalBytes: 1024 * 1024,
            rootPrefix,
            rootPrefix.Length,
            validChild,
            validChild.Length,
            out byte isValidChild);
        Assert.Equal(VN_OK, statusChild);
        Assert.Equal(1, isValidChild);

        int statusExactRoot = vn_clipboard_store_validate_archive_extract_step(
            entryIndex: 0,
            maxEntries: 100,
            declaredLength: 64,
            accumulatedBytes: 0,
            maxTotalBytes: 1024 * 1024,
            rootPrefix,
            rootPrefix.Length,
            exactRoot,
            exactRoot.Length,
            out byte isValidExactRoot);
        Assert.Equal(VN_OK, statusExactRoot);
        Assert.Equal(0, isValidExactRoot);
    }

    [Fact]
    public void Finding03_GlassBuildRefractionRows_NonFiniteFloatingPointParams_ReturnsInvalidArg()
    {
        Span<double> lut = stackalloc double[64];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = i / 63.0;
        }

        Span<int> outMap = stackalloc int[16 * 16];
        var baseParams = new VnGlassMapParams
        {
            OutW = 16,
            OutH = 16,
            SrcW = 32,
            SrcH = 32,
            Margin = 2,
            NotchW = 16,
            NotchH = 16,
            NotchOffX = 0,
            NotchOffY = 0,
            CaptureShiftX = 0,
            CaptureShiftY = 0,
            OutScale = 1.0,
            TopCornerRadius = 4.0,
            BottomCornerRadius = 4.0,
            EdgeBend = 0.25,
            ChromaticAberration = 0.0,
            Distortion = 0.1,
            FPower = 2.0,
            RefractionA = 0.1,
            RefractionB = 0.1,
            RefractionC = 0.1,
            RefractionD = 1.0
        };

        VnGlassMapParams nanScale = baseParams;
        nanScale.OutScale = double.NaN;
        Assert.Equal(VN_ERR_INVALID_ARG, vn_glass_build_refraction_rows(in nanScale, lut, lut.Length, 0, 16, outMap));

        VnGlassMapParams nanRadius = baseParams;
        nanRadius.TopCornerRadius = double.NaN;
        Assert.Equal(VN_ERR_INVALID_ARG, vn_glass_build_refraction_rows(in nanRadius, lut, lut.Length, 0, 16, outMap));

        VnGlassMapParams infEdgeBend = baseParams;
        infEdgeBend.EdgeBend = double.PositiveInfinity;
        Assert.Equal(VN_ERR_INVALID_ARG, vn_glass_build_refraction_rows(in infEdgeBend, lut, lut.Length, 0, 16, outMap));
    }

    [Fact]
    public void Finding04_VisualizerSpectrum_OutOfBoundsLastBinInvertedBandsAndHugeFft_HandledSafely()
    {
        Span<VnComplexFloat> fft = stackalloc VnComplexFloat[64];
        Span<int> starts = stackalloc int[6] { 1, 2, 4, 8, 16, 24 };
        Span<int> ends = stackalloc int[6] { 2, 4, 8, 16, 24, 31 };
        Span<double> powersScratch = stackalloc double[64];
        Span<double> outEnergies = stackalloc double[6];

        // last_bin == fft_length (64) is out of bounds for a 64-element buffer (valid indices 0..63)
        int statusLastBinOob = vn_visualizer_spectrum_compute(
            fft,
            fftLength: 64,
            starts,
            ends,
            lastBin: 64,
            powerScale: 1.0,
            powersScratch,
            outEnergies);
        Assert.Equal(VN_ERR_INVALID_ARG, statusLastBinOob);

        // Inverted band range: starts[0] > ends[0]
        Span<int> invertedStarts = stackalloc int[6] { 10, 2, 4, 8, 16, 24 };
        int statusInvertedBand = vn_visualizer_spectrum_compute(
            fft,
            fftLength: 64,
            invertedStarts,
            ends,
            lastBin: 31,
            powerScale: 1.0,
            powersScratch,
            outEnergies);
        Assert.Equal(VN_ERR_INVALID_ARG, statusInvertedBand);

        // Huge fft_length / max_freq must not overflow to INT32_MIN
        Span<int> outStarts = stackalloc int[6];
        Span<int> outEnds = stackalloc int[6];
        int statusRanges = vn_visualizer_spectrum_update_ranges(
            sampleRate: 1,
            fftLength: 0x40000000,
            powersLength: 64,
            outStarts,
            outEnds,
            out int outLastBin);
        Assert.Equal(VN_OK, statusRanges);
        Assert.NotEqual(int.MinValue, outLastBin);
        Assert.InRange(outLastBin, 0, 63);
        for (int i = 0; i < 6; i++)
        {
            Assert.NotEqual(int.MinValue, outStarts[i]);
            Assert.NotEqual(int.MinValue, outEnds[i]);
            Assert.InRange(outStarts[i], 0, 63);
            Assert.InRange(outEnds[i], outStarts[i], 63);
        }
    }

    [Fact]
    public void Finding05_SettingsDoubleEqualsAndNormalizeNumeric_HandlesNaNCorrectly()
    {
        Assert.Equal(VN_OK, vn_settings_double_equals(double.NaN, 1.0, 0, out byte eq1));
        Assert.Equal(0, eq1);

        Assert.Equal(VN_OK, vn_settings_double_equals(double.NaN, double.NaN, 0, out byte eq2));
        Assert.Equal(0, eq2);

        Assert.Equal(VN_OK, vn_settings_double_equals(double.NaN, double.NaN, 1, out byte eq3));
        Assert.Equal(1, eq3);

        var normParams = new VnSettingsNormalizationParams
        {
            Width = 300,
            Height = 40,
            CornerRadius = 14,
            DynamicIslandWidth = 340,
            DynamicIslandHeight = 40,
            NotificationDuration = 4000,
            AnimationFps = 60,
            MinFps = 30,
            MaxFps = 120,
            SpotifyCanvasBrightness = double.NaN,
            Opacity = double.NaN
        };

        int normStatus = vn_settings_service_normalize_numeric(ref normParams, out byte outChanged);
        Assert.Equal(VN_OK, normStatus);
        Assert.Equal(1, outChanged);
        Assert.Equal(0.95, normParams.Opacity, precision: 6);
        Assert.Equal(0.6, normParams.SpotifyCanvasBrightness, precision: 6);
    }

    [Fact]
    public void Finding06_WebcamConvertNv12ToBgra8_OddDimensions_ReturnsInvalidArg()
    {
        Span<byte> src = stackalloc byte[64];
        Span<byte> dst = stackalloc byte[128];

        Assert.Equal(VN_ERR_INVALID_ARG, vn_webcam_convert_nv12_to_bgra8(src, width: 3, height: 2, dst));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_webcam_convert_nv12_to_bgra8(src, width: 2, height: 3, dst));
    }

    [Fact]
    public void Finding07_LocalFileSearchRun_ReusedHandle_DoesNotAccumulateStaleResults()
    {
        string tempDir = Path.Combine(AppContext.BaseDirectory, "local-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        long handle = 0;

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "alpha_only_item.txt"), "a");
            File.WriteAllText(Path.Combine(tempDir, "bravo_only_item.txt"), "b");

            Assert.Equal(VN_OK, vn_local_file_search_create(out handle));
            Assert.True(handle > 0);

            ReadOnlySpan<char> roots = tempDir.AsSpan();
            ReadOnlySpan<char> query1 = "alpha_only_item";
            Assert.Equal(VN_OK, vn_local_file_search_run(
                handle,
                query1,
                query1.Length,
                roots,
                roots.Length,
                ReadOnlySpan<char>.Empty,
                0,
                limit: 10,
                budgetMs: 2000,
                out int count1));
            Assert.Equal(1, count1);

            ReadOnlySpan<char> query2 = "bravo_only_item";
            Assert.Equal(VN_OK, vn_local_file_search_run(
                handle,
                query2,
                query2.Length,
                roots,
                roots.Length,
                ReadOnlySpan<char>.Empty,
                0,
                limit: 10,
                budgetMs: 2000,
                out int count2));
            Assert.Equal(1, count2);

            Span<char> pathBuf = stackalloc char[512];
            Assert.Equal(VN_OK, vn_local_file_search_get_result(
                handle,
                index: 0,
                pathBuf,
                pathBuf.Length,
                out int pathLen,
                out _,
                out _));
            string matchedPath = new string(pathBuf[..pathLen]);
            Assert.Contains("bravo_only_item.txt", matchedPath, StringComparison.OrdinalIgnoreCase);

            ReadOnlySpan<char> queryEmpty = "nonexistent_zzzz_token_999";
            Assert.Equal(VN_OK, vn_local_file_search_run(
                handle,
                queryEmpty,
                queryEmpty.Length,
                roots,
                roots.Length,
                ReadOnlySpan<char>.Empty,
                0,
                limit: 10,
                budgetMs: 2000,
                out int count3));
            Assert.Equal(0, count3);
            Assert.Equal(VN_ERR_INVALID_ARG, vn_local_file_search_get_result(
                handle,
                index: 0,
                pathBuf,
                pathBuf.Length,
                out _,
                out _,
                out _));
        }
        finally
        {
            if (handle != 0)
            {
                _ = vn_local_file_search_close(handle);
            }
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Finding08_GlassFrameHash128_128ByteBuffer_ProducesDeterministicNonZeroOutput()
    {
        Span<byte> buffer = stackalloc byte[128];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)((i * 37) + 13);
        }

        Assert.Equal(VN_OK, vn_glass_frame_hash_128(buffer, buffer.Length, out ulong lo1, out ulong hi1));
        Assert.Equal(VN_OK, vn_glass_frame_hash_128(buffer, buffer.Length, out ulong lo2, out ulong hi2));

        Assert.True((lo1 | hi1) != 0UL);
        Assert.Equal(lo1, lo2);
        Assert.Equal(hi1, hi2);
    }

    [Fact]
    public void Finding09_SettingsSearchIsNormalizedMatch_MultiWordStrings_MatchesCorrectly()
    {
        ReadOnlySpan<char> candidate = "liquid glass refraction quality";
        ReadOnlySpan<char> matchingQuery = "glass refraction";
        ReadOnlySpan<char> nonMatchingQuery = "glass bluetooth";

        Assert.Equal(VN_OK, vn_settings_search_is_normalized_match(
            candidate, candidate.Length, matchingQuery, matchingQuery.Length, out byte matched));
        Assert.Equal(1, matched);

        Assert.Equal(VN_OK, vn_settings_search_is_normalized_match(
            candidate, candidate.Length, nonMatchingQuery, nonMatchingQuery.Length, out byte notMatched));
        Assert.Equal(0, notMatched);
    }

    [Fact]
    public void Finding10_PipTryFindPipWindow_CompletesSafely()
    {
        ReadOnlySpan<char> procName = "nonexistent_vnotch_pip_test_proc";
        ReadOnlySpan<char> trackName = "Test Track";
        Span<char> titleBuf = stackalloc char[256];

        int status = vn_pip_try_find_pip_window(
            procName,
            procName.Length,
            trackName,
            trackName.Length,
            out nint hwnd,
            titleBuf,
            titleBuf.Length,
            out int titleLen,
            out byte found);

        Assert.Equal(VN_OK, status);
        Assert.Equal(0, found);
        Assert.Equal(nint.Zero, hwnd);
        Assert.Equal(0, titleLen);
    }

    [Fact]
    public void Finding11_SensitiveDataScrub_50000CharRepeatedEscapedQuoteColon_FinishesUnder100Ms()
    {
        const int pairCount = 25_000; // 50,000 chars total of repeated \":
        string pathological = string.Create(pairCount * 2, pairCount, static (span, count) =>
        {
            for (int i = 0; i < count; i++)
            {
                span[i * 2] = '\\';
                span[(i * 2) + 1] = ':';
            }
        });

        char[] outBuf = new char[pathological.Length + 64];

        // Warm up P/Invoke
        _ = vn_sensitive_data_scrub("warmup", 6, outBuf, outBuf.Length, out _, out _);

        var sw = Stopwatch.StartNew();
        int status = vn_sensitive_data_scrub(
            pathological.AsSpan(),
            pathological.Length,
            outBuf.AsSpan(),
            outBuf.Length,
            out int outLen,
            out byte outChanged);
        sw.Stop();

        Assert.Equal(VN_OK, status);
        Assert.Equal(0, outChanged);
        Assert.Equal(0, outLen);
        Assert.True(sw.Elapsed.TotalMilliseconds < 100.0, $"Scrubber took {sw.Elapsed.TotalMilliseconds:F2} ms (expected < 100 ms)");
    }

    [Fact]
    public void Finding12_SmartCrop_InvalidScaleOrDimensions_ReturnsInvalidArg()
    {
        float[] yoloxBuf = new float[3549 * 85];
        Span<VnDetection> detections = stackalloc VnDetection[16];

        Assert.Equal(VN_ERR_INVALID_ARG, vn_smartcrop_parse_yolox_and_nms(
            yoloxBuf, imgWidth: 640, imgHeight: 480, scale: 0.0f, detections, detections.Length, out _, out _));

        Assert.Equal(VN_ERR_INVALID_ARG, vn_smartcrop_parse_yolox_and_nms(
            yoloxBuf, imgWidth: 640, imgHeight: 480, scale: float.NaN, detections, detections.Length, out _, out _));

        Assert.Equal(VN_ERR_INVALID_ARG, vn_smartcrop_parse_yolox_and_nms(
            yoloxBuf, imgWidth: -1, imgHeight: 480, scale: 1.0f, detections, detections.Length, out _, out _));

        Span<byte> pixels = stackalloc byte[4 * 4 * 4];
        Assert.Equal(VN_ERR_INVALID_ARG, vn_smartcrop_compute_saliency_crop_rect(
            pixels, w: 4, h: 4, stride: 16, imgWidth: 100, imgHeight: 100, targetSize: 50, out _));
    }

    [Fact]
    public void Finding13_CalculatorTryEvaluate_NestedDivideByZero_SetsOutValidZero()
    {
        ReadOnlySpan<char> expr = "1/(1/0)";
        int status = vn_calculator_try_evaluate(expr, expr.Length, out _, out byte valid);

        Assert.Equal(VN_OK, status);
        Assert.Equal(0, valid);
    }

    [Fact]
    public void Finding14_QwenEngineComputeOutputBudget_SmallMaxOutputAndHugeInputTokens_ValidBoundedBudget()
    {
        int statusSmallMax = vn_qwen_engine_compute_output_budget(
            contextSize: 4096,
            usedTokens: 100,
            reserveTokens: 64,
            inputTokens: 50,
            maxOutputTokens: 64,
            out int budgetSmallMax,
            out byte okSmallMax);
        Assert.Equal(VN_OK, statusSmallMax);
        Assert.Equal(1, okSmallMax);
        Assert.Equal(64, budgetSmallMax);

        int statusHugeInput = vn_qwen_engine_compute_output_budget(
            contextSize: 4096,
            usedTokens: 100,
            reserveTokens: 64,
            inputTokens: 0x30000000,
            maxOutputTokens: 512,
            out int budgetHugeInput,
            out byte okHugeInput);
        Assert.Equal(VN_OK, statusHugeInput);
        Assert.Equal(1, okHugeInput);
        Assert.InRange(budgetHugeInput, 1, 512);
    }

    [Fact]
    public void Finding15_SelectionTextReaderSanitize_SmallCapacity_ReturnsBufferTooSmallWithRequiredLength()
    {
        ReadOnlySpan<char> raw = "hello";
        Span<char> outBuf = stackalloc char[2];

        int status = vn_selection_text_reader_sanitize(raw, raw.Length, outBuf, outBuf.Length, out int outLen);

        Assert.Equal(VN_ERR_BUFFER_TOO_SMALL, status);
        Assert.Equal(5, outLen);
    }

    [Fact]
    public void Finding16_SpotlightUsageComputeBoost_NegativeCountUnremembered_SetsZeroBoost()
    {
        int status = vn_spotlight_usage_compute_boost(
            count: -2,
            lastLaunchedUtcTicks: 0,
            nowUtcTicks: 0,
            remembered: 0,
            out double boost);

        Assert.Equal(VN_OK, status);
        Assert.Equal(0.0, boost);
    }

    [Fact]
    public void Finding17_FastBlurDarkenPixels_NaNFactorOrNegativeLength_ReturnsInvalidArg()
    {
        Span<byte> pixels = stackalloc byte[16];

        Assert.Equal(VN_ERR_INVALID_ARG, vn_fast_blur_darken_pixels(pixels, pixels.Length, float.NaN));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_fast_blur_darken_pixels(pixels, -1, 0.5f));
    }

    [Fact]
    public void Finding18_HttpGetHeaders_HugeCapacity_ReturnsInvalidArg()
    {
        Assert.Equal(VN_OK, vn_http_create(out long handle));
        try
        {
            Span<char> buffer = stackalloc char[16];
            int status = vn_http_get_headers(handle, buffer, capacity: 0x40000001, out _);
            Assert.Equal(VN_ERR_INVALID_ARG, status);
        }
        finally
        {
            _ = vn_http_destroy(handle);
        }
    }

    [Fact]
    public void Finding19_UiSmoothScrollStepAndComputeDueTimestamp_NonFiniteInputs_SettlesAndAvoidsOverflow()
    {
        int scrollStatus = vn_ui_smooth_scroll_step(
            currentOffset: double.NaN,
            targetOffset: 100.0,
            velocity: 5.0,
            maxScroll: 400.0,
            friction: 0.85,
            minVelocity: 0.5,
            dtSeconds: 0.016,
            out double nextOffset,
            out double nextVelocity,
            out byte settled);

        Assert.Equal(VN_OK, scrollStatus);
        Assert.Equal(1, settled);
        Assert.True(double.IsFinite(nextOffset));
        Assert.True(double.IsFinite(nextVelocity));

        int dueInfStatus = vn_ui_scheduled_timer_compute_due_timestamp(
            nowTimestamp: 1_000_000L,
            intervalSeconds: double.PositiveInfinity,
            stopwatchFrequency: 10_000_000L,
            out long dueInf);
        Assert.Equal(VN_OK, dueInfStatus);
        Assert.True(dueInf >= 1_000_000L);

        int dueNanStatus = vn_ui_scheduled_timer_compute_due_timestamp(
            nowTimestamp: 1_000_000L,
            intervalSeconds: double.NaN,
            stopwatchFrequency: 10_000_000L,
            out long dueNan);
        Assert.Equal(VN_OK, dueNanStatus);
        Assert.True(dueNan >= 1_000_000L);
    }

    [Fact]
    public void Finding20_AnimationMath_NonFiniteAndZeroNegativeDimensions_HandledSafely()
    {
        // Category pill motion rejects zero/negative/NaN target_width with VN_ERR_INVALID_ARG
        Assert.Equal(VN_ERR_INVALID_ARG, vn_animation_compute_category_pill_motion(
            currentX: 0.0, currentWidth: 64.0, targetX: 120.0, targetWidth: 0.0, reducedMotion: 0, out _));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_animation_compute_category_pill_motion(
            currentX: 0.0, currentWidth: 64.0, targetX: 120.0, targetWidth: double.NaN, reducedMotion: 0, out _));
        Assert.Equal(VN_OK, vn_animation_compute_category_pill_motion(
            currentX: double.NaN, currentWidth: -10.0, targetX: double.PositiveInfinity, targetWidth: 80.0, reducedMotion: 0, out VnCategoryPillMotionOut pillMotion));
        Assert.True(double.IsFinite(pillMotion.PeakScaleX));

        // Progress spring step with NaN state snaps cleanly and finishes
        var springState = new VnProgressSpringState
        {
            DisplayRatio = double.NaN,
            TargetRatio = double.NaN,
            SpringTargetRatio = double.PositiveInfinity,
            Velocity = double.NaN,
            SettleFrames = -5
        };
        Assert.Equal(VN_OK, vn_progress_spring_step(ref springState, double.NaN, double.NaN, double.NaN, out byte springFinished));
        Assert.Equal(1, springFinished);
        Assert.True(double.IsFinite(springState.DisplayRatio));

        // Volume HUD state & transition with NaN/negative dimensions
        Span<byte> ticks = stackalloc byte[16];
        Assert.Equal(VN_ERR_INVALID_ARG, vn_animation_compute_volume_hud_state(0.5, 0, tickCount: 0, 160.0, 28.0, 30.0, out _, ticks, ticks.Length));
        Assert.Equal(VN_OK, vn_animation_compute_volume_hud_state(double.NaN, 0, tickCount: 16, double.NaN, -10.0, double.PositiveInfinity, out VnVolumeHudLayoutOut hudLayout, ticks, ticks.Length));
        Assert.True(double.IsFinite(hudLayout.LabelOffsetX));

        Assert.Equal(VN_OK, vn_animation_compute_volume_hud_transition(1, 1, 1, double.NaN, -20.0, double.PositiveInfinity, out VnVolumeHudTransitionParamsOut hudTrans));
        Assert.True(double.IsFinite(hudTrans.TargetWidth) && hudTrans.TargetWidth > 0.0);

        // Settings switch, nav tab, elastic slider, and quick actions motion with NaN/Infinity
        Assert.Equal(VN_OK, vn_animation_compute_settings_switch_motion(double.NaN, double.PositiveInfinity, double.NaN, 1, 1, 0, out VnSettingsSwitchMotionOut switchMotion));
        Assert.True(double.IsFinite(switchMotion.TargetX) && double.IsFinite(switchMotion.PeakScaleX));

        Assert.Equal(VN_OK, vn_animation_compute_nav_tab_motion(double.NaN, double.PositiveInfinity, -10.0, 0, out VnNavTabMotionOut navMotion));
        Assert.True(double.IsFinite(navMotion.PeakScaleY) && double.IsFinite(navMotion.DipScaleX));

        Assert.Equal(VN_OK, vn_animation_compute_elastic_slider_overscroll(double.NaN, double.PositiveInfinity, 0, out double overScaleX, out double overScaleY));
        Assert.True(double.IsFinite(overScaleX) && double.IsFinite(overScaleY));

        Assert.Equal(VN_OK, vn_animation_compute_spotlight_quick_actions_motion(1, double.NaN, 0, out VnQuickActionsSlideMotionOut qaMotion));
        Assert.True(double.IsFinite(qaMotion.ResultsToX) && double.IsFinite(qaMotion.ActionsFromX));
    }

    [Fact]
    public void Finding21_VerifyAuthenticodeAndSystemInterop_PathSlicesAndEmbeddedNulls_StrictlyBounded()
    {
        string signedBinary = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        Assert.True(File.Exists(signedBinary));

        Assert.Equal(VN_OK, vn_update_verify_authenticode(signedBinary.AsSpan(), signedBinary.Length, out byte baselineTrusted));
        Assert.Equal(1, baselineTrusted);

        const string trailingGarbage = "TRAILING_NON_NUL_GARBAGE_SUFFIX.exe";
        char[] unnullTerminatedSlice = (signedBinary + trailingGarbage).ToCharArray();

        // Passing only signedBinary.Length must ignore the adjacent non-NUL characters in memory
        int sliceStatus = vn_update_verify_authenticode(unnullTerminatedSlice.AsSpan(), signedBinary.Length, out byte sliceTrusted);
        Assert.Equal(VN_OK, sliceStatus);
        Assert.Equal(baselineTrusted, sliceTrusted);

        // Passing the full buffer length (which points to a nonexistent path) must not be trusted
        int fullStatus = vn_update_verify_authenticode(unnullTerminatedSlice.AsSpan(), unnullTerminatedSlice.Length, out byte fullTrusted);
        Assert.Equal(VN_OK, fullStatus);
        Assert.Equal(0, fullTrusted);

        // Embedded NULs in paths, window classes, and hosts are rejected
        ReadOnlySpan<char> nulPath = "C:\\Windows\\System32\\kernel32.dll\0evil.exe";
        Assert.Equal(VN_ERR_INVALID_ARG, vn_update_verify_authenticode(nulPath, nulPath.Length, out _));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_file_create_delete_on_close(nulPath, nulPath.Length, out _, out _));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_win32_find_window("Shell_TrayWnd\0Evil", 18, out _));
        Assert.Equal(VN_ERR_INVALID_ARG, vn_process_image_path_equals((uint)Environment.ProcessId, nulPath, nulPath.Length, out _));

        ReadOnlySpan<char> nulHost = "objects.githubusercontent.com\0.evil.com";
        Assert.Equal(VN_OK, vn_app_integrity_is_release_asset_host(nulHost, nulHost.Length, out byte assetHost));
        Assert.Equal(0, assetHost);
    }

    [Fact]
    public void Finding22_Normalizers_InsufficientOutputCapacity_ReturnBufferTooSmallWithRequiredLength()
    {
        ReadOnlySpan<char> thumbprint = "0123456789abcdef0123456789abcdef01234567"; // 40 hex chars
        Span<char> smallThumbBuf = stackalloc char[10];
        int thumbStatus = vn_update_security_policy_normalize_thumbprint(
            thumbprint, thumbprint.Length, smallThumbBuf, smallThumbBuf.Length, out int thumbLen);
        Assert.Equal(VN_ERR_BUFFER_TOO_SMALL, thumbStatus);
        Assert.Equal(40, thumbLen);

        ReadOnlySpan<char> city = "Ho Chi Minh City"; // 16 chars
        Span<char> smallCityBuf = stackalloc char[5];
        int cityStatus = vn_settings_service_clean_manual_city(
            city, city.Length, smallCityBuf, smallCityBuf.Length, out int cityLen);
        Assert.Equal(VN_ERR_BUFFER_TOO_SMALL, cityStatus);
        Assert.Equal(16, cityLen);

        ReadOnlySpan<char> sourceApp = "Spotify.exe"; // extracted "Spotify" -> 7 chars
        Span<char> smallProcBuf = stackalloc char[4];
        int procStatus = vn_media_detection_extract_process_name(
            sourceApp, sourceApp.Length, smallProcBuf, smallProcBuf.Length, out int procLen);
        Assert.Equal(VN_ERR_BUFFER_TOO_SMALL, procStatus);
        Assert.Equal(7, procLen);
    }
}
