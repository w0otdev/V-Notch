using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services.Spotlight.Providers;

internal sealed partial class WindowsSearchProvider : ISpotlightProvider
{
    private const string NativeDll = "VNotch.Native.dll";

    internal Func<string, bool>? IsExcluded { get; set; }

    private const int QueryTimeoutMilliseconds = 1500;
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties='Application=Windows'";
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string> CachedDefaultWindowsScope =
        new(() => SpotlightFileVisibility.BuildWindowsScope(string.Empty));

    private volatile bool _isAvailable = true;
    private long _lastFailureTicks = DateTime.MinValue.Ticks;
    private int _loggedUnavailable;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_sanitize_query(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_escape_like_pattern(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_build_name_predicate(
        ReadOnlySpan<char> sanitizedPtr,
        int sanitizedLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    public bool IsAvailable
    {
        get
        {
            if (_isAvailable) return true;
            long lastFailureTicks = Interlocked.Read(ref _lastFailureTicks);
            if (DateTime.UtcNow.Ticks - lastFailureTicks > RetryCooldown.Ticks)
            {
                return true;
            }
            return false;
        }
    }

    public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || !SpotlightFileVisibility.ShouldSearch(query)) return Array.Empty<SpotlightSearchItem>();

        string sanitizedQuery = SanitizeQuery(query);
        if (sanitizedQuery.Length == 0 || limit <= 0) return Array.Empty<SpotlightSearchItem>();
        limit = Math.Min(limit, 50);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeoutMilliseconds);
        try
        {
            var items = await Task.Run(
                () => ExecuteQuery(sanitizedQuery, query, limit, timeoutCts.Token), timeoutCts.Token)
                .ConfigureAwait(false);
            _isAvailable = true;
            Interlocked.Exchange(ref _loggedUnavailable, 0);

            return items
                .Where(item => IsExcluded?.Invoke(item.Target) != true)
                .Where(item => SpotlightFileVisibility.ShouldInclude(item, query))
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
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            _isAvailable = false;
            return Array.Empty<SpotlightSearchItem>();
        }
        catch (OleDbException ex)
        {
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            _isAvailable = false;
            if (Interlocked.Exchange(ref _loggedUnavailable, 1) == 0)
            {
                bool isStopped = (uint)ex.ErrorCode == 0x80041820 || (uint)ex.HResult == 0x80041820;
                string detail = isStopped
                    ? "Windows Search service (WSearch) is stopped or not running (0x80041820)."
                    : ex.Message;
                RuntimeLog.Log("SPOTLIGHT-SEARCH", $"WindowsSearchProvider disabled: {detail}");
            }
            return Array.Empty<SpotlightSearchItem>();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            _isAvailable = false;
            if (Interlocked.Exchange(ref _loggedUnavailable, 1) == 0)
            {
                RuntimeLog.Log("SPOTLIGHT-SEARCH", $"WindowsSearchProvider error: {ex.Message}");
            }
            return Array.Empty<SpotlightSearchItem>();
        }
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".msc", ".cpl", ".com", ".ps1", ".appref-ms", ".lnk"
    };

    internal static string BuildQuerySql(string sanitizedQuery, string originalQuery, int fetch)
    {
        bool isExplicitPath = SpotlightFileVisibility.IsExplicitPath(originalQuery);
        string predicate = isExplicitPath
            ? "System.ItemPathDisplay LIKE '" + EscapeLikePattern(originalQuery.Trim().Trim('"')
                .Replace('/', '\\').Replace("'", "''")) + "%'"
            : BuildNamePredicate(sanitizedQuery);
        string scope = isExplicitPath
            ? SpotlightFileVisibility.BuildWindowsScope(originalQuery)
            : CachedDefaultWindowsScope.Value;
        return
            $"SELECT TOP {fetch} System.ItemNameDisplay, System.ItemPathDisplay, System.ItemType " +
            "FROM SystemIndex " +
            $"WHERE SCOPE='file:' AND ({predicate}) " +
            scope + " " +
            "ORDER BY System.Search.Rank DESC";
    }

    private static List<SpotlightSearchItem> ExecuteQuery(
        string sanitizedQuery,
        string originalQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        int fetch = Math.Clamp(limit * 4, 20, 100);
        string sql = BuildQuerySql(sanitizedQuery, originalQuery, fetch);

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
            var item = ReadSearchItem(reader);
            if (item != null)
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static SpotlightSearchItem? ReadSearchItem(OleDbDataReader reader)
    {
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;

        string name = reader.GetString(0);
        string path = reader.GetString(1);
        if (name.Length == 0 || path.Length == 0) return null;

        bool isFolder = !reader.IsDBNull(2) &&
            string.Equals(reader.GetString(2), "Directory", StringComparison.OrdinalIgnoreCase);
        string ext = Path.GetExtension(path);
        bool isExec = !isFolder && ExecutableExtensions.Contains(ext);

        SpotlightResultKind kind;
        if (isFolder)
        {
            kind = SpotlightResultKind.Folder;
        }
        else if (isExec)
        {
            kind = SpotlightResultKind.Application;
        }
        else
        {
            kind = SpotlightResultKind.File;
        }

        string typePrefix = kind switch
        {
            SpotlightResultKind.Folder => "folder",
            SpotlightResultKind.Application => "app",
            _ => "file"
        };

        string title = Path.GetFileName(path) is { Length: > 0 } fileName ? fileName : name;

        return new SpotlightSearchItem(
            $"{typePrefix}:{path}",
            kind,
            title,
            path,
            path,
            path);
    }

    private static string BuildNamePredicate(string sanitizedQuery)
    {
        ReadOnlySpan<char> span = (sanitizedQuery ?? string.Empty).AsSpan();
        int cap = span.Length * 4 + 128;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        int rc = vn_windows_search_build_name_predicate(span, span.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length) { char[] larger = new char[len]; rc = vn_windows_search_build_name_predicate(span, span.Length, larger, larger.Length, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty; }
        if (rc != 0) len = 0;
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    private static string EscapeLikePattern(string sanitizedQuery)
    {
        ReadOnlySpan<char> span = (sanitizedQuery ?? string.Empty).AsSpan();
        int cap = span.Length * 3 + 16;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        int rc = vn_windows_search_escape_like_pattern(span, span.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length) { char[] larger = new char[len]; rc = vn_windows_search_escape_like_pattern(span, span.Length, larger, larger.Length, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty; }
        if (rc != 0) len = 0;
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    internal static string SanitizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        ReadOnlySpan<char> span = query.AsSpan();
        Span<char> buf = stackalloc char[260];
        int rc = vn_windows_search_sanitize_query(span, span.Length, buf, buf.Length, out int len);
        if (rc != 0) len = 0;
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }
}
