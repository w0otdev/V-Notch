using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed class SystemCommandProvider : ISpotlightProvider
{
    public bool IsAvailable => true;
    public bool IsInstant => true;

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        string normalized = SettingsSearchMatcher.Normalize(query);
        var results = new List<SpotlightSearchItem>();
        if (normalized.Length == 0 || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results);

        foreach (var entry in SpotlightSystemCatalog.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = entry.CreateItem();
            IEnumerable<string> terms = entry.Keywords.Split(';').Append(item.Title);
            if (entry.Kind == SpotlightResultKind.Application)
                terms = terms.Append(System.IO.Path.GetFileName(entry.Target));
            double score = terms
                .Select(term => Score(SettingsSearchMatcher.Normalize(term), normalized)).Max();
            if (score > 0 && SpotlightLauncher.IsValidTarget(item))
                results.Add(item with { Score = score });
        }
        return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit).ToArray());
    }

    private static double Score(string term, string query)
    {
        if (term == query) return 1450;
        if (term.StartsWith(query, StringComparison.Ordinal)) return 1250 - Math.Min(100, term.Length - query.Length);
        if (term.Split(' ').Contains(query)) return 1100;
        if (SettingsSearchMatcher.GetNormalizedMatchScore(term, query, allowFuzzy: false) > 0) return 850;
        return 0;
    }
}
