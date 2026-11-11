using System;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;
using System.IO;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct SearchResult { public nint Name, Path; public int Folder; }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_execute(string sql, SafeWaitHandle cancel,
        Span<SearchResult> rows, int capacity, out int count);

    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string> CachedDefaultWindowsScope =
        new(() => SpotlightFileVisibility.BuildWindowsScope(string.Empty));

    private volatile bool _isAvailable = true;
    private long _lastFailureTicks = DateTime.MinValue.Ticks;
    private int _loggedUnavailable;

    private const int MaxQueryLength = 256;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_sanitize_query(
        ReadOnlySpan<char> queryPtr,
        int queryLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_build_query_sql(
        ReadOnlySpan<char> sanitizedPtr,
        int sanitizedLen,
        ReadOnlySpan<char> originalPtr,
        int originalLen,
        ReadOnlySpan<char> scopePtr,
        int scopeLen,
        byte isExplicitPath,
        int fetch,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_windows_search_normalize_result_path(
        ReadOnlySpan<char> rawPathPtr,
        int rawPathLen,
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
        query = ClampQuery(query);
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

            if (items.Count == 0) return Array.Empty<SpotlightSearchItem>();

            var matches = new List<SpotlightSearchItem>(items.Count);
            var isExcluded = IsExcluded;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (isExcluded?.Invoke(item.Target) == true) continue;
                if (!SpotlightFileVisibility.ShouldInclude(item, query)) continue;
                double score = SpotlightRanker.Score(item, query);
                if (score <= 0) continue;
                matches.Add(item with { Score = score });
            }

            if (matches.Count == 0) return Array.Empty<SpotlightSearchItem>();
            matches.Sort(SpotlightSearchService.s_scoreAndTitleComparison);
            int resultCount = Math.Min(limit, matches.Count);
            var results = new SpotlightSearchItem[resultCount];
            matches.CopyTo(0, results, 0, resultCount);
            return results;
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
        catch (COMException ex)
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

    private static string ClampQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        ReadOnlySpan<char> trimmed = query.AsSpan().Trim();
        if (trimmed.Length <= MaxQueryLength)
            return trimmed.Length == query.Length ? query : trimmed.ToString();
        int len = MaxQueryLength;
        if (char.IsHighSurrogate(trimmed[len - 1])) len--;
        return trimmed[..len].ToString();
    }

    private static ReadOnlySpan<char> ClampSpan(ReadOnlySpan<char> span, int maxLen)
    {
        if (span.Length <= maxLen) return span;
        int len = maxLen;
        if (len > 0 && char.IsHighSurrogate(span[len - 1])) len--;
        return span[..len];
    }

    internal static string BuildQuerySql(string sanitizedQuery, string originalQuery, int fetch)
    {
        string clampedOriginal = ClampQuery(originalQuery);
        bool isExplicitPath = SpotlightFileVisibility.IsExplicitPath(clampedOriginal);
        string scope = isExplicitPath
            ? SpotlightFileVisibility.BuildWindowsScope(clampedOriginal)
            : CachedDefaultWindowsScope.Value;

        ReadOnlySpan<char> sanitizedSpan = ClampSpan((sanitizedQuery ?? string.Empty).AsSpan(), MaxQueryLength);
        ReadOnlySpan<char> originalSpan = ClampSpan(clampedOriginal.AsSpan(), MaxQueryLength);
        ReadOnlySpan<char> scopeSpan = ClampSpan((scope ?? string.Empty).AsSpan(), 4097);

        int cap = checked(sanitizedSpan.Length * 4 + originalSpan.Length * 3 + scopeSpan.Length + 256);
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        int rc = vn_windows_search_build_query_sql(
            sanitizedSpan, sanitizedSpan.Length,
            originalSpan, originalSpan.Length,
            scopeSpan, scopeSpan.Length,
            isExplicitPath ? (byte)1 : (byte)0,
            fetch,
            buf, buf.Length,
            out int len);
        if (rc == -2 && len > buf.Length)
        {
            char[] larger = new char[len];
            rc = vn_windows_search_build_query_sql(
                sanitizedSpan, sanitizedSpan.Length,
                originalSpan, originalSpan.Length,
                scopeSpan, scopeSpan.Length,
                isExplicitPath ? (byte)1 : (byte)0,
                fetch,
                larger, larger.Length,
                out len);
            return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty;
        }
        return rc == 0 && len > 0 ? new string(buf[..len]) : string.Empty;
    }

    internal static string NormalizeResultPath(string rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return string.Empty;
        ReadOnlySpan<char> span = rawPath.AsSpan();
        int cap = span.Length + 8;
        Span<char> buf = cap <= 520 ? stackalloc char[cap] : new char[cap];
        int rc = vn_windows_search_normalize_result_path(span, span.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length)
        {
            char[] larger = new char[len];
            rc = vn_windows_search_normalize_result_path(span, span.Length, larger, larger.Length, out len);
            return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty;
        }
        return rc == 0 && len > 0 ? new string(buf[..len]) : string.Empty;
    }

    private static List<SpotlightSearchItem> ExecuteQuery(
        string sanitizedQuery,
        string originalQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        int fetch = Math.Clamp(limit * 4, 20, 100);
        string sql = BuildQuerySql(sanitizedQuery, originalQuery, fetch);
        if (sql.Length == 0) return new List<SpotlightSearchItem>();

        var results = new List<SpotlightSearchItem>(fetch);
        Span<SearchResult> rows = stackalloc SearchResult[fetch];
        int count = 0;
        try
        {
            int hr = vn_windows_search_execute(sql, cancellationToken.WaitHandle.SafeWaitHandle, rows, fetch, out count);
            cancellationToken.ThrowIfCancellationRequested();
            if (hr < 0) throw new COMException(Marshal.GetExceptionForHR(hr)?.Message, hr);
            foreach (var row in rows[..count])
            {
                var item = ReadSearchItem(Marshal.PtrToStringBSTR(row.Name),
                    NormalizeResultPath(Marshal.PtrToStringBSTR(row.Path)), row.Folder != 0);
                if (item != null) results.Add(item);
            }
            return results;
        }
        finally
        {
            foreach (var row in rows[..count])
            {
                Marshal.FreeBSTR(row.Name);
                Marshal.FreeBSTR(row.Path);
            }
        }
    }

    private static SpotlightSearchItem? ReadSearchItem(string name, string path, bool isFolder)
    {
        if (name.Length == 0 || path.Length == 0) return null;
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
