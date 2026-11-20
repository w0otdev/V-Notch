// Port of Details.so Orb 01 / Jakub Antalik's thinking-orbs (MIT).
// See docs/licenses/ThinkingOrbs-MIT.txt. The demo interpolates the 20/64
// presets at 40px; render that same composition at the chat control's size.
namespace VNotch.Controls;

internal static partial class AgentOrbGeometry
{
    internal const int Capacity = 160;
    private const double ReferenceSize = 40;
    private const double PresetBlend = (ReferenceSize - 20) / 44;
    private static readonly IComparer<ThinkingOrbGeometry.Dot> DepthOrder =
        Comparer<ThinkingOrbGeometry.Dot>.Create((a, b) => a.Z.CompareTo(b.Z));

    internal static int WriteFrame(ThinkingOrbGeometry.Dot[] dots, ThinkingOrbState state, double size, double seconds)
    {
        int count = state switch
        {
            ThinkingOrbState.Listening => Listening(dots, seconds * Mix(3.998, 4.388)),
            ThinkingOrbState.Solving => Solving(dots, seconds * Mix(1.95, 1.82)),
            ThinkingOrbState.Shaping => Shaping(dots, seconds * Mix(2.08, 2.405)),
            _ => Searching(dots, seconds * Mix(2.665, 2.015))
        };
        // Stable insertion sort preserves the reference painter order at equal depths.
        for (int i = 1; i < count; i++)
        {
            var dot = dots[i];
            int j = i - 1;
            while (j >= 0 && DepthOrder.Compare(dots[j], dot) > 0) { dots[j + 1] = dots[j]; j--; }
            dots[j + 1] = dot;
        }
        double scale = size / ReferenceSize;
        for (int i = 0; i < count; i++)
        {
            var dot = dots[i];
            dots[i] = dot with { X = dot.X * scale, Y = dot.Y * scale, Radius = dot.Radius * scale };
        }
        return count;
    }

    private static double Mix(double small, double large) => small + (large - small) * PresetBlend;
    private static int Round(double value) => (int)Math.Floor(value + .5);
    private static double RadiusScale => Math.Pow(ReferenceSize / 300, .6);
    private static double Hash(double a, double b)
    {
        double h = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
        return h - Math.Floor(h);
    }

    private readonly struct Projection(double yaw, double tilt, double scale)
    {
        private readonly double _sy = Math.Sin(yaw), _cy = Math.Cos(yaw);
        private readonly double _st = Math.Sin(tilt), _ct = Math.Cos(tilt);
        internal (double X, double Y, double Z) Project(double x, double y, double z)
        {
            double x1 = x * _cy + z * _sy, z1 = -x * _sy + z * _cy;
            return (ReferenceSize / 2 + x1 * scale,
                ReferenceSize / 2 - (y * _ct - z1 * _st) * scale, y * _st + z1 * _ct);
        }
    }
}
