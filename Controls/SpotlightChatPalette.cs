using System.Windows.Media;

namespace VNotch.Controls;

// Opaque ink stays predictable over Spotlight's black and glass surfaces.
// Secondary text also clears 4.5:1 over a white backdrop under the 64% black scrim.
internal static class SpotlightChatPalette
{
    public static SolidColorBrush Primary { get; } = Freeze(0xEE, 0xF0, 0xF4);
    public static SolidColorBrush Secondary { get; } = Freeze(0xD4, 0xD7, 0xDE);
    public static SolidColorBrush Icon { get; } = Secondary;
    public static SolidColorBrush Surface { get; } = Freeze(0x18, 0x1A, 0x1E);
    public static SolidColorBrush Hover { get; } = Freeze(0x29, 0x2C, 0x33);
    public static SolidColorBrush Pressed { get; } = Freeze(0x36, 0x3A, 0x43);
    public static SolidColorBrush Border { get; } = Freeze(0x45, 0x49, 0x52);
    public static SolidColorBrush Focus { get; } = Secondary;
    // Vivid red uses the opaque status surface to stay legible over bright glass.
    public static SolidColorBrush Error { get; } = Freeze(0xFF, 0x5C, 0x64);
    public static SolidColorBrush Link { get; } = Freeze(0xC3, 0xD9, 0xFF);

    internal static Color UserBubbleColor(Color accent)
    {
        double r = accent.R / 255d, g = accent.G / 255d, b = accent.B / 255d;
        double lightness = (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
        double chromaRange = Math.Min(lightness, 1 - lightness);

        // Lower HSL lightness while preserving the accent's hue and saturation.
        // An opaque fill keeps the result readable over any desktop behind the glass.
        Color Shade(double target)
        {
            double scale = chromaRange > 0 ? target / chromaRange : 0;
            byte Channel(double value) => (byte)Math.Round(Math.Clamp(target + (value - lightness) * scale, 0, 1) * 255);
            return Color.FromRgb(Channel(r), Channel(g), Channel(b));
        }

        double upper = Math.Min(lightness * 0.65, 0.38);
        double maximumLuminance = (Luminance(Primary.Color) + 0.05) / 4.5 - 0.05;
        var color = Shade(upper);
        if (Luminance(color) <= maximumLuminance) return color;

        // Bright hues need a little more darkening to keep the same light text.
        double lower = 0;
        for (int i = 0; i < 12; i++)
        {
            double middle = (lower + upper) / 2;
            if (Luminance(Shade(middle)) <= maximumLuminance) lower = middle;
            else upper = middle;
        }
        return Shade(lower);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) => channel / 255d <= 0.04045
            ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
