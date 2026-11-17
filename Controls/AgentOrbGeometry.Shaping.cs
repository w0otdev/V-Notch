// Evenly resampled circle -> triangle -> square, thinking-orbs (MIT).
namespace VNotch.Controls;

internal static partial class AgentOrbGeometry
{
    private static readonly (double X, double Y)[] Triangle = [(0, -.26), (.24, .16), (-.24, .16)];
    private static readonly (double X, double Y)[] Square = [(0, -.2), (.2, -.2), (.2, .2), (-.2, .2), (-.2, -.2)];

    private static int Shaping(ThinkingOrbGeometry.Dot[] dots, double t)
    {
        const double hold = 1.4, morph = .9, segment = hold + morph, spread = 1.45;
        double cycle = t % (segment * 3);
        int shape = (int)(cycle / segment);
        double local = cycle - shape * segment;
        double progress = local > hold ? (local - hold) / morph : 0;
        double blend = progress * progress * (3 - 2 * progress);
        const int samples = 160;
        Span<(double X, double Y)> points = stackalloc (double, double)[samples];
        Span<double> lengths = stackalloc double[samples];
        for (int i = 0; i < samples; i++)
        {
            var a = ShapePoint(shape, i / (double)samples);
            var b = ShapePoint((shape + 1) % 3, i / (double)samples);
            points[i] = ((a.X + (b.X - a.X) * blend) * spread, (a.Y + (b.Y - a.Y) * blend) * spread);
        }
        double total = 0;
        for (int i = 0; i < samples; i++)
        {
            var a = points[i]; var b = points[(i + 1) % samples];
            lengths[i] = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            total += lengths[i];
        }
        // Details uses .54 for its large shaping count (upstream React uses .702).
        int count = Math.Max(6, Round(34 * Mix(.53, .54))), index = 0;
        double accumulated = 0, pulse = 1 + .02 * Math.Sin(local * 3.1);
        double radius = Math.Max(.35, .021 * Mix(1.011, .395) * 1.35 * spread * ReferenceSize);
        for (int i = 0; i < count; i++)
        {
            double target = i / (double)count * total;
            while (accumulated + lengths[index] < target && index < samples - 1) accumulated += lengths[index++];
            var a = points[index]; var b = points[(index + 1) % samples];
            double f = lengths[index] > 0 ? Math.Min(1, (target - accumulated) / lengths[index]) : 0;
            dots[i] = new(ReferenceSize / 2 + (a.X + (b.X - a.X) * f) * pulse * ReferenceSize,
                ReferenceSize / 2 + (a.Y + (b.Y - a.Y) * f) * pulse * ReferenceSize, 0, radius, .1, 1);
        }
        return count;
    }

    private static (double X, double Y) ShapePoint(int shape, double fraction)
    {
        if (shape == 0)
        {
            double angle = -Math.PI / 2 + fraction * Math.Tau;
            return (Math.Cos(angle) * .24, Math.Sin(angle) * .24);
        }
        var vertices = shape == 1 ? Triangle : Square;
        Span<double> lengths = stackalloc double[5];
        double total = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Length];
            lengths[i] = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            total += lengths[i];
        }
        double target = fraction * total;
        int index = 0;
        while (target > lengths[index] && index < vertices.Length - 1) target -= lengths[index++];
        var from = vertices[index]; var to = vertices[(index + 1) % vertices.Length];
        double f = lengths[index] > 0 ? Math.Min(1, target / lengths[index]) : 0;
        return (from.X + (to.X - from.X) * f, from.Y + (to.Y - from.Y) * f);
    }
}
