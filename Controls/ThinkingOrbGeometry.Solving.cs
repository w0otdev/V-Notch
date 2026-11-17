// Adapted from thinking-orbs/src/engine/lattice.ts (frameRubik), MIT.
// Jakub Antalik, revision de85557ca220332586d070d8788c0e1d6e877a0d.
// See docs/licenses/ThinkingOrbs-MIT.txt. The reference's regular preset
// retains its 138 dots at the 40-DIP size used by Details.so's Solving demo.
namespace VNotch.Controls;

internal static partial class ThinkingOrbGeometry
{
    private const int SolveMoveCount = 14;
    private readonly record struct SolvePoint(double X, double Y, double Z);
    private readonly record struct SolveMove(int Axis, double Low, double Angle);
    private static readonly SolvePoint[] SolvePoints = CreateSolvePoints();
    private static readonly SolveMove[] SolveMoves = CreateSolveMoves();

    private static int Solving(Dot[] dots, double size, double t)
    {
        const double slotDuration = .42;
        const double restDuration = 1.2;
        double time = t % (2 * SolveMoveCount * slotDuration + restDuration);
        Span<double> amounts = stackalloc double[SolveMoveCount];
        amounts.Clear();
        int active = -1;
        if (time < 2 * SolveMoveCount * slotDuration)
        {
            int slot = (int)(time / slotDuration);
            double progress = Math.Min(1, (time - slot * slotDuration) / slotDuration / .7);
            double eased = 1 - Math.Pow(1 - progress, 3);
            active = slot < SolveMoveCount ? slot : 2 * SolveMoveCount - 1 - slot;
            amounts[..active].Fill(1);
            amounts[active] = slot < SolveMoveCount ? eased : 1 - eased;
        }

        // The same deterministic moves scramble, then unwind in reverse order.
        // Trigonometry is shared by all particles and no frame allocates arrays.
        Span<double> cosines = stackalloc double[SolveMoveCount];
        Span<double> sines = stackalloc double[SolveMoveCount];
        for (int i = 0; i < SolveMoveCount; i++)
        {
            double angle = SolveMoves[i].Angle * amounts[i];
            cosines[i] = Math.Cos(angle);
            sines[i] = Math.Sin(angle);
        }
        var projection = new Projection(t * .55, size, .35 + .1 * Math.Sin(t * .9), size * .41);
        double radiusScale = Math.Pow(size / 300, .6);
        int count = 0;
        foreach (var point in SolvePoints)
        {
            double x = point.X, y = point.Y, z = point.Z;
            bool inActive = false;
            for (int i = 0; i < SolveMoveCount; i++)
            {
                if (amounts[i] <= 0) continue;
                var move = SolveMoves[i];
                double coordinate = move.Axis == 0 ? x : move.Axis == 1 ? y : z;
                if (coordinate < move.Low || coordinate >= move.Low + .5) continue;
                if (i == active) inActive = true;
                double cos = cosines[i], sin = sines[i];
                if (move.Axis == 0) (y, z) = (y * cos - z * sin, y * sin + z * cos);
                else if (move.Axis == 1) (x, z) = (x * cos + z * sin, -x * sin + z * cos);
                else (x, y) = (x * cos - y * sin, x * sin + y * cos);
            }
            var p = projection.Project(x, y, z);
            double depth = (p.Z + 1) / 2;
            dots[count++] = new Dot(p.X, p.Y, p.Z,
                Math.Max(.3, (.63 + 1.785 * depth + (inActive ? .315 : 0)) * radiusScale),
                .62 - .54 * depth - (inActive ? .14 : 0), 1);
        }
        return count;
    }

    private static SolvePoint[] CreateSolvePoints()
    {
        var points = new List<SolvePoint>();
        for (int ring = 0; ring <= 9; ring++)
        {
            double latitude = -Math.PI / 2 + ring / 9d * Math.PI;
            double cos = Math.Cos(latitude), sin = Math.Sin(latitude);
            int longitudeCount = Math.Max(1, (int)Math.Floor(Math.Abs(cos) * 24 + .5));
            for (int longitude = 0; longitude < longitudeCount; longitude++)
            {
                double angle = longitude / (double)longitudeCount * Math.Tau;
                points.Add(new SolvePoint(cos * Math.Cos(angle), sin, cos * Math.Sin(angle)));
            }
        }
        return points.ToArray();
    }

    private static SolveMove[] CreateSolveMoves()
    {
        var moves = new SolveMove[SolveMoveCount];
        for (int i = 0; i < moves.Length; i++)
            moves[i] = new SolveMove(Math.Min(2, (int)(Hash(i, 2.3) * 3)),
                -1 + .5 * Math.Min(3, (int)(Hash(i, 5.9) * 4)),
                (Hash(i, 7.7) < .5 ? 1 : -1) * Math.PI / 2);
        return moves;
    }
}
