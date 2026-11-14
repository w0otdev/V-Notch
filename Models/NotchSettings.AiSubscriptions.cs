namespace VNotch.Models;

public partial class NotchSettings
{
    public bool AiUsageShowCodex { get; set; } = true;
    public bool AiUsageShowClaude { get; set; } = true;
    public bool AiUsageClaudeFirst { get; set; }
    public bool AiUsageCompact { get; set; }
    public bool AiUsageShowUsedPercent { get; set; }
    public bool AiUsageShowWeekly { get; set; } = true;
    public bool AiUsageShowResets { get; set; } = true;
    public bool AiUsageShowPlan { get; set; } = true;
    public bool AiUsageShowLogos { get; set; } = true;
    public bool AiUsageShowAccount { get; set; } = true;
    public bool AiUsageStatusColors { get; set; } = true;
    private int _aiUsageBarHeight = 5;
    public int AiUsageBarHeight { get => _aiUsageBarHeight; set => _aiUsageBarHeight = ClampInt(value, 3, 10); }
    private int _aiUsageMotionIntensity = 75;
    public int AiUsageMotionIntensity { get => _aiUsageMotionIntensity; set => _aiUsageMotionIntensity = ClampInt(value, 0, 100); }
    private string _aiUsageCodexColor = "#00D491";
    public string AiUsageCodexColor { get => _aiUsageCodexColor; set => _aiUsageCodexColor = NormalizeAiUsageColor(value, "#00D491"); }
    private string _aiUsageClaudeColor = "#D97757";
    public string AiUsageClaudeColor { get => _aiUsageClaudeColor; set => _aiUsageClaudeColor = NormalizeAiUsageColor(value, "#D97757"); }

    private static string NormalizeAiUsageColor(string? value, string fallback) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit)
            ? value.ToUpperInvariant() : fallback;
}
