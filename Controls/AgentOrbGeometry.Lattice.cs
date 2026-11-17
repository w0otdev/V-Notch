// Searching, Listening and Solving from thinking-orbs/engine/lattice.ts (MIT).
namespace VNotch.Controls;

internal static partial class AgentOrbGeometry
{
    private readonly record struct Move(int Axis, double Low, double High, double Angle);
    private static readonly Move[] Moves = CreateMoves();

    private static int Searching(ThinkingOrbGeometry.Dot[] dots, double t)
    {
        double density = Math.Sqrt(Mix(.105, .42)), radius = Mix(1.75, 1.15);
        int rings = Round(17 * density), longitudes = Round(44 * density), count = 0;
        var projection = new Projection(t * .5, .4 + .06 * Math.Sin(t * .35), ReferenceSize * .41);
        double scan = t * (.5 + 1.2 * Mix(4.335, 4.08));
        for (int ring = 0; ring <= rings; ring++)
        {
            double latitude = -Math.PI / 2 + ring / (double)rings * Math.PI;
            double cosLat = Math.Cos(latitude), sinLat = Math.Sin(latitude);
            int n = Math.Max(1, Round(Math.Abs(cosLat) * longitudes));
            for (int j = 0; j < n; j++)
            {
                double longitude = j / (double)n * Math.Tau;
                var p = projection.Project(cosLat * Math.Cos(longitude), sinLat, cosLat * Math.Sin(longitude));
                double depth = (p.Z + 1) / 2;
                double delta = Math.Atan2(Math.Sin(longitude + t * .5 - scan), Math.Cos(longitude + t * .5 - scan));
                double boost = Math.Exp(-delta * delta / .18) * Math.Max(0, p.Z);
                dots[count++] = new(p.X, p.Y, p.Z,
                    Math.Max(.3, ((.6 + 1.7 * depth) * radius + boost) * RadiusScale),
                    .62 - .54 * depth, .45 + .55 * Math.Min(1, boost));
            }
        }
        return count;
    }

    private static int Listening(ThinkingOrbGeometry.Dot[] dots, double t)
    {
        double density = Math.Sqrt(Mix(.105, .341)), dotRadius = Mix(1.6, 1);
        int rings = Round(15 * density), longitudes = Round(40 * density), count = 0;
        const double radius = ReferenceSize * .437;
        var projection = new Projection(t * .18, .38, 1);
        for (int ring = 0; ring <= rings; ring++)
        {
            double latitude = -Math.PI / 2 + ring / (double)rings * Math.PI;
            double cosLat = Math.Cos(latitude), sinLat = Math.Sin(latitude);
            double wave = .62 * Math.Sin(t * 2.1 - ring * .52) + .38 * Math.Sin(t * 1.27 + ring * .83);
            double rr = radius * (.88 + .105 * wave), crest = Math.Max(0, wave);
            int n = Math.Max(1, Round(Math.Abs(cosLat) * longitudes));
            for (int j = 0; j < n; j++)
            {
                double longitude = j / (double)n * Math.Tau;
                var p = projection.Project(cosLat * Math.Cos(longitude) * rr, sinLat * rr, cosLat * Math.Sin(longitude) * rr);
                double depth = (p.Z / radius + 1) / 2;
                dots[count++] = new(p.X, p.Y, p.Z,
                    Math.Max(.3, (.6 + 1.7 * depth) * dotRadius * (1 + .4 * crest) * RadiusScale),
                    .66 - .56 * depth - .1 * crest, 1);
            }
        }
        return count;
    }

    private static int Solving(ThinkingOrbGeometry.Dot[] dots, double t)
    {
        const double slotDuration = .42, rest = 1.2;
        double cycle = t % (2 * Moves.Length * slotDuration + rest);
        Span<double> amounts = stackalloc double[14];
        amounts.Clear();
        int active = -1;
        if (cycle < 2 * Moves.Length * slotDuration)
        {
            int slot = (int)(cycle / slotDuration);
            double progress = (cycle - slot * slotDuration) / slotDuration;
            double eased = 1 - Math.Pow(1 - Math.Min(1, progress / .7), 3);
            active = slot < Moves.Length ? slot : 2 * Moves.Length - 1 - slot;
            for (int i = 0; i < active; i++) amounts[i] = 1;
            amounts[active] = slot < Moves.Length ? eased : 1 - eased;
        }
        double density = Math.Sqrt(Mix(.088, .35)), dotRadius = Mix(1.9, 1.05);
        int rings = Round(15 * density), longitudes = Round(40 * density), count = 0;
        var projection = new Projection(t * .55, .35 + .1 * Math.Sin(t * .9), ReferenceSize * .41);
        for (int ring = 0; ring <= rings; ring++)
        {
            double latitude = -Math.PI / 2 + ring / (double)rings * Math.PI;
            double cosLat = Math.Cos(latitude), sinLat = Math.Sin(latitude);
            int n = Math.Max(1, Round(Math.Abs(cosLat) * longitudes));
            for (int j = 0; j < n; j++)
            {
                double longitude = j / (double)n * Math.Tau;
                double x = cosLat * Math.Cos(longitude), y = sinLat, z = cosLat * Math.Sin(longitude);
                bool inActive = false;
                for (int i = 0; i < Moves.Length; i++)
                {
                    if (amounts[i] <= 0) continue;
                    var move = Moves[i];
                    double coordinate = move.Axis == 0 ? x : move.Axis == 1 ? y : z;
                    if (coordinate < move.Low || coordinate >= move.High) continue;
                    if (i == active) inActive = true;
                    double angle = move.Angle * amounts[i], cos = Math.Cos(angle), sin = Math.Sin(angle);
                    if (move.Axis == 0) (y, z) = (y * cos - z * sin, y * sin + z * cos);
                    else if (move.Axis == 1) (x, z) = (x * cos + z * sin, -x * sin + z * cos);
                    else (x, y) = (x * cos - y * sin, x * sin + y * cos);
                }
                var p = projection.Project(x, y, z);
                double depth = (p.Z + 1) / 2;
                dots[count++] = new(p.X, p.Y, p.Z,
                    Math.Max(.3, (.6 + 1.7 * depth + (inActive ? .3 : 0)) * dotRadius * RadiusScale),
                    .62 - .54 * depth - (inActive ? .14 : 0), 1);
            }
        }
        return count;
    }

    private static Move[] CreateMoves() => Enumerable.Range(0, 14).Select(i =>
    {
        int axis = Math.Min(2, (int)(Hash(i, 2.3) * 3));
        double low = -1 + .5 * Math.Min(3, (int)(Hash(i, 5.9) * 4));
        return new Move(axis, low, low + .5, (Hash(i, 7.7) < .5 ? 1 : -1) * Math.PI / 2);
    }).ToArray();
}
