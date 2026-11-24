using System.Drawing;

namespace VNotch.Models;

// The anchor is the top center of the notch, relative to the selected monitor.
public sealed record NotchPosition(double XFraction, double YFraction)
{
    internal static NotchPosition FromAnchor(Rectangle screen, Point anchor) => new(
        Math.Clamp((anchor.X - (double)screen.Left) / screen.Width, 0, 1),
        Math.Clamp((anchor.Y - (double)screen.Top) / screen.Height, 0, 1));

    internal Point Resolve(Rectangle screen, int notchWidth, int notchHeight)
    {
        int width = Math.Clamp(notchWidth, 0, screen.Width);
        int height = Math.Clamp(notchHeight, 0, screen.Height);
        double x = double.IsFinite(XFraction) ? Math.Clamp(XFraction, 0, 1) : 0.5;
        double y = double.IsFinite(YFraction) ? Math.Clamp(YFraction, 0, 1) : 0;
        return new Point(
            screen.Left + Math.Clamp((int)Math.Round(x * screen.Width), width / 2, screen.Width - (width + 1) / 2),
            screen.Top + Math.Min((int)Math.Round(y * screen.Height), screen.Height - height));
    }
}
