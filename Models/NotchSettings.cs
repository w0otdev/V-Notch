using System.Reflection;
using System.Text.Json.Serialization;

namespace VNotch.Models;

public class NotchSettings
{
    public const string SystemBatteryDeviceId = "";
    public const string AutoBluetoothBatteryDeviceId = "__auto_bluetooth__";

    public int SettingsVersion { get; set; } = 0;

    public string LastRunVersion { get; set; } = "";

    public int Width { get; set; } = 230;
    public int DynamicIslandWidth { get; set; } = 220;
    public int DynamicIslandHeight { get; set; } = 40;
    public int Height { get; set; } = 34;
    public int CornerRadius { get; set; } = 8;
    public double Opacity { get; set; } = 1.0;
    public double MediaBlurBrightnessBoost { get; set; } = 2.0;
    public double MediaBlurDarkOverlay { get; set; } = 0.0;
    public bool EnableBlurEffects { get; set; } = true;
    public bool ShowMediaArtBackground { get; set; } = true;
    public int AnimationFps { get; set; } = 240;

    public int MonitorIndex { get; set; } = 0;

    public string CameraDeviceId { get; set; } = "";
    public string VisualizerAudioDeviceId { get; set; } = "";
    public string BatteryDeviceId { get; set; } = SystemBatteryDeviceId;

    public bool AutoStart { get; set; } = true;
    public bool StayBehindWindows { get; set; } = false;
    public bool EnableHoverExpand { get; set; } = false;
    public bool EnableCursorBypass { get; set; } = false;
    public bool EnableAnimations { get; set; } = true;
    public bool DisableMouseLeaveAutoClose { get; set; } = true;
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

    // The user's personally tuned Liquid Glass values. Kept as a separate slot so
    // that applying a built-in preset (Frosted/Dark) never destroys what the user
    // hand-tuned — selecting "Custom Settings" always restores exactly this.
    public LiquidGlassConfig? LiquidGlassCustom { get; set; }

    // Which Liquid Glass preset is active: "custom", "frosted" or "dark".
    public string LiquidGlassPreset { get; set; } = "custom";

    public bool EnableDynamicIslandMode { get; set; } = false;

    public bool HideOnExclusiveFullscreen { get; set; } = false;
    public bool HideOnWindowedFullscreen { get; set; } = false;

    public bool EnableDebugMode { get; set; } = false;

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

    public bool EnableSpotifyLyrics { get; set; } = true;

    public bool EnableSpotifyCanvas { get; set; } = true;

    public double SpotifyCanvasBrightness { get; set; } = 0.7;

    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string SpotifySpDc { get; set; } = "";

    public bool EnableYouTubeSubtitles { get; set; } = false;

    public bool IsShelfUploadLimitUnlocked { get; set; } = true;

    public bool CopyShelfFilesToClipboard { get; set; } = false;

    public bool EnableWeather { get; set; } = false;
    public string ManualCity { get; set; } = string.Empty;

    public bool EnableYouTubeApi { get; set; } = false;

    [JsonConverter(typeof(VNotch.Services.DpapiJsonConverter))]
    public string YouTubeApiKey { get; set; } = "";

    public string SubtitlePriority { get; set; } = "native,english,auto";

    public string Language { get; set; } = "en";

    public string ExpandedWidget { get; set; } = "clock";

    public string ProcessPriority { get; set; } = "Normal";
    public int GpuPreference { get; set; } = 0;

    public bool HasSeenSpotlightIntro { get; set; } = false;

    [JsonIgnore]
    public bool IsDirty { get; set; } = false;

    private static readonly PropertyInfo[] _cloneableProperties = Array.FindAll(
        typeof(NotchSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    public NotchSettings Clone()
    {
        var clone = new NotchSettings();
        for (int i = 0; i < _cloneableProperties.Length; i++)
        {
            var prop = _cloneableProperties[i];
            prop.SetValue(clone, prop.GetValue(this));
        }
        clone.LiquidGlass = LiquidGlass?.Clone() ?? new LiquidGlassConfig();
        clone.LiquidGlassCustom = LiquidGlassCustom?.Clone();
        return clone;
    }
}

public class LiquidGlassConfig
{
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

    // OverShifted LiquidGlass Core Parameters
    public double PowerFactor { get; set; } = 3.0;
    public double RefractionA { get; set; } = 0.7;
    public double RefractionB { get; set; } = 2.3;
    public double RefractionC { get; set; } = 5.2;
    public double RefractionD { get; set; } = 6.9;
    public double FPower { get; set; } = 1.0;
    public double Noise { get; set; } = 0.06;
    public double GlowWeight { get; set; } = 0.25;
    public double GlowBias { get; set; } = 0.0;
    public double GlowEdge0 { get; set; } = 0.15;
    public double GlowEdge1 { get; set; } = 0.0;

    // Legacy compatibility only. The magnifier filter now excludes the notch from
    // its private backdrop source without hiding the visible overlay in screenshots.
    public bool HideFromScreenCapture { get; set; } = false;

    // New installations use the native-resolution GPU lens. Existing settings keep
    // their serialized value, including an explicit false from the old opt-in UI.
    public bool UseGpuRefraction { get; set; } = true;

    public LiquidGlassConfig Clone() => (LiquidGlassConfig)MemberwiseClone();
}
