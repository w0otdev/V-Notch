using System.Data.OleDb;
using System.IO;
using System.Text;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

/// <summary>
/// Queries the Windows Search index (SystemIndex) directly over OLE DB, the
/// way Flow Launcher's Windows Index plugin does. This skips the slow
/// StorageFile materialization of Windows.Storage.Search and widens the scope
/// from the user profile to every indexed location.
/// </summary>
internal sealed class WindowsSearchProvider : ISpotlightProvider
{
    private const int QueryTimeoutMilliseconds = 1500;
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties='Application=Windows'";

    public bool IsAvailable { get; private set; } = true;

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        string sanitizedQuery = SanitizeQuery(query);
        if (sanitizedQuery.Length == 0 || limit <= 0) return Array.Empty<SpotlightSearchItem>();
        limit = Math.Min(limit, 50);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeoutMilliseconds);
        try
        {
            var items = await Task.Run(
                () => ExecuteQuery(sanitizedQuery, limit, timeoutCts.Token), timeoutCts.Token)
                .ConfigureAwait(false);
            IsAvailable = true;

            return items
                .Select(item => item with { Score = SpotlightRanker.Score(item, query) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            IsAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }
        catch
        {
            IsAvailable = false;
            throw;
        }
    }

    private static List<SpotlightSearchItem> ExecuteQuery(
        string sanitizedQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        int fetch = Math.Clamp(limit * 4, 20, 100);
        string sql =
            $"SELECT TOP {fetch} System.ItemNameDisplay, System.ItemPathDisplay, System.ItemType " +
            "FROM SystemIndex " +
            $"WHERE SCOPE='file:' AND ({BuildNamePredicate(sanitizedQuery)}) " +
            "ORDER BY System.Search.Rank DESC";

        var results = new List<SpotlightSearchItem>(fetch);
        using var connection = new OleDbConnection(ConnectionString);
        connection.Open();
        using var command = new OleDbCommand(sql, connection)
        {
            CommandTimeout = Math.Max(1, QueryTimeoutMilliseconds / 1000)
        };
        using var reader = command.ExecuteReader();
        while (results.Count < fetch && reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;

            string name = reader.GetString(0);
            string path = reader.GetString(1);
            if (name.Length == 0 || path.Length == 0) continue;

            bool isFolder = !reader.IsDBNull(2) &&
                string.Equals(reader.GetString(2), "Directory", StringComparison.OrdinalIgnoreCase);
            results.Add(new SpotlightSearchItem(
                $"{(isFolder ? "folder" : "file")}:{path}",
                isFolder ? SpotlightResultKind.Folder : SpotlightResultKind.File,
                Path.GetFileName(path) is { Length: > 0 } fileName ? fileName : name,
                path,
                path,
                path));
        }

        return results;
    }

    /// <summary>
    /// Word-prefix match through the full-text index (fast) plus a substring
    /// LIKE so mid-name hits such as "port" in "report.pdf" still appear.
    /// </summary>
    private static string BuildNamePredicate(string sanitizedQuery)
    {
        string like = EscapeLikePattern(sanitizedQuery);
        string[] terms = sanitizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string contains = string.Join(" AND ", terms.Select(term => $"\"{term}*\""));
        return $"System.FileName LIKE '%{like}%' OR CONTAINS(System.FileName, '{contains}')";
    }

    /// <summary>
    /// SanitizeQuery already strips quote/paren/star characters that break
    /// Windows Search SQL; this additionally escapes LIKE wildcards.
    /// </summary>
    private static string EscapeLikePattern(string sanitizedQuery)
    {
        var builder = new StringBuilder(sanitizedQuery.Length);
        foreach (char character in sanitizedQuery)
        {
            builder.Append(character switch
            {
                '%' => "[%]",
                '_' => "[_]",
                '[' => "[[]",
                _ => character.ToString()
            });
        }
        return builder.ToString();
    }

    internal static string SanitizeQuery(string query) =>
        new string(query.Where(character => character is not ('"' or '\'' or '\\' or '(' or ')' or ':' or '*'))
            .Take(256).ToArray()).Trim();
}
