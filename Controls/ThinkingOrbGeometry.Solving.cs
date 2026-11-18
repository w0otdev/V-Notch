// Rubik-style motion inspired by thinking-orbs/src/engine/lattice.ts (frameRubik), MIT.
// Jakub Antalik, revision de85557ca220332586d070d8788c0e1d6e877a0d.
// See docs/licenses/ThinkingOrbs-MIT.txt. A cube-symmetric spherical lattice
// realigns after every quarter-turn instead of accumulating displaced rows.
namespace VNotch.Controls;

internal static partial class ThinkingOrbGeometry
{
    private readonly record struct SolvePoint(double X, double Y, double Z);
    private readonly record struct SolveTurn(int Axis, int Side, int Direction);
    private static readonly SolvePoint[] SolvePoints = CreateSolvePoints();
    private static readonly SolveTurn[] SolveTurns =
    [
        new(0, 1, 1), new(1, -1, -1), new(2, 1, -1),
        new(0, -1, -1), new(1, 1, 1), new(2, -1, 1)
    ];

    private static int Solving(Dot[] dots, double size, double t)
    {
        // About 0.23s per turn and 0.075s to settle at the control's time scale.
        const double turnDuration = 1.65, slotDuration = 2.2;
        double phase = t % slotDuration;
        var turn = SolveTurns[(int)(t / slotDuration) % SolveTurns.Length];
        double progress = Math.Min(phase / turnDuration, 1);
        double eased = progress * progress * progress * (progress * (progress * 6 - 15) + 10);
        double angle = turn.Direction * Math.PI / 2 * eased;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var projection = new Projection(.4 + t * .16, size, .35, size * .41);
        double radiusScale = Math.Pow(size / 300, .6);
        int count = 0;
        foreach (var point in SolvePoints)
        {
            double x = point.X, y = point.Y, z = point.Z;
            double coordinate = turn.Axis == 0 ? x : turn.Axis == 1 ? y : z;
            // This cut sits in the lattice's empty band: the nearest fixed
            // and moving rows remain at least 0.198 sphere radii apart.
            if (coordinate * turn.Side > .3)
            {
                if (turn.Axis == 0) (y, z) = (y * cos - z * sin, y * sin + z * cos);
                else if (turn.Axis == 1) (x, z) = (x * cos + z * sin, -x * sin + z * cos);
                else (x, y) = (x * cos - y * sin, x * sin + y * cos);
            }
            var p = projection.Project(x, y, z);
            double depth = (p.Z + 1) / 2;
            dots[count++] = new Dot(p.X, p.Y, p.Z,
                Math.Max(.3, (.63 + 1.785 * depth) * radiusScale),
                .62 - .54 * depth, 1);
        }
        return count;
    }

    private static SolvePoint[] CreateSolvePoints()
    {
        // Project a 6 x 6 grid on each cube face onto the sphere (152 unique
        // points). Unlike latitude rings, this set is invariant under 90-degree
        // turns around all three axes, so completed turns need no stored scramble.
        var points = new List<SolvePoint>(152);
        for (int ix = -5; ix <= 5; ix += 2)
        for (int iy = -5; iy <= 5; iy += 2)
        for (int iz = -5; iz <= 5; iz += 2)
        {
            if (Math.Max(Math.Abs(ix), Math.Max(Math.Abs(iy), Math.Abs(iz))) != 5) continue;
            double length = Math.Sqrt(ix * ix + iy * iy + iz * iz);
            points.Add(new SolvePoint(ix / length, iy / length, iz / length));
        }
        return points.ToArray();
    }
}
