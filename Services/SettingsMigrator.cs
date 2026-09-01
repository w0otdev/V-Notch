using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using VNotch.Models;

namespace VNotch.Services;

public static class SettingsMigrator
{

    public const int CurrentVersion = 13;

    private static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> _migrations =
        new Dictionary<int, Func<JsonObject, JsonObject>>
        {

            [0] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.NotchStyle)))
                {
                    root[nameof(NotchSettings.NotchStyle)] = "default";
                }
                if (!root.ContainsKey(nameof(NotchSettings.HoverZoneMargin)))
                {
                    root[nameof(NotchSettings.HoverZoneMargin)] = 60;
                }
                return root;
            },
            [1] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.IsShelfUploadLimitUnlocked)))
                {
                    root[nameof(NotchSettings.IsShelfUploadLimitUnlocked)] = false;
                }
                return root;
            },
            [2] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.MediaBlurBrightnessBoost)))
                {
                    root[nameof(NotchSettings.MediaBlurBrightnessBoost)] = 1.4;
                }
                return root;
            },
            [3] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.DynamicIslandWidth)))
                {
                    int width = 230;
                    if (root.TryGetPropertyValue(nameof(NotchSettings.Width), out var widthNode)
                        && widthNode is JsonValue widthValue
                        && widthValue.TryGetValue(out int parsedWidth))
                    {
                        width = parsedWidth;
                    }

                    root[nameof(NotchSettings.DynamicIslandWidth)] = (int)Math.Round(width * 1.12 / 10.0) * 10;
                }
                return root;
            },
            [4] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableBlurEffects)))
                {
                    root[nameof(NotchSettings.EnableBlurEffects)] = true;
                }
                if (!root.ContainsKey(nameof(NotchSettings.AnimationFps)))
                {
                    root[nameof(NotchSettings.AnimationFps)] = AnimationConfig.MaxFps;
                }
                return root;
            },
            [5] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.DynamicIslandHeight)))
                {
                    root[nameof(NotchSettings.DynamicIslandHeight)] = 40;
                }
                return root;
            },
            [6] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.ReopenLastViewOnExpand)))
                {
                    root[nameof(NotchSettings.ReopenLastViewOnExpand)] = false;
                }
                return root;
            },
            [7] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableWeather)))
                {
                    root[nameof(NotchSettings.EnableWeather)] = false;
                }
                if (!root.ContainsKey(nameof(NotchSettings.ManualCity)))
                {
                    root[nameof(NotchSettings.ManualCity)] = string.Empty;
                }
                return root;
            },
            [8] = root => root,
            [9] = root =>
            {
                // Canvas no longer uses the hosted PaxSenix API. Remove the old
                // credential entirely when upgrading to the Spotify login flow.
                root.Remove("PaxSenixApiKey");
                return root;
            },
            [10] = root =>
            {
                int animationFps = AnimationConfig.MaxFps;
                if (root.TryGetPropertyValue(nameof(NotchSettings.AnimationFps), out var fpsNode)
                    && fpsNode is JsonValue fpsValue
                    && fpsValue.TryGetValue(out int parsedFps))
                {
                    animationFps = Math.Clamp(parsedFps, AnimationConfig.MinFps, AnimationConfig.MaxFps);
                }

                root[nameof(NotchSettings.AnimationFps)] = animationFps;
                return root;
            },
            [11] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableSpotlight)))
                {
                    root[nameof(NotchSettings.EnableSpotlight)] = true;
                }
                return root;
            },
            [12] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableLocalOnlyMode)))
                    root[nameof(NotchSettings.EnableLocalOnlyMode)] = false;
                if (!root.ContainsKey(nameof(NotchSettings.AutoCheckUpdates)))
                    root[nameof(NotchSettings.AutoCheckUpdates)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnableOnlineArtworkLookup)))
                    root[nameof(NotchSettings.EnableOnlineArtworkLookup)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnableOnlineLyrics)))
                    root[nameof(NotchSettings.EnableOnlineLyrics)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnableBrowserUrlInspection)))
                    root[nameof(NotchSettings.EnableBrowserUrlInspection)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnablePrivacyIndicators)))
                    root[nameof(NotchSettings.EnablePrivacyIndicators)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnableDiagnosticLogging)))
                    root[nameof(NotchSettings.EnableDiagnosticLogging)] = true;
                if (!root.ContainsKey(nameof(NotchSettings.EnableSpotlightHistory)))
                    root[nameof(NotchSettings.EnableSpotlightHistory)] = true;
                return root;
            },
        };

    public static (NotchSettings settings, bool migrated) Migrate(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new JsonException("Settings JSON is null or empty");

        var node = JsonNode.Parse(rawJson)
                   ?? throw new JsonException("Settings JSON parsed to null root");

        if (node is not JsonObject root)
        {
            throw new JsonException("Settings JSON root must be an object");
        }

        int startVersion = 0;
        if (root.TryGetPropertyValue(nameof(NotchSettings.SettingsVersion), out var versionNode)
            && versionNode is JsonValue vv
            && vv.TryGetValue(out int parsed))
        {
            startVersion = parsed;
        }

        int currentVersion = startVersion;
        bool migrated = false;

        while (currentVersion < CurrentVersion)
        {
            if (_migrations.TryGetValue(currentVersion, out var step))
            {
                root = step(root);
                migrated = true;
            }

            currentVersion++;
        }

        root[nameof(NotchSettings.SettingsVersion)] = CurrentVersion;

        // Version 9 introduced key protection, but a settings file can already
        // report that version while still containing a legacy plaintext value.
        // Therefore inspect every loaded file, not just a particular migration
        // step. Protect intentionally throws: callers must retain the old file
        // rather than clear or rewrite a key that could not be protected.
        foreach (string keyName in new[]
                 {
                     nameof(NotchSettings.YouTubeApiKey),
                     nameof(NotchSettings.SpotifySpDc),
                 })
        {
            if (root.TryGetPropertyValue(keyName, out var keyNode)
                && keyNode is JsonValue keyValue
                && keyValue.TryGetValue<string>(out var key)
                && !string.IsNullOrEmpty(key)
                && !DataProtection.IsEncrypted(key))
            {
                root[keyName] = DataProtection.Protect(key);
                migrated = true;
            }
        }

        var normalizedJson = root.ToJsonString();
        var settings = JsonSerializer.Deserialize<NotchSettings>(normalizedJson) ?? new NotchSettings();
        settings.SettingsVersion = CurrentVersion;

        if (startVersion != CurrentVersion)
        {
            migrated = true;
        }

        return (settings, migrated);
    }
}
