using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SpirePilot.Api;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Team;
using SpirePilot.Core.Text;
using SpirePilot.Game;
using SpirePilot.Infra;
using SpirePilot.Team;
using SpirePilot.Ui;

namespace SpirePilot;

/// <summary>
/// Owns every long-lived SpirePilot service and wires them together. The UI and the HTTP API talk to the pilot only
/// through this class. Members are safe to read from any thread unless noted.
/// </summary>
internal static class PilotRuntime
{
    public const string ModId = "SpirePilot";
    public static readonly string Version = typeof(PilotRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DataDir { get; private set; } = "";
    public static SettingsStore Store { get; private set; } = null!;
    public static PilotSettings Settings => Store.Current;
    public static TeamChatHub Team { get; private set; } = null!;
    public static DecisionEngine Engine { get; private set; } = null!;
    public static PilotLoop Loop { get; private set; } = null!;
    public static GameBridge Bridge { get; private set; } = null!;
    public static Harmony Harmony { get; private set; } = null!;
    public static LocalHttpApi? HttpApi { get; private set; }

    /// <summary>Raised (on the saving thread) after settings were saved.</summary>
    public static event Action<PilotSettings>? SettingsChanged;

    private static bool _initialized;
    private static string? _gameLanguage;

    /// <summary>Main thread, from the mod initializer.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        DataDir = Path.Combine(OS.GetUserDataDir(), "mod_configs", ModId);
        Directory.CreateDirectory(DataDir);
        ModLog.Configure(DataDir);
        ModLog.Info($"SpirePilot {Version} starting; data dir {DataDir}");

        Store = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        Store.Load();
        if (Store.LoadError != null) ModLog.Warn("settings file unreadable, using defaults: " + Store.LoadError);
        if (!File.Exists(Store.Path)) Store.Save();
        Store.Saved += s => SettingsChanged?.Invoke(s);
        L10n.Configure(Settings.Ui.Language, null);

        Team = new TeamChatHub { MaxAiMessagesPerMinute = Settings.Team.MaxAiMessagesPerMinute };
        Team.Transport = TeamLink.Transport;
        Bridge = new GameBridge();
        var log = new ModLog.Sink();
        Engine = new DecisionEngine(() => Store.Current, Team, log)
        {
            MessageLanguage = () => ResolveMessageLanguage(Store.Current),
        };
        Loop = new PilotLoop(Bridge, Engine, Team, () => Store.Current, log);
        Loop.RunStarted += OnRunStarted;
        Loop.StatusChanged += s => TeamLink.SetLocalAiActive(Store.Current.Control.Mode is not (PilotMode.Off or PilotMode.Advisor) && !Loop.Paused);

        MainThread.Install();
        Harmony = new Harmony("spirepilot.mod");
        TryInit("team link", () => TeamLink.Initialize(Harmony, Team));
        TryInit("overlay", () => Overlay.Initialize(Harmony));
        MainThread.Frame += OnFrame;

        ApplyHttpApi();
        Loop.Start();
        ModLog.Info("SpirePilot ready");
    }

    private static void TryInit(string what, Action init)
    {
        try { init(); }
        catch (Exception ex) { ModLog.Error($"{what} failed to initialize (feature disabled): {ex}"); }
    }

    private static void OnFrame()
    {
        // Localization is not ready when mods load; pick up the game language once it is.
        if (_gameLanguage == null)
        {
            try
            {
                var lang = LocManager.Instance?.Language;
                if (!string.IsNullOrEmpty(lang))
                {
                    _gameLanguage = lang;
                    L10n.Configure(Settings.Ui.Language, lang);
                    ModLog.Info("game language: " + lang);
                }
            }
            catch (Exception) { /* not yet */ }
        }
        try { TeamLink.Tick(); } catch (Exception ex) { ThrottledLog.Warn("teamlink.tick", "team link tick failed: " + ex.Message); }
        try { Overlay.Tick(); } catch (Exception ex) { ThrottledLog.Warn("overlay.tick", "overlay tick failed: " + ex.Message); }
        try { HttpApi?.Pump(); } catch (Exception ex) { ThrottledLog.Warn("http.pump", "http api failed: " + ex.Message); }
    }

    /// <summary>Saves new settings and applies everything that depends on them.</summary>
    public static void SaveSettings(PilotSettings settings)
    {
        Store.Save(settings);
        L10n.Configure(settings.Ui.Language, _gameLanguage);
        Team.MaxAiMessagesPerMinute = settings.Team.MaxAiMessagesPerMinute;
        ApplyHttpApi();
    }

    /// <summary>Convenience for the UI: change the mode and persist it.</summary>
    public static void SetMode(PilotMode mode)
    {
        var s = SettingsStore.Clone(Settings);
        s.Control.Mode = mode;
        if (mode != PilotMode.Off) Loop.Paused = false;
        SaveSettings(s);
    }

    /// <summary>Resume after a pause / error.</summary>
    public static void Resume()
    {
        Loop.Paused = false;
    }

    /// <summary>Human-readable language name for AI team messages.</summary>
    public static string ResolveMessageLanguage(PilotSettings s)
    {
        var lang = (s.Team.MessageLanguage ?? "auto").Trim().ToLowerInvariant();
        if (lang is "zh" or "zhs" or "chinese") return "Simplified Chinese";
        if (lang is "en" or "eng" or "english") return "English";
        if (lang != "auto" && lang.Length > 0) return s.Team.MessageLanguage!;
        return L10n.IsChinese ? "Simplified Chinese" : "English";
    }

    private static void OnRunStarted()
    {
        // Persist the round-robin cursor so character rotation survives restarts.
        var s = SettingsStore.Clone(Settings);
        s.Characters.RoundRobinCursor++;
        SaveSettings(s);
        Team.AddSystemNote(L10n.T("ui.run_started"));
    }

    private static void ApplyHttpApi()
    {
        var cfg = Settings.HttpApi;
        try
        {
            if (cfg.Enabled && (HttpApi == null || HttpApi.Port != cfg.Port))
            {
                HttpApi?.Dispose();
                HttpApi = new LocalHttpApi(cfg.Port, cfg.Token);
                HttpApi.Start();
            }
            else if (!cfg.Enabled && HttpApi != null)
            {
                HttpApi.Dispose();
                HttpApi = null;
            }
            else if (HttpApi != null)
            {
                HttpApi.Token = cfg.Token;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("HTTP API failed to start: " + ex.Message);
            HttpApi = null;
        }
    }
}
