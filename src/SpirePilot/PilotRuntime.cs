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
    private static volatile string? _gameLanguage;
    private static readonly object SettingsLock = new();

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
        Loop.NewRunDetected += OnNewRunDetected;
        var traces = new TraceWriter(DataDir);
        Loop.DecisionTraced += t => { if (Store.Current.SaveDecisionTraces) traces.Write(t); };
        // Status changes come from the loop's worker thread; the team link only runs on the main thread.
        Loop.StatusChanged += _ =>
        {
            bool active = Store.Current.Control.Mode is not (PilotMode.Off or PilotMode.Advisor) && !Loop.Paused;
            MainThread.Post(() => TeamLink.SetLocalAiActive(active));
        };

        MainThread.Install();
        HarmonyPreload.EnsureLoaded();
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
        // Localization is not ready when mods load, and the player can switch the game language later: keep
        // following it (cheap; checked a few times per second), so the 'auto' language setting follows the game.
        if (_gameLanguage == null || MainThread.FrameCount % 20 == 0) TrackGameLanguage();
        try { TeamLink.Tick(); } catch (Exception ex) { ThrottledLog.Warn("teamlink.tick", "team link tick failed: " + ex.Message); }
        try { Overlay.Tick(); } catch (Exception ex) { ThrottledLog.Warn("overlay.tick", "overlay tick failed: " + ex.Message); }
        try { HttpApi?.Pump(); } catch (Exception ex) { ThrottledLog.Warn("http.pump", "http api failed: " + ex.Message); }
    }

    private static void TrackGameLanguage()
    {
        string? lang;
        try { lang = LocManager.Instance?.Language; }
        catch (Exception) { return; /* not ready yet */ }
        if (string.IsNullOrEmpty(lang) || lang == _gameLanguage) return;
        bool first = _gameLanguage == null;
        _gameLanguage = lang;
        L10n.Configure(Settings.Ui.Language, lang);
        ModLog.Info((first ? "game language: " : "game language changed: ") + lang);
    }

    /// <summary>
    /// The one way to change settings: clones the live settings, applies <paramref name="mutate"/>, saves and applies
    /// the result, all under one lock, so concurrent writers (UI, HTTP API threads, the pilot's worker) never lose each
    /// other's changes. Side effects that touch Godot or the HTTP listener run on the main thread. Any thread.
    /// Throws when the file cannot be written (the new settings still apply for this session).
    /// </summary>
    public static PilotSettings UpdateSettings(Action<PilotSettings> mutate)
    {
        PilotSettings saved;
        lock (SettingsLock)
        {
            var s = SettingsStore.Clone(Store.Current);
            mutate(s);
            Store.Save(s);
            saved = Store.Current;
            L10n.Configure(saved.Ui.Language, _gameLanguage);
            Team.MaxAiMessagesPerMinute = saved.Team.MaxAiMessagesPerMinute;
        }
        // The HTTP API is started / stopped / re-keyed from the main thread only (never from one of its own request
        // threads, which Dispose would wait on).
        if (MainThread.IsMainThread) ApplyHttpApi();
        else MainThread.Post(ApplyHttpApi);
        return saved;
    }

    /// <summary>Convenience for the UI and the HTTP API: change the mode and persist it. Any thread.</summary>
    public static void SetMode(PilotMode mode)
    {
        UpdateSettings(s => s.Control.Mode = mode);
        if (mode != PilotMode.Off) Loop.Paused = false;
    }

    /// <summary>Writes a diagnostics report (main thread) and returns its path, or null on failure.</summary>
    public static string? WriteDiagnostics()
    {
        try { return Diagnostics.Write(); }
        catch (Exception ex)
        {
            ModLog.Error("diagnostics failed: " + ex);
            return null;
        }
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

    /// <summary>Raised on the pilot's worker thread: the settings change runs on the main thread.</summary>
    private static void OnRunStarted() => MainThread.Post(() =>
    {
        // Persist the round-robin cursor so character rotation survives restarts.
        try { UpdateSettings(s => s.Characters.RoundRobinCursor++); }
        catch (Exception ex) { ModLog.Warn("saving the character rotation failed: " + ex.Message); }
        Team.AddSystemNote(L10n.T("ui.run_started"));
    });

    /// <summary>
    /// A pinned route belongs to the map it was drawn on; a new run has a different map. Raised on the pilot's worker
    /// thread: the settings change runs on the main thread.
    /// </summary>
    private static void OnNewRunDetected(string seed) => MainThread.Post(() =>
    {
        // A pin made in this very run (e.g. while the pilot was paused) stays; only another run's pin goes.
        var route = Settings.Route;
        if (route.PinnedPath.Count == 0 || route.PinnedSeed == seed) return;
        try
        {
            UpdateSettings(s => { s.Route.PinnedPath.Clear(); s.Route.PinnedSeed = ""; });
            ModLog.Info("new run: pinned route cleared");
        }
        catch (Exception ex) { ModLog.Warn("clearing the pinned route failed: " + ex.Message); }
    });

    /// <summary>Main thread: starts, stops or re-keys the HTTP API to match the settings.</summary>
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
