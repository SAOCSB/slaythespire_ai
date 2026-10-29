using System.Text.Json;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;

namespace SpirePilot.Bench;

/// <summary>
/// Builds the <see cref="PilotSettings"/> each fixture runs with: the LLM profile from the user's file, defaults (or the
/// file's own settings with --keep-settings) for everything else, then the fixture's overrides on top.
/// </summary>
public static class BenchSettings
{
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Loads a SpirePilot settings.json or a single profile JSON (an object with provider / baseUrl / model / apiKey).
    /// <paramref name="profile"/> selects a profile by name; <paramref name="keepOwnerSettings"/> keeps the file's
    /// route / strategy / team settings instead of the defaults (results then depend on your preferences).
    /// </summary>
    public static PilotSettings Load(string path, string? profile = null, bool keepOwnerSettings = false)
    {
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocOptions) as JsonObject
                   ?? throw new JsonException("expected a JSON object");
        PilotSettings loaded;
        if (IsProfile(node))
        {
            var p = node.Deserialize<LlmProfile>(SettingsStore.JsonOptions) ?? throw new JsonException("profile is empty");
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name == "default") p.Name = Path.GetFileNameWithoutExtension(path);
            loaded = new PilotSettings { Profiles = new List<LlmProfile> { p }, ActiveProfile = p.Name };
        }
        else
        {
            loaded = node.Deserialize<PilotSettings>(SettingsStore.JsonOptions) ?? throw new JsonException("settings are empty");
        }
        loaded.Normalize();

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var match = loaded.Profiles.FirstOrDefault(x => x.Name.Equals(profile.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"no profile named \"{profile}\" (available: {string.Join(", ", loaded.Profiles.Select(x => x.Name))})");
            loaded.ActiveProfile = match.Name;
        }
        if (keepOwnerSettings) return loaded;

        var clean = new PilotSettings { Profiles = loaded.Profiles, ActiveProfile = loaded.ActiveProfile };
        clean.Normalize();
        return clean;
    }

    /// <summary>
    /// For --dry-run: profiles without a usable key get a placeholder so the engine's profile check passes. The key is
    /// never sent anywhere (the dry-run client makes no requests).
    /// </summary>
    public static PilotSettings ForDryRun(PilotSettings? settings)
    {
        var s = settings == null ? new PilotSettings() : SettingsStore.Clone(settings);
        foreach (var p in s.Profiles)
            if (LlmClientFactory.Validate(p).Contains("api_key")) p.ApiKey = "dry-run-placeholder";
        s.Normalize();
        return s;
    }

    /// <summary>Base settings + the fixture's overrides, with the benchmark's own fixed choices applied.</summary>
    public static PilotSettings ForFixture(PilotSettings baseSettings, BenchFixture fixture)
    {
        var node = JsonSerializer.SerializeToNode(baseSettings, SettingsStore.JsonOptions) as JsonObject
                   ?? throw new InvalidOperationException("settings did not serialize to an object");
        if (fixture.Settings != null)
        {
            var overrides = (JsonObject)fixture.Settings.DeepClone();
            foreach (var key in overrides.Select(kv => kv.Key).ToList())
                if (key.Equals("profiles", StringComparison.OrdinalIgnoreCase) || key.Equals("activeProfile", StringComparison.OrdinalIgnoreCase))
                    overrides.Remove(key);
            Merge(node, overrides);
        }
        var s = node.Deserialize<PilotSettings>(SettingsStore.JsonOptions) ?? throw new JsonException("settings are empty");
        s.Profiles = baseSettings.Profiles.Select(p => p.Clone()).ToList();
        s.ActiveProfile = baseSettings.ActiveProfile;
        // Score the model, not the rule-based fallback: an LLM failure is an error, not a fallback answer.
        s.Control.FallbackOnLlmFailure = false;
        s.LogPrompts = false;
        s.Normalize();
        return s;
    }

    /// <summary>A profile file has profile fields at the top level and no profile list.</summary>
    private static bool IsProfile(JsonObject node)
    {
        bool Has(string name) => node.Any(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        return !Has("profiles") && (Has("model") || Has("baseUrl") || Has("provider") || Has("apiKey"));
    }

    /// <summary>Deep merge: objects merge recursively (keys case-insensitive), everything else replaces.</summary>
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            var existing = target.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            source.Remove(key);
            if (value is JsonObject obj && target[existing] is JsonObject into) Merge(into, obj);
            else target[existing] = value;
        }
    }
}
