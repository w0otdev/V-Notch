namespace VNotch.Services.Translation;

// M2M100's published generation config uses five beams and length penalty 1.
internal sealed class TranslationBeamSearch(uint start, int width = 5)
{
    private sealed record Path(uint[] Tokens, double Score);
    private List<Path> _active = [new([start], 0)];
    private readonly List<Path> _finished = [];
    internal uint[] LastTokens => _active.Select(path => path.Tokens[^1]).ToArray();
    internal bool IsDone { get; private set; }
    internal uint[] Result => _finished.OrderByDescending(p => p.Score / p.Tokens.Length).First().Tokens;

    internal int[] Advance(float[] logits, int vocabulary)
    {
        var candidates = new List<(double Score, int Parent, uint Token)>();
        for (int row = 0; row < _active.Count; row++)
        {
            int offset = row * vocabulary;
            double maximum = double.NegativeInfinity;
            for (int token = 0; token < vocabulary; token++) maximum = Math.Max(maximum, logits[offset + token]);
            double sum = 0;
            for (int token = 0; token < vocabulary; token++) sum += Math.Exp(logits[offset + token] - maximum);
            double normalizer = maximum + Math.Log(sum);
            // Keep only the globally best 2*width candidates without sorting 128k logits.
            for (uint token = 0; token < vocabulary; token++)
            {
                if (token == 1) continue; // padding must never be generated
                double score = _active[row].Score + logits[offset + (int)token] - normalizer;
                if (candidates.Count == width * 2 && score <= candidates[^1].Score) continue;
                int position = candidates.FindIndex(candidate => score > candidate.Score);
                candidates.Insert(position < 0 ? candidates.Count : position, (score, row, token));
                if (candidates.Count > width * 2) candidates.RemoveAt(candidates.Count - 1);
            }
        }
        var next = new List<Path>();
        var parents = new List<int>();
        if (width == 1)
        {
            var best = candidates[0];
            var previous = _active[best.Parent];
            IsDone = best.Token == 2;
            if (IsDone) { _finished.Add(new(previous.Tokens, best.Score)); return []; }
            _active = [new([..previous.Tokens, best.Token], best.Score)];
            return [best.Parent];
        }
        for (int rank = 0; rank < candidates.Count; rank++)
        {
            var candidate = candidates[rank];
            var old = _active[candidate.Parent];
            if (candidate.Token == 2)
            {
                if (rank < width) _finished.Add(new(old.Tokens, candidate.Score));
                continue;
            }
            if (next.Count == width) continue;
            next.Add(new([..old.Tokens, candidate.Token], candidate.Score));
            parents.Add(candidate.Parent);
        }
        _finished.Sort((a, b) => (b.Score / b.Tokens.Length).CompareTo(a.Score / a.Tokens.Length));
        if (_finished.Count > width) _finished.RemoveRange(width, _finished.Count - width);
        _active = next;
        IsDone = next.Count == 0 || (_finished.Count == width && next.Max(p => p.Score / p.Tokens.Length) <= _finished[^1].Score / _finished[^1].Tokens.Length);
        return parents.ToArray();
    }
}
