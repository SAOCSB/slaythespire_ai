using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using SpirePilot.Game;

namespace SpirePilot.Infra;

/// <summary>
/// Writes a self-contained diagnostics report (versions, patches, missing game members, current screen and snapshot,
/// recent log) for bug reports. The game parts are read on the main thread, the log tail and the file off it. API
/// keys are never included.
/// </summary>
internal static class Diagnostics
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Builds the report text. Any thread.</summary>
    public static async Task<string> BuildAsync()
    {
        var report = await MainThread.Run(BuildGameParts).ConfigureAwait(false);
        // The log can be megabytes: its tail is read off the main thread.
        return report + await Task.Run(RecentLog).ConfigureAwait(false);
    }

    /// <summary>Writes the report next to the log and returns its path. Any thread.</summary>
    public static async Task<string> WriteAsync()
    {
        var report = await BuildAsync().ConfigureAwait(false);
        var path = Path.Combine(PilotRuntime.DataDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, report).ConfigureAwait(false);
        ModLog.Info("diagnostics written to " + path);
        return path;
    }

    /// <summary>Everything but the log (main thread).</summary>
    private static string BuildGameParts()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SpirePilot diagnostics");
        sb.AppendLine($"time: {DateTime.Now:O}");
        sb.AppendLine($"SpirePilot: {PilotRuntime.Version} (compiled for game API {CompiledFor})");
        sb.AppendLine($"game: {Try(() => ReleaseInfoManager.Instance?.ReleaseInfo?.Version) ?? "?"}");
        sb.AppendLine($"godot: {Try(() => Engine.GetVersionInfo()["string"].AsString()) ?? "?"}");
        sb.AppendLine($"os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; .NET {System.Environment.Version}");
        sb.AppendLine($"data dir: {PilotRuntime.DataDir}");

        var s = PilotRuntime.Settings;
        var p = s.Active;
        sb.AppendLine();
        sb.AppendLine("## Pilot");
        sb.AppendLine($"mode: {s.Control.Mode}, paused: {PilotRuntime.Loop.Paused}, status: {PilotRuntime.Loop.Status.State} — {PilotRuntime.Loop.Status.Text}");
        sb.AppendLine($"profile: {p?.Name} provider={p?.Provider} model={p?.Model} baseUrl={p?.BaseUrl} key={(string.IsNullOrEmpty(p?.ApiKey) ? "none" : p!.ApiKey.StartsWith("env:") ? p.ApiKey : $"set ({p.ApiKey.Length} chars)")} proxy={(string.IsNullOrEmpty(p?.ProxyUrl) ? "-" : "set")}");
        var e = PilotRuntime.Engine;
        sb.AppendLine($"llm calls: {e.LlmCalls}, tokens in/out/cached: {e.TotalInputTokens}/{e.TotalOutputTokens}/{e.TotalCachedTokens}, last error: {e.LastLlmError ?? "-"}");
        sb.AppendLine($"team link: available={Try(() => Team.TeamLink.Transport.IsAvailable.ToString())}, linked peers={Try(() => Team.TeamLink.Transport.LinkedPeers.Count.ToString())}");

        sb.AppendLine();
        sb.AppendLine("## Harmony patches (ours)");
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info != null && info.Owners.Contains(PilotRuntime.Harmony.Id))
                    sb.AppendLine($"- {method.DeclaringType?.FullName}.{method.Name}");
            }
        }
        catch (Exception ex) { sb.AppendLine("(unavailable: " + ex.Message + ")"); }

        sb.AppendLine();
        sb.AppendLine("## Missing game members (looked up by reflection)");
        var missing = Reflect.MissingMembers;
        sb.AppendLine(missing.Count == 0 ? "(none)" : string.Join("\n", missing.Select(m => "- " + m)));

        sb.AppendLine();
        sb.AppendLine("## Screen");
        try
        {
            var probe = ScreenResolver.Resolve(GameContext.Capture());
            sb.AppendLine($"kind={probe.Kind} name={probe.Name} humanOnly={probe.HumanOnlyReason ?? "-"} modal={probe.Modal?.GetType().Name ?? "-"} underlying={probe.Underlying?.GetType().Name ?? "-"}");
        }
        catch (Exception ex) { sb.AppendLine("(probe failed: " + ex + ")"); }

        sb.AppendLine();
        sb.AppendLine("## Snapshot");
        try
        {
            var snap = PilotRuntime.Bridge.CaptureOnMainThread();
            sb.AppendLine("```json");
            sb.AppendLine(snap == null ? "null" : JsonSerializer.Serialize(snap, Json));
            sb.AppendLine("```");
        }
        catch (Exception ex) { sb.AppendLine("(snapshot failed: " + ex + ")"); }

        return sb.ToString();
    }

    private static string RecentLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Recent log");
        try
        {
            var lines = ModLog.Tail(200);
            if (lines.Count > 0)
            {
                sb.AppendLine("```");
                foreach (var line in lines) sb.AppendLine(line);
                sb.AppendLine("```");
            }
        }
        catch (Exception ex) { sb.AppendLine("(log unavailable: " + ex.Message + ")"); }
        return sb.ToString();
    }

    /// <summary>The game branch this DLL was compiled against ("0.107" / "0.111").</summary>
    internal const string CompiledFor =
#if STS2_V0107
        "0.107";
#else
        "0.111";
#endif

    private static string? Try(Func<string?> f)
    {
        try { return f(); }
        catch (Exception) { return null; }
    }
}
