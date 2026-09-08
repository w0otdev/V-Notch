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
            [0] = MigrateV0,
            [1] = MigrateV1,
            [2] = MigrateV2,
            [3] = MigrateV3,
            [4] = MigrateV4,
            [5] = MigrateV5,
            [6] = MigrateV6,
            [7] = MigrateV7,
            [8] = static root => root,
            [9] = MigrateV9,
            [10] = MigrateV10,
            [11] = MigrateV11,
            [12] = MigrateV12,
        };

    private static void EnsureProperty(JsonObject root, string propertyName, JsonNode? defaultValue)
    {
        if (!root.ContainsKey(propertyName))
        {
            root[propertyName] = defaultValue;
        }
    }

    private static JsonObject MigrateV0(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.NotchStyle), "default");
        EnsureProperty(root, nameof(NotchSettings.HoverZoneMargin), 60);
        return root;
    }

    private static JsonObject MigrateV1(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.IsShelfUploadLimitUnlocked), false);
        return root;
    }

    private static JsonObject MigrateV2(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.MediaBlurBrightnessBoost), 1.4);
        return root;
    }

    private static JsonObject MigrateV3(JsonObject root)
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
    }

    private static JsonObject MigrateV4(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.EnableBlurEffects), true);
        EnsureProperty(root, nameof(NotchSettings.AnimationFps), AnimationConfig.MaxFps);
        return root;
    }

    private static JsonObject MigrateV5(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.DynamicIslandHeight), 40);
        return root;
    }

    private static JsonObject MigrateV6(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.ReopenLastViewOnExpand), false);
        return root;
    }

    private static JsonObject MigrateV7(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.EnableWeather), false);
        EnsureProperty(root, nameof(NotchSettings.ManualCity), string.Empty);
        return root;
    }

    private static JsonObject MigrateV9(JsonObject root)
    {
        // Canvas no longer uses the hosted PaxSenix API. Remove the old
        // credential entirely when upgrading to the Spotify login flow.
        root.Remove("PaxSenixApiKey");
        return root;
    }

    private static JsonObject MigrateV10(JsonObject root)
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
    }

    private static JsonObject MigrateV11(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.EnableSpotlight), true);
        return root;
    }

    private static JsonObject MigrateV12(JsonObject root)
    {
        EnsureProperty(root, nameof(NotchSettings.EnableLocalOnlyMode), false);
        EnsureProperty(root, nameof(NotchSettings.AutoCheckUpdates), true);
        EnsureProperty(root, nameof(NotchSettings.EnableOnlineArtworkLookup), true);
        EnsureProperty(root, nameof(NotchSettings.EnableOnlineLyrics), true);
        EnsureProperty(root, nameof(NotchSettings.EnableBrowserUrlInspection), true);
        EnsureProperty(root, nameof(NotchSettings.EnablePrivacyIndicators), true);
        EnsureProperty(root, nameof(NotchSettings.EnableDiagnosticLogging), true);
        EnsureProperty(root, nameof(NotchSettings.EnableSpotlightHistory), true);
        return root;
    }

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
