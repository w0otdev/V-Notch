using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal sealed record AiUsageAppearance
{
    internal static AiUsageAppearance Current { get; private set; } = new();
    internal static event Action? Changed;
    internal bool ShowCodex { get; init; } = true;
    internal bool ShowClaude { get; init; } = true;
    internal bool ClaudeFirst { get; init; }
    internal bool Compact { get; init; }
    internal bool ShowUsed { get; init; }
    internal bool ShowWeekly { get; init; } = true;
    internal bool ShowResets { get; init; } = true;
    internal bool ShowPlan { get; init; } = true;
    internal bool ShowLogos { get; init; } = true;
    internal bool ShowAccount { get; init; } = true;
    internal bool StatusColors { get; init; } = true;
    internal int BarHeight { get; init; } = 5;
    internal int MotionIntensity { get; init; } = 75;
    internal string CodexColor { get; init; } = "#00D491";
    internal string ClaudeColor { get; init; } = "#D97757";

    internal static AiUsageAppearance From(NotchSettings settings) => new()
    {
        ShowCodex = settings.AiUsageShowCodex, ShowClaude = settings.AiUsageShowClaude,
        ClaudeFirst = settings.AiUsageClaudeFirst, Compact = settings.AiUsageCompact,
        ShowUsed = settings.AiUsageShowUsedPercent, ShowWeekly = settings.AiUsageShowWeekly,
        ShowResets = settings.AiUsageShowResets, ShowPlan = settings.AiUsageShowPlan,
        ShowLogos = settings.AiUsageShowLogos, ShowAccount = settings.AiUsageShowAccount,
        StatusColors = settings.AiUsageStatusColors, BarHeight = settings.AiUsageBarHeight,
        MotionIntensity = settings.AiUsageMotionIntensity, CodexColor = settings.AiUsageCodexColor,
        ClaudeColor = settings.AiUsageClaudeColor
    };

    internal static void Apply(NotchSettings settings) => Apply(From(settings));

    internal static void Apply(AiUsageAppearance appearance)
    {
        if (Current == appearance) return;
        Current = appearance;
        Changed?.Invoke();
    }

    internal bool Shows(string provider) => provider == "Claude" ? ShowClaude : ShowCodex;
    internal double Percent(AiQuotaWindow window) => ShowUsed ? window.UsedPercent : window.RemainingPercent;
    internal string PercentText(double percent) => Loc.Get(ShowUsed ? "aiUsage.used" : "aiUsage.left", percent.ToString("0.#", Loc.GetCulture()));
    internal Brush Fill(string provider, double used)
    {
        if (SystemParameters.HighContrast) return SystemColors.HighlightBrush;
        if (StatusColors)
        {
            return Application.Current?.TryFindResource("AccentBrush") as Brush
                ?? AccentTheme.Brush(AccentTheme.GetSwatch("default").Color);
        }
        string hex = provider == "Claude" ? ClaudeColor : CodexColor;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal void BindFill(Border fill, string provider, double used)
    {
        fill.Background = Fill(provider, used);
        // AccentTheme replaces its resource brushes; keep existing quota bars live.
        if (!SystemParameters.HighContrast && StatusColors &&
            Application.Current?.TryFindResource("AccentBrush") is Brush)
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
    }
}
