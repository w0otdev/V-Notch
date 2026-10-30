using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace VNotch.Models;

public partial class NotchSettings
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    internal static partial int vn_settings_clamp_int(int value, int minVal, int maxVal, out int outValue);

    [LibraryImport(NativeDll)]
    internal static partial int vn_settings_clamp_double(double value, double minVal, double maxVal, double fallbackIfNonFinite, out double outValue);

    [LibraryImport(NativeDll)]
    internal static partial int vn_settings_double_equals(double a, double b, byte allowNanEqual, out byte outEqual);

    private static int ClampInt(int value, int min, int max) =>
        vn_settings_clamp_int(value, min, max, out int result) == 0 ? result : value;

    private static double ClampDouble(double value, double min, double max, double fallback) =>
        vn_settings_clamp_double(value, min, max, fallback, out double result) == 0 ? result : fallback;

    public const string SystemBatteryDeviceId = "";
    public const string AutoBluetoothBatteryDeviceId = "__auto_bluetooth__";

    public int SettingsVersion { get; set; } = 0;
    public bool InvisibleMode { get; set; }
    public bool EnableInteractionSounds { get; set; } = true;
    private double _interactionSoundVolume = .55;
    public double InteractionSoundVolume
    {
        get => _interactionSoundVolume;
        set => _interactionSoundVolume = ClampDouble(value, 0, 1, .55);
    }

    public string LastRunVersion { get; set; } = "";

    public int Width { get; set; } = 230;
    public int DynamicIslandWidth { get; set; } = 220;
    public int DynamicIslandHeight { get; set; } = 40;
    public int Height { get; set; } = 34;
    public int CornerRadius { get; set; } = 8;
    public double Opacity { get; set; } = 1.0;
    public double MediaBlurBrightnessBoost { get; set; } = 2.0;
    public double MediaBlurDarkOverlay { get; set; } = 0.0;
    public bool BrightenDarkLyricsBackground { get; set; } = false;
    public bool EnableBlurEffects { get; set; } = true;
    public bool ShowMediaArtBackground { get; set; } = true;
    public int AnimationFps { get; set; } = 240;
    public bool AutoAnimationFps { get; set; } = true;

    public int MonitorIndex { get; set; } = 0;
    public string MonitorDeviceId { get; set; } = "";

    public bool HideCamera { get; set; } = false;
    public bool ClipboardAutoDeleteSensitive { get; set; }
    public bool ClipboardCaptureText { get; set; } = true;
    public bool ClipboardCaptureImages { get; set; } = true;
    public bool ClipboardCaptureFiles { get; set; } = true;
    private int _clipboardCaptureDelay = 250;
    public int ClipboardCaptureDelay { get => _clipboardCaptureDelay; set => _clipboardCaptureDelay = ClampInt(value, 100, 2000); }
    public string ClipboardHotkey { get; set; } = "";

    public string TranslationModelId { get; set; } = "qwen3-4b-instruct-2507-q4km-v1";
    public bool EnableLiveTranslation { get; set; } = false;
    public bool TranslationIgnoreFullscreen { get; set; } = true;
    public string TranslationExcludedApps { get; set; } = "cs2;csgo;VALORANT-Win64-Shipping;GenshinImpact";
    public bool AutoLiveTranslation { get; set; } = false;
    public bool FormatTranslationText { get; set; } = true;
    public bool EnableTranslationHumanizer { get; set; } = true;
    public string TranslationSourceLanguage { get; set; } = "auto";
    public string TranslationTargetLanguage { get; set; } = "vi";
    public bool EnableCurrencyConversion { get; set; } = true;
    public string TranslationTargetCurrency { get; set; } = "auto";
    private int _translationModelIdleMinutes = 5;
    public int TranslationModelIdleMinutes { get => _translationModelIdleMinutes; set => _translationModelIdleMinutes = ClampInt(value, 0, 60); }
    private int _translationCacheMinutes = 30;
    public int TranslationCacheMinutes { get => _translationCacheMinutes; set => _translationCacheMinutes = ClampInt(value, 1, 240); }
    private int _translationCacheEntries = 128;
    public int TranslationCacheEntries { get => _translationCacheEntries; set => _translationCacheEntries = ClampInt(value, 0, 512); }
    private int _translationGpuLayers = 36;
    public int TranslationGpuLayers { get => _translationGpuLayers; set => _translationGpuLayers = ClampInt(value, 0, 128); }
    private int _translationThreads = 0;
    public int TranslationThreads { get => _translationThreads; set => _translationThreads = ClampInt(value, 0, 64); }
    private int _translationBatchThreads = 0;
    public int TranslationBatchThreads { get => _translationBatchThreads; set => _translationBatchThreads = ClampInt(value, 0, 64); }
    private int _translationContextSize = 2048;
    public int TranslationContextSize { get => _translationContextSize; set => _translationContextSize = ClampInt(value, 2048, 8192); }
    private int _translationBatchSize = 512;
    public int TranslationBatchSize { get => _translationBatchSize; set => _translationBatchSize = ClampInt(value, 32, 512); }
    private int _translationOutputTokens = 768;
    public int TranslationOutputTokens { get => _translationOutputTokens; set => _translationOutputTokens = ClampInt(value, 128, 1024); }
    private int _translationTemperaturePercent = 20;
    public int TranslationTemperaturePercent { get => _translationTemperaturePercent; set => _translationTemperaturePercent = ClampInt(value, 0, 100); }
    private int _translationTopPPercent = 80;
    public int TranslationTopPPercent { get => _translationTopPPercent; set => _translationTopPPercent = ClampInt(value, 1, 100); }
    private int _translationTopK = 20;
    public int TranslationTopK { get => _translationTopK; set => _translationTopK = ClampInt(value, 1, 100); }
    private int _translationTimeoutSeconds = 90;
    public int TranslationTimeoutSeconds { get => _translationTimeoutSeconds; set => _translationTimeoutSeconds = ClampInt(value, 15, 600); }


    public string CameraDeviceId { get; set; } = "";
    public string VisualizerAudioDeviceId { get; set; } = "";
    public string BatteryDeviceId { get; set; } = SystemBatteryDeviceId;

    public bool AutoStart { get; set; } = true;
    public bool StayBehindWindows { get; set; } = false;
    public bool EnableHoverExpand { get; set; } = false;
    public bool EnableCursorBypass { get; set; } = false;
    public bool EnableAnimations { get; set; } = true;
    public bool DisableMouseLeaveAutoClose { get; set; } = true;
    public bool KeepMediaPinnedOnTrackChange { get; set; } = true;
    public bool ReopenLastViewOnExpand { get; set; } = false;

    public double AnimationSpeed { get; set; } = 2.0;
    public bool EnableBounceEffect { get; set; } = true;

    public int HoverExpandDelay { get; set; } = 0;
    public int HoverCollapseDelay { get; set; } = 500;
    public int HoverZoneMargin { get; set; } = 60;

    public double CompactExpandMultiplier { get; set; } = 1.2;
    public double MediumExpandMultiplier { get; set; } = 1.8;
    public double LargeExpandMultiplier { get; set; } = 2.5;

    public bool EnableShadow { get; set; } = true;
    public bool EnableGlowOnHover { get; set; } = true;
    public string NotchStyle { get; set; } = "default";

    public LiquidGlassConfig LiquidGlass { get; set; } = new();

    // User's custom Liquid Glass settings; kept separate so applying presets
    // does not overwrite hand-tuned values when switching back to Custom.
    public LiquidGlassConfig? LiquidGlassCustom { get; set; }

    // Which Liquid Glass preset is active: "custom", "frosted" or "dark".
    public string LiquidGlassPreset { get; set; } = "custom";

    public bool EnableDynamicIslandMode { get; set; } = false;

    public bool HideOnExclusiveFullscreen { get; set; } = false;
    public bool HideOnWindowedFullscreen { get; set; } = false;

    public bool EnableDebugMode { get; set; } = false;
    public double? DebugWindowX { get; set; }
    public double? DebugWindowY { get; set; }

    public bool EnableIdleAutoHide { get; set; } = false;
    public int IdleAutoHideDelay { get; set; } = 5000;

    public bool ShowMusicNotifications { get; set; } = true;
    public bool ShowSystemNotifications { get; set; } = true;

    public bool ShowBatteryIndicator { get; set; } = true;
    public int NotificationDuration { get; set; } = 5000;

    public bool EnableSmartCrop { get; set; } = true;

    public bool EnableSubjectBlur { get; set; } = true;

    public bool EnableGestureControls { get; set; } = true;

    public bool EnableHelloGreeting { get; set; } = true;

    public bool EnableSpotlight { get; set; } = true;
    public bool SpotlightDefaultAi { get; set; } = false;
    public int SpotlightAiWordsPerSecond { get; set; } = 9;
    public string SpotlightAiProvider { get; set; } = "OpenAI";
    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotlightOpenAIApiKey { get; set; } = "";
    public string SpotlightOpenAIModel { get; set; } = "";
    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotlightGeminiApiKey { get; set; } = "";
    public string SpotlightGeminiModel { get; set; } = "";
    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotlightClaudeApiKey { get; set; } = "";
    public string SpotlightClaudeModel { get; set; } = "";
    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotlightDeepSeekApiKey { get; set; } = "";
    public string SpotlightDeepSeekModel { get; set; } = "";


    public bool EnableSpotifyLyrics { get; set; } = true;

    public bool EnableSpotifyCanvas { get; set; } = false;
    public int SpotifyCanvasConsentVersion { get; set; } = 0;

    public double SpotifyCanvasBrightness { get; set; } = 0.7;

    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotifySpDc { get; set; } = "";

    public bool EnableYouTubeSubtitles { get; set; } = true;
    public bool IgnoreYouTubeAutoSubtitles { get; set; } = true;




    public bool EnableWeather { get; set; } = false;
    public string ManualCity { get; set; } = string.Empty;

    public bool EnableYouTubeApi { get; set; } = false;

    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string YouTubeApiKey { get; set; } = "";

    public string SubtitlePriority { get; set; } = "native,english,auto";

    public string Language { get; set; } = "en";

    public string ExpandedWidget { get; set; } = "clock";
    public string NavTabOrder { get; set; } = "Media,Secondary,Timer,AudioMixer,Camera";
    public string VisibleNavTabs { get; set; } = "Media,Secondary,Timer,AudioMixer,Camera";
    public string ClockPageStyle { get; set; } = "analog";

    public string ProcessPriority { get; set; } = "Normal";
    public int GpuPreference { get; set; } = 0;

    public bool HasSeenSpotlightIntro { get; set; } = false;
    public bool HasSeenCpp26Notice { get; set; } = false;

    public bool EnableLocalOnlyMode { get; set; } = false;
    public bool AllowOnlineAi { get; set; } = true;
    public bool AllowOnlineSubtitles { get; set; } = true;
    public bool AllowOnlineCanvas { get; set; } = false;
    public bool AllowOnlineWeather { get; set; } = true;
    public bool SaveAiChatHistory { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool IncludePrereleaseUpdates { get; set; } = false;
    public bool EnableOnlineArtworkLookup { get; set; } = true;
    public bool EnableOnlineLyrics { get; set; } = true;
    public bool EnableBrowserUrlInspection { get; set; } = true;
    public bool EnablePrivacyIndicators { get; set; } = true;
    public bool EnableDiagnosticLogging { get; set; } = true;
    public bool EnableSpotlightHistory { get; set; } = true;

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    private static readonly PropertyInfo[] _cloneableProperties = Array.FindAll(
        typeof(NotchSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    private static readonly Func<NotchSettings, NotchSettings, bool> _scalarValueEquals = BuildScalarValueEquals();

    private static bool DoubleEquals(double d1, double d2) =>
        vn_settings_double_equals(d1, d2, 0, out byte equal) == 0 && equal != 0;

    private static bool NullableDoubleEquals(double? d1, double? d2)
    {
        if (d1.HasValue != d2.HasValue)
            return false;
        return !d1.HasValue || DoubleEquals(d1.Value, d2!.Value);
    }

    private static bool OrdinalStringEquals(string? s1, string? s2) =>
        string.Equals(s1, s2, StringComparison.Ordinal);

    private static Func<NotchSettings, NotchSettings, bool> BuildScalarValueEquals()
    {
        var left = Expression.Parameter(typeof(NotchSettings), "left");
        var right = Expression.Parameter(typeof(NotchSettings), "right");
#pragma warning disable S3011
        var doubleEqualsMethod = typeof(NotchSettings).GetMethod(nameof(DoubleEquals), BindingFlags.NonPublic | BindingFlags.Static)!;
        var nullableDoubleEqualsMethod = typeof(NotchSettings).GetMethod(nameof(NullableDoubleEquals), BindingFlags.NonPublic | BindingFlags.Static)!;
        var stringEqualsMethod = typeof(NotchSettings).GetMethod(nameof(OrdinalStringEquals), BindingFlags.NonPublic | BindingFlags.Static)!;
#pragma warning restore S3011

        Expression? combined = null;
        for (int i = 0; i < _cloneableProperties.Length; i++)
        {
            var prop = _cloneableProperties[i];
            if (prop.PropertyType == typeof(LiquidGlassConfig))
                continue;

            var p1 = Expression.Property(left, prop);
            var p2 = Expression.Property(right, prop);
            Expression check;

            if (prop.PropertyType == typeof(double))
            {
                check = Expression.Call(doubleEqualsMethod, p1, p2);
            }
            else if (prop.PropertyType == typeof(double?))
            {
                check = Expression.Call(nullableDoubleEqualsMethod, p1, p2);
            }
            else if (prop.PropertyType == typeof(string))
            {
                check = Expression.Call(stringEqualsMethod, p1, p2);
            }
            else if (prop.PropertyType.IsPrimitive || prop.PropertyType.IsEnum)
            {
                check = Expression.Equal(p1, p2);
            }
            else
            {
                var eqMethod = typeof(object).GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Static)!;
                check = Expression.Call(
                    eqMethod,
                    Expression.Convert(p1, typeof(object)),
                    Expression.Convert(p2, typeof(object)));
            }

            combined = combined is null ? check : Expression.AndAlso(combined, check);
        }

        var body = combined ?? Expression.Constant(true);
        return Expression.Lambda<Func<NotchSettings, NotchSettings, bool>>(body, left, right).Compile();
    }

    public NotchSettings Clone()
    {
        var clone = (NotchSettings)MemberwiseClone();
        clone.IsDirty = false;
        clone.LiquidGlass = LiquidGlass?.Clone() ?? new LiquidGlassConfig();
        clone.LiquidGlassCustom = LiquidGlassCustom?.Clone();
        return clone;
    }

    public bool ValueEquals(NotchSettings? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        if (!_scalarValueEquals(this, other))
            return false;

        if ((LiquidGlass == null) != (other.LiquidGlass == null))
            return false;
        if (LiquidGlass != null && other.LiquidGlass != null && !LiquidGlass.ValueEquals(other.LiquidGlass))
            return false;

        if ((LiquidGlassCustom == null) != (other.LiquidGlassCustom == null))
            return false;
        if (LiquidGlassCustom != null && other.LiquidGlassCustom != null && !LiquidGlassCustom.ValueEquals(other.LiquidGlassCustom))
            return false;

        return true;
    }
}

public partial class LiquidGlassConfig
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnLiquidGlassScalars
    {
        public double BlurAmount;
        public double Refraction;
        public double EdgeBend;
        public double ChromaticAberration;
        public double EdgeHighlight;
        public double TouchLight;
        public double Specular;
        public double Fresnel;
        public double Distortion;
        public int CornerRadius;
        public double ZRadius;
        public double Opacity;
        public double Saturation;
        public double Brightness;
        public double ShadowOpacity;
        public int ShadowSpread;
        public int BevelMode;
        public int TargetFps;
        public int Variant;
        public double PowerFactor;
        public double RefractionA;
        public double RefractionB;
        public double RefractionC;
        public double RefractionD;
        public double FPower;
        public double Noise;
        public double GlowWeight;
        public double GlowBias;
        public double GlowEdge0;
        public double GlowEdge1;
        public byte HideFromScreenCapture;
        public byte UseGpuRefraction;
        public byte UseUltimateGpu;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_liquid_glass_config_equals(
        ref VnLiquidGlassScalars a,
        ref VnLiquidGlassScalars b,
        out byte outEqual);

    // Defaults are tuned from OverShifted/LiquidGlass Apple squircle shader architecture.
    public double BlurAmount { get; set; } = 0.3;
    public double Refraction { get; set; } = 1.0;
    public double EdgeBend { get; set; } = 1.65;
    public double ChromaticAberration { get; set; } = 0.56;
    public double EdgeHighlight { get; set; } = 0.1;
    public double TouchLight { get; set; } = 0.9;
    public double Specular { get; set; } = 0.0;
    public double Fresnel { get; set; } = 0.0;
    public double Distortion { get; set; } = 0.32;
    public int CornerRadius { get; set; } = 20;
    public double ZRadius { get; set; } = 0.23;
    public double Opacity { get; set; } = 1.0;
    public double Saturation { get; set; } = 0.15;
    public double Brightness { get; set; } = -0.05;
    public double ShadowOpacity { get; set; } = 0.85;
    public int ShadowSpread { get; set; } = 24;
    public int BevelMode { get; set; } = 0;
    public int TargetFps { get; set; } = 0;
    public int Variant { get; set; } = 0; // 0 = Regular (Adaptive), 1 = Clear (highly transparent, non-adaptive)

    // OverShifted LiquidGlass Core Parameters (matching showcase preset)
    public double PowerFactor { get; set; } = 3.0;
    public double RefractionA { get; set; } = 0.7;
    public double RefractionB { get; set; } = 2.3;
    public double RefractionC { get; set; } = 5.2;
    public double RefractionD { get; set; } = 6.9;
    public double FPower { get; set; } = 1.0;
    public double Noise { get; set; } = 0.066;
    public double GlowWeight { get; set; } = 0.692;
    public double GlowBias { get; set; } = -0.040;
    public double GlowEdge0 { get; set; } = 0.441;
    public double GlowEdge1 { get; set; } = -0.474;

    // Legacy compatibility only. The magnifier filter now excludes the notch from
    // its private backdrop source without hiding the visible overlay in screenshots.
    public bool HideFromScreenCapture { get; set; } = false;

    // New installations use the native-resolution GPU lens. Existing settings keep
    // their serialized value, including an explicit false from the old opt-in UI.
    public bool UseGpuRefraction { get; set; } = true;

    // Dedicated high-performance GPU (GPU 0 / dGPU) mode for Liquid Glass, paced strictly to TargetFps.
    public bool UseUltimateGpu { get; set; } = false;

    public LiquidGlassConfig Clone() => (LiquidGlassConfig)MemberwiseClone();

    private VnLiquidGlassScalars ToNative() => new()
    {
        BlurAmount = BlurAmount,
        Refraction = Refraction,
        EdgeBend = EdgeBend,
        ChromaticAberration = ChromaticAberration,
        EdgeHighlight = EdgeHighlight,
        TouchLight = TouchLight,
        Specular = Specular,
        Fresnel = Fresnel,
        Distortion = Distortion,
        CornerRadius = CornerRadius,
        ZRadius = ZRadius,
        Opacity = Opacity,
        Saturation = Saturation,
        Brightness = Brightness,
        ShadowOpacity = ShadowOpacity,
        ShadowSpread = ShadowSpread,
        BevelMode = BevelMode,
        TargetFps = TargetFps,
        Variant = Variant,
        PowerFactor = PowerFactor,
        RefractionA = RefractionA,
        RefractionB = RefractionB,
        RefractionC = RefractionC,
        RefractionD = RefractionD,
        FPower = FPower,
        Noise = Noise,
        GlowWeight = GlowWeight,
        GlowBias = GlowBias,
        GlowEdge0 = GlowEdge0,
        GlowEdge1 = GlowEdge1,
        HideFromScreenCapture = HideFromScreenCapture ? (byte)1 : (byte)0,
        UseGpuRefraction = UseGpuRefraction ? (byte)1 : (byte)0,
        UseUltimateGpu = UseUltimateGpu ? (byte)1 : (byte)0,
    };

    public bool ValueEquals(LiquidGlassConfig? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        var left = ToNative();
        var right = other.ToNative();
        return vn_liquid_glass_config_equals(ref left, ref right, out byte equal) == 0 && equal != 0;
    }
}
