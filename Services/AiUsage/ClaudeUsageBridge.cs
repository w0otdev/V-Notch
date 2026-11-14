using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VNotch.Services.AiUsage;

internal enum ClaudeBridgeSetup { Installed, AlreadyInstalled, ExistingStatusLine }

internal readonly record struct ClaudeBridgeSetupStatus(
    bool HasSettingsFile,
    bool HasStatusLine,
    bool MatchesBridgeCommand,
    bool HasCustomApiEndpoint);

internal sealed partial class ClaudeUsageBridge
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_claude_bridge_build_command(
        ReadOnlySpan<char> scriptPtr,
        int scriptLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    private readonly string _directory;
    private readonly string _settingsPath;
    internal ClaudeUsageBridge(string? dataDirectory = null, string? settingsPath = null)
    {
        _directory = dataDirectory ?? Path.Combine(AppPaths.LocalAppDataRoot, "ai-usage");
        string configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _settingsPath = settingsPath ?? Path.Combine(configDirectory, "settings.json");
    }

    // Encode the invocation so Git Bash and cmd both pass it unchanged. The path is
    // a PowerShell single-quoted literal; quotes, $, &, and spaces cannot become code.
    internal string Command
    {
        get
        {
            string script = Path.Combine(_directory, "claude-statusline.ps1");
            ReadOnlySpan<char> scriptSpan = script.AsSpan();
            Span<char> stackBuf = stackalloc char[512];
            int rc = vn_claude_bridge_build_command(scriptSpan, scriptSpan.Length, stackBuf, stackBuf.Length, out int written);
            if (rc == 0) return new string(stackBuf[..written]);
            if (rc == -2 && written > 0)
            {
                char[] heapBuf = new char[written];
                if (vn_claude_bridge_build_command(scriptSpan, scriptSpan.Length, heapBuf, heapBuf.Length, out written) == 0)
                    return new string(heapBuf, 0, written);
            }
            return string.Empty;
        }
    }

    internal ClaudeBridgeSetup Install()
    {
        AiUsageFiles.CheckPath(_settingsPath);
        string? original = File.Exists(_settingsPath) ? AiUsageFiles.Read(_settingsPath, 1024 * 1024) : null;
        var settings = ParseSettings(original);
        if (settings["statusLine"] != null)
        {
            if (settings["statusLine"] is not JsonObject line || line["command"]?.GetValue<string>() != Command)
            {
                WriteScript();
                return ClaudeBridgeSetup.ExistingStatusLine;
            }
            WriteScript(); // Repair the owned script after an app upgrade/move.
            return ClaudeBridgeSetup.AlreadyInstalled;
        }
        WriteScript();
        settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command };
        SaveSettings(settings, original);
        return ClaudeBridgeSetup.Installed;
    }

    internal bool Remove()
    {
        if (!File.Exists(_settingsPath)) return false;
        string original = AiUsageFiles.Read(_settingsPath, 1024 * 1024);
        var settings = ParseSettings(original);
        if (settings["statusLine"] is not JsonObject line || line["command"]?.GetValue<string>() != Command) return false;
        settings.Remove("statusLine");
        SaveSettings(settings, original);
        // Keep a custom replacement untouched. The cached numeric sample is safe to forget.
        string data = Path.Combine(_directory, "claude.json");
        AiUsageFiles.CheckPath(data);
        if (File.Exists(data)) File.Delete(data);
        return true;
    }

    private static JsonObject ParseSettings(string? text) => text == null ? new JsonObject() :
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
        ?? throw new IOException("Invalid Claude settings.");

    internal ClaudeBridgeSetupStatus InspectSetupState()
    {
        try
        {
            AiUsageFiles.CheckPath(_settingsPath);
            if (!File.Exists(_settingsPath)) return default;
            var settings = ParseSettings(AiUsageFiles.Read(_settingsPath, 1024 * 1024));
            bool hasStatusLine = settings["statusLine"] != null;
            bool matchesBridge = settings["statusLine"] is JsonObject line &&
                string.Equals(line["command"]?.GetValue<string>(), Command, StringComparison.Ordinal);
            return new(true, hasStatusLine, matchesBridge, HasCustomEndpoint(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    private static bool HasCustomEndpoint(JsonObject settings)
    {
        if (settings["apiKeyHelper"] != null) return true;
        if (settings["env"] is not JsonObject env) return false;
        if (!string.IsNullOrWhiteSpace(env["ANTHROPIC_API_KEY"]?.ToString()) ||
            !string.IsNullOrWhiteSpace(env["ANTHROPIC_AUTH_TOKEN"]?.ToString()))
            return true;
        string? baseUrl = env["ANTHROPIC_BASE_URL"]?.ToString();
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        return !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveSettings(JsonObject settings, string? original)
    {
        AiUsageFiles.CheckPath(_settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        if ((File.Exists(_settingsPath) ? AiUsageFiles.Read(_settingsPath, 1024 * 1024) : null) != original)
            throw new IOException("Claude settings changed during setup. Retry.");
        if (original != null)
            AiUsageFiles.WriteAtomic(_settingsPath + ".v-notch-backup-" + Guid.NewGuid().ToString("N"), original);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            settings.WriteTo(writer);
            writer.Flush();
        }
        AiUsageFiles.WriteAtomic(_settingsPath, Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length) + Environment.NewLine);
    }

    private void WriteScript()
    {
        using var resource = typeof(ClaudeUsageBridge).Assembly.GetManifestResourceStream("VNotch.ClaudeUsageBridge.ps1")
            ?? throw new IOException("Claude usage bridge unavailable.");
        using var reader = new StreamReader(resource);
        AiUsageFiles.WriteAtomic(Path.Combine(_directory, "claude-statusline.ps1"), reader.ReadToEnd());
    }

}
