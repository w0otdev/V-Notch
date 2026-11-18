// Adapted from Jakub Antalik's thinking-orbs (MIT).
// See docs/licenses/ThinkingOrbs-MIT.txt and docs/THIRD_PARTY_NOTICES.md.
namespace VNotch.Controls;

public enum ThinkingOrbState { Working, Composing, Solving, Searching, Shaping, Listening }

/// <summary>The reference's small-orb presets, projected from rotating 3D particles.</summary>
internal static partial class ThinkingOrbGeometry
{
    internal readonly record struct Dot(double X, double Y, double Z, double Radius, double Ink, double Alpha);
    internal const int Capacity = 208;
    private static readonly IComparer<Dot> DepthOrder = Comparer<Dot>.Create((a, b) => a.Z.CompareTo(b.Z));

    internal static int WriteFrame(Dot[] dots, ThinkingOrbState state, double size, double seconds)
    {
        int count = state switch
        {
            ThinkingOrbState.Working => Working(dots, size, seconds * 3.9),
            ThinkingOrbState.Composing => Composing(dots, size, seconds * 3.12),
            ThinkingOrbState.Solving => Solving(dots, size, seconds * 7.28),
            ThinkingOrbState.Searching or ThinkingOrbState.Shaping or ThinkingOrbState.Listening
                => AgentOrbGeometry.WriteFrame(dots, state, size, seconds),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        Array.Sort(dots, 0, count, DepthOrder);
        return count;
    }

    private static int Working(Dot[] dots, double size, double t)
    {
        double radius = size * .41;
        double scale = Math.Pow(size / 300, .6) * 2.4;
        var projection = new Projection(t * .12, size);
        int count = 0;
        // 12 orbits / 40 ghost dots scaled by the upstream 0.238 small preset.
        for (int orbit = 0; orbit < 3; orbit++)
        {
            double h1 = Hash(orbit, 1.7), h2 = Hash(orbit, 5.2), h3 = Hash(orbit, 8.9);
            double r = radius * (.45 + .52 * h1);
            double theta = h1 * Math.Tau, phi = Math.Acos(2 * h2 - 1);
            double nx = Math.Sin(phi) * Math.Cos(theta), ny = Math.Cos(phi), nz = Math.Sin(phi) * Math.Sin(theta);
            double length = Math.Max(1e-6, Math.Sqrt(ny * ny + nx * nx));
            double ux = -ny / length, uy = nx / length;
            double vx = -nz * uy, vy = nz * ux, vz = nx * uy - ny * ux;
            double speed = (.25 + .55 * h3) * (h3 > .5 ? 1 : -1);
            for (int i = 0; i < 13; i++)
            {
                bool ghost = i < 10;
                double angle = ghost ? i / 10d * Math.Tau : t * speed + (i - 10) / 3d * Math.Tau + h2 * 6;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                var p = projection.Project((ux * cos + vx * sin) * r, (uy * cos + vy * sin) * r, vz * sin * r);
                double depth = (p.Z / r + 1) / 2;
                dots[count++] = new Dot(p.X, p.Y, p.Z,
                    Math.Max(.3, (ghost ? .9 : 1.2 + 1.6 * depth) * scale),
                    ghost ? .72 : .3 - .22 * depth,
                    ghost ? .5 * (.4 + .6 * depth) : 1);
            }
        }
        return count;
    }

    private static int Composing(Dot[] dots, double size, double t)
    {
        double radius = size * .39;
        double scale = Math.Pow(size / 300, .6);
        var projection = new Projection(0, size);
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            double y = 1 - 2 * (i + .5) / 8, radial = Math.Sqrt(1 - y * y);
            double angle = i * Math.PI * (3 - Math.Sqrt(5));
            var p = projection.Project(radial * Math.Cos(angle) * radius, y * radius, radial * Math.Sin(angle) * radius);
            double depth = (p.Z / radius + 1) / 2;
            dots[count++] = new Dot(p.X, p.Y, p.Z, Math.Max(.3, .8 * scale), .78, .1 + .22 * depth);
        }
        // Two sampled lanes expanded by bandMul=4.94; spin=0 fixes the band plane.
        double tiltCos = Math.Cos(.55), tiltSin = Math.Sin(.55);
        for (int lane = 0; lane < 10; lane++)
        {
            double laneOffset = (lane - 4.5) * .075, edge = Math.Abs(lane - 4.5) / 4.5;
            for (int k = 0; k < 20; k++)
            {
                double angle = k / 20d * Math.Tau;
                double wobble = .16 * Math.Sin(angle * 3 - t * 1.7 + lane * .22) + .07 * Math.Sin(angle * 5 + t * 1.1);
                double offset = laneOffset + wobble;
                double x = Math.Cos(angle), y = tiltCos * Math.Sin(angle) - tiltSin * offset;
                double z = tiltSin * Math.Sin(angle) + tiltCos * offset;
                double factor = radius / Math.Sqrt(x * x + y * y + z * z);
                var p = projection.Project(x * factor, y * factor, z * factor);
                double depth = (p.Z / radius + 1) / 2;
                dots[count++] = new Dot(p.X, p.Y, p.Z,
                    Math.Max(.3, (1.1 + 1.7 * depth) * 1.073 * (1 - .25 * edge) * scale),
                    .52 - .44 * depth + .18 * edge, .4 + .6 * depth);
            }
        }
        return count;
    }

    private static double Hash(double a, double b)
    {
        double h = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
        return h - Math.Floor(h);
    }

    private readonly struct Projection(double yaw, double size, double tilt = .3, double scale = 1)
    {
        private readonly double _sin = Math.Sin(yaw), _cos = Math.Cos(yaw);
        private readonly double _tiltSin = Math.Sin(tilt), _tiltCos = Math.Cos(tilt);
        internal (double X, double Y, double Z) Project(double x, double y, double z)
        {
            double x1 = x * _cos + z * _sin, z1 = -x * _sin + z * _cos;
            double y1 = y * _tiltCos - z1 * _tiltSin;
            return (size / 2 + x1 * scale, size / 2 - y1 * scale, y * _tiltSin + z1 * _tiltCos);
        }
    }
}
