using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal interface ICodexAccountUsageReader
{
    Task<AiAccountUsage> ReadAsync(string executable, CancellationToken token);
}

// Read-only RPCs through the installed, official CLI. Credentials remain owned by Codex.
// Never start a thread/turn, read auth.json, or log the child's stdout/stderr.
internal sealed class CodexAccountUsageReader : ICodexAccountUsageReader
{
    public async Task<AiAccountUsage> ReadAsync(string executable, CancellationToken token)
    {
        var start = FindExecutable(executable);
        if (start == null) return new("Codex", AiUsageState.NotInstalled);
        Directory.CreateDirectory(AppPaths.LocalAppDataRoot);
        start.WorkingDirectory = AppPaths.LocalAppDataRoot;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, NetworkPrivacy.Current.Acquire(NetworkFeature.Ai));
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = new Process { StartInfo = start };
        Task? drain = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            process.Start();
            drain = DrainAsync(process.StandardError, timeout.Token);
            var session = ReadSessionAsync(process.StandardOutput, process.StandardInput, DateTimeOffset.UtcNow, timeout.Token);
            // Some synchronous Windows pipe reads cannot be interrupted by their token.
            // Bound the wait independently so finally always terminates the owned child.
            _ = session.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await session.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            timeout.Cancel();
            // Only this read-only child belongs to us. Never terminate a user's CLI/app.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (drain != null)
            {
                try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
        }
    }

    internal static ProcessStartInfo? FindExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // A selected executable is a path, never a command line or shell script.
            return Path.IsPathFullyQualified(configured) && File.Exists(configured) &&
                Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? new(configured) : null;
        }
        var paths = ParseQualifiedSearchPaths(Environment.GetEnvironmentVariable("PATH"));
        foreach (string dir in paths)
        {
            string candidate = Path.Combine(dir, "codex.exe");
            if (File.Exists(candidate)) return new(candidate);
        }
        // The Windows desktop app bundles the official CLI even when it is not on PATH.
        // Query only its known package family for the current user, never arbitrary packages.
        try
        {
            var packages = new global::Windows.Management.Deployment.PackageManager()
                .FindPackagesForUser(string.Empty, "OpenAI.Codex_2p2nqsd0c76g0");
            foreach (var package in packages)
            {
                string candidate = Path.Combine(package.InstalledLocation.Path, "app", "resources", "codex.exe");
                if (File.Exists(candidate)) return new(candidate);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or TypeLoadException) { }
        // npm's .cmd shim must not be executed by a shell. Invoke its official JS entry instead.
        string? node = null;
        foreach (string dir in paths)
        {
            string candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate))
            {
                node = candidate;
                break;
            }
        }
        if (node != null)
        {
            foreach (string dir in paths)
            {
                string script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
                if (!File.Exists(script)) continue;
                var info = new ProcessStartInfo(node);
                info.ArgumentList.Add(script);
                return info;
            }
        }
        return null;
    }

    private static List<string> ParseQualifiedSearchPaths(string? pathEnv)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(pathEnv)) return result;

        ReadOnlySpan<char> remaining = pathEnv.AsSpan();
        while (true)
        {
            int sep = remaining.IndexOf(Path.PathSeparator);
            ReadOnlySpan<char> segment = sep >= 0 ? remaining[..sep] : remaining;
            if (!segment.IsWhiteSpace())
            {
                string trimmed = segment.Trim('"').ToString();
                if (Path.IsPathFullyQualified(trimmed))
                    result.Add(trimmed);
            }
            if (sep < 0) break;
            remaining = remaining[(sep + 1)..];
        }
        return result;
    }

    private static bool ContainsControlChar(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c)) return true;
        }
        return false;
    }

    internal static async Task<AiAccountUsage> ReadSessionAsync(TextReader output, TextWriter input, DateTimeOffset now, CancellationToken token)
    {
        var reader = new BoundedLineReader(output);
        await Call(1, "initialize", new { clientInfo = new { name = "v_notch_usage", title = "V-Notch", version = "1.0" } });
        await input.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), token);
        await input.FlushAsync(token);
        var account = await Call(2, "account/read", new { refreshToken = false });
        if (!account.TryGetProperty("account", out var identity) || identity.ValueKind == JsonValueKind.Null)
            return new("Codex", AiUsageState.SignedOut);
        if (identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("chatgpt" or "chatgptAuthTokens")) return new("Codex", AiUsageState.Unsupported);
        string? plan = identity.TryGetProperty("planType", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        string? email = identity.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (email is { Length: > 254 } || (email != null && ContainsControlChar(email))) email = null;
        var limits = await Call(3, "account/rateLimits/read", new { });
        return AiUsageParsing.Codex(limits, now, plan, email);

        async Task<JsonElement> Call(int id, string method, object parameters)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
            await input.FlushAsync(token);
            for (int count = 0; count < 128; count++)
            {
                string? line = await reader.ReadAsync(token).ConfigureAwait(false);
                if (line == null) throw new IOException("Codex usage connection closed.");
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var responseId)) continue;
                if (root.TryGetProperty("method", out _))
                {
                    // This monitor cannot approve or execute any server-initiated action.
                    await input.WriteLineAsync(JsonSerializer.Serialize(new { id = responseId.Clone(), error = new { code = -32601, message = "Read-only usage client" } }).AsMemory(), token);
                    await input.FlushAsync(token);
                    continue;
                }
                if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out int actual) || actual != id) continue;
                if (root.TryGetProperty("error", out _)) throw new IOException("Codex usage RPC unavailable.");
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new IOException("Invalid Codex usage response.");
                return result.Clone();
            }
            throw new IOException("Codex usage response limit exceeded.");
        }
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        char[] buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
    }

    private sealed class BoundedLineReader(TextReader reader)
    {
        private const int MaxTotalChars = 1024 * 1024;
        private const int MaxLineChars = 256 * 1024;
        private readonly char[] _buffer = new char[4096];
        private int _offset, _count, _total;
        internal async Task<string?> ReadAsync(CancellationToken token)
        {
            StringBuilder? text = null;
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                    _offset = 0;
                    _total += _count;
                    if (_total > MaxTotalChars) throw new IOException("Codex usage response too large.");
                    if (_count == 0) return text is null or { Length: 0 } ? null : text.ToString();
                }

                int avail = _count - _offset;
                int nlPos = Array.IndexOf(_buffer, '\n', _offset, avail);
                if (nlPos >= 0)
                {
                    int nl = nlPos - _offset;
                    int priorLen = text?.Length ?? 0;
                    if (priorLen + nl > MaxLineChars) throw new IOException("Codex usage line too large.");
                    int start = _offset;
                    _offset = nlPos + 1;
                    if (text == null)
                    {
                        int trimLen = nl;
                        while (trimLen > 0 && _buffer[start + trimLen - 1] == '\r') trimLen--;
                        return trimLen == 0 ? string.Empty : new string(_buffer, start, trimLen);
                    }

                    if (nl > 0) text.Append(_buffer, start, nl);
                    int end = text.Length;
                    while (end > 0 && text[end - 1] == '\r') end--;
                    return text.ToString(0, end);
                }

                int nextLen = (text?.Length ?? 0) + avail;
                if (nextLen > MaxLineChars) throw new IOException("Codex usage line too large.");
                text ??= new StringBuilder(avail);
                text.Append(_buffer, _offset, avail);
                _offset = _count;
            }
        }
    }
}
