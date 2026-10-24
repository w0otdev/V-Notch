using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class SystemCommandProvider : ISpotlightProvider
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_system_command_provider_score_entry(
        ReadOnlySpan<char> keywordsPtr, int keywordsLen,
        ReadOnlySpan<char> titlePtr, int titleLen,
        ReadOnlySpan<char> fileNamePtr, int fileNameLen,
        ReadOnlySpan<char> normQueryPtr, int normQueryLen,
        out double outScore);

    public bool IsAvailable => true;
    public bool IsInstant => true;

    public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        string normalized = SettingsSearchMatcher.Normalize(query);
        var results = new List<SpotlightSearchItem>();
        if (normalized.Length == 0 || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results);

        ReadOnlySpan<char> normSpan = normalized.AsSpan();
        foreach (var entry in SpotlightSystemCatalog.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = entry.CreateItem();
            ReadOnlySpan<char> fileName = entry.Kind == SpotlightResultKind.Application
                ? System.IO.Path.GetFileName(entry.Target.AsSpan())
                : ReadOnlySpan<char>.Empty;

            _ = vn_system_command_provider_score_entry(
                entry.Keywords.AsSpan(), entry.Keywords.Length,
                item.Title.AsSpan(), item.Title.Length,
                fileName, fileName.Length,
                normSpan, normSpan.Length,
                out double score);

            if (score > 0 && SpotlightLauncher.IsValidTarget(item))
                results.Add(item with { Score = score });
        }
        return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit).ToArray());
    }
}
