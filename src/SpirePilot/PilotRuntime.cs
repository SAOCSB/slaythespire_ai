using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
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

    /// <summary>
    /// True while this build is incompatible with the running game and the owner did not choose "run anyway" for this
    /// pairing: the AI stays off (the saved mode is kept for a matching build), and the HTTP API refuses to act.
    /// </summary>
    public static bool VersionBlocked =>
        _gameVersion is { Fit: GameCompatibility.Incompatible } v && Settings.Control.IgnoreGameVersionFor != v.OverrideKey;

    /// <summary>
    /// The value "run anyway" stores for this build on this game, while the two are incompatible (the panel then offers
    /// the switch); null otherwise.
    /// </summary>
    public static string? VersionOverrideKey => _gameVersion is { Fit: GameCompatibility.Incompatible } v ? v.OverrideKey : null;

    /// <summary>The mode the pilot runs in: the saved one, or Off while <see cref="VersionBlocked"/>.</summary>
    public static PilotMode EffectiveMode => VersionBlocked ? PilotMode.Off : Settings.Control.Mode;

    /// <summary>
    /// Why the AI is off while <see cref="VersionBlocked"/>, naming only builds that exist; null otherwise (also while
    /// the game version is not known yet).
    /// </summary>
    public static string? VersionProblem => VersionBlocked ? VersionText(english: false, blocked: true) : null;

    /// <summary><see cref="VersionProblem"/> in English, for the log and the HTTP API.</summary>
    public static string? VersionProblemEnglish => VersionBlocked ? VersionText(english: true, blocked: true) : null;

    /// <summary>
    /// The AI runs on a game version this build was not made for: a newer, untested one, or an incompatible one by the
    /// owner's choice. Null otherwise.
    /// </summary>
    public static string? VersionWarning => VersionWarned ? VersionText(english: false, blocked: false) : null;

    /// <summary><see cref="VersionWarning"/> in English, for the log, the HTTP API and diagnostics.</summary>
    public static string? VersionWarningEnglish => VersionWarned ? VersionText(english: true, blocked: false) : null;

    /// <summary>
    /// <see cref="VersionProblem"/> or <see cref="VersionWarning"/> cut to what the overlay's toast has room for: the
    /// versions and where the full notice is. Null when there is neither.
    /// </summary>
    public static string? VersionToast =>
        _gameVersion is { } v && (VersionBlocked || VersionWarned)
            ? L10n.T(VersionBlocked ? "ui.version.toast_off" : "ui.version.toast_runs", Diagnostics.CompiledFor, v.Version, L10n.T("ui.tab.control"))
            : null;

    private static bool VersionWarned => _gameVersion is { Fit: not GameCompatibility.Ok } && !VersionBlocked;

    private sealed record GameVersionFit(string Version, GameCompatibility Fit, string OverrideKey);
    private sealed record OffView(PilotSettings Saved, PilotSettings Off);

    /// <summary>The game version and how this build fits it, once known (stays null when unreadable).</summary>
    private static volatile GameVersionFit? _gameVersion;
    private static bool _versionChecked;
    private static int _versionAttempts;
    private static bool _wasBlocked;
    private static volatile OffView? _offView;
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
        if (!File.Exists(Store.Path))
        {
            // An unwritable data folder must not cost the whole mod: run on the in-memory settings.
            try { Store.Save(); }
            catch (Exception ex) { ModLog.Warn("could not write the first settings file (using in-memory settings): " + ex.Message); }
        }
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
        Loop = new PilotLoop(Bridge, Engine, Team, LoopSettings, log);
        Loop.RunStarted += OnRunStarted;
        Loop.NewRunDetected += OnNewRunDetected;
        var traces = new TraceWriter(DataDir);
        Loop.DecisionTraced += t => { if (Store.Current.SaveDecisionTraces) traces.Write(t); };
        // Status changes come from the loop's worker thread; the team link only runs on the main thread.
        Loop.StatusChanged += _ =>
        {
            bool active = EffectiveMode is not (PilotMode.Off or PilotMode.Advisor) && !Loop.Paused;
            MainThread.Post(() => TeamLink.SetLocalAiActive(active));
        };

        MainThread.Install();
        HarmonyPreload.EnsureLoaded();
        Harmony = new Harmony("spirepilot.mod");
        TryInit("team link", () => TeamLink.Initialize(Harmony, Team));
        TryInit("overlay", () => Overlay.Initialize(Harmony));
        MainThread.Frame += OnFrame;

        CheckGameVersion();
        ApplyHttpApi();
        ModLog.Info("SpirePilot ready");
    }

    /// <summary>
    /// Main thread, until it has decided: compares the game's major.minor with the branch this DLL was compiled for and
    /// starts the pilot unless the build is incompatible (or when the version cannot be read at all). An incompatible
    /// build would fail at the first game member that changed (character select, potions ...), so the AI stays off
    /// until the owner chooses "run anyway"; a game newer than every build runs with a warning.
    /// </summary>
    private static void CheckGameVersion()
    {
        if (_versionChecked) return;
        string? version;
        try { version = ReadGameVersion(); }
        catch (Exception) { version = null; }
        var fit = GameVersion.Compatibility(version, Diagnostics.CompiledFor);
        if (fit == null)
        {
            // Not readable yet; a build without release info never is: then run unchecked after a while.
            if (++_versionAttempts < 600) return;
            ModLog.Warn($"game version unreadable ({version ?? "none"}): running without the version check");
        }
        lock (SettingsLock)
        {
            if (fit is { } f) _gameVersion = new GameVersionFit(version!.Trim(), f, GameVersion.OverrideKey(version, Diagnostics.CompiledFor)!);
            _versionChecked = true;
            _wasBlocked = VersionBlocked;
            if (VersionProblemEnglish is { } problem) ModLog.Error(problem);
            else if (VersionWarningEnglish is { } warning) ModLog.Warn(warning);
            if (!_wasBlocked) Loop.Start();
        }
    }

    /// <summary>The version notice: why the AI is off (<paramref name="blocked"/>), or why it runs on an untested game.</summary>
    private static string VersionText(bool english, bool blocked)
    {
        var (game, fit, _) = _gameVersion!;
        var built = Diagnostics.CompiledFor;
        if (!blocked)
        {
            if (fit == GameCompatibility.Untested)
                return english
                    ? $"this SpirePilot build is for game {built}; game {game} is newer and untested: the AI runs, but parts may not work; check for a SpirePilot update"
                    : L10n.T("ui.version.untested", built, game);
            return english
                ? $"this SpirePilot build is for game {built}, but the game is {game}: running anyway as the owner chose; screens may be misread and actions may fail"
                : L10n.T("ui.version.forced", built, game);
        }
        var build = GameVersion.BuildFor(game, out bool untested);
        var branch = GameVersion.Branch(game) ?? game;
        if (!english)
        {
            return build == null ? L10n.T("ui.version.no_build", built, game, branch)
                : untested ? L10n.T("ui.version.no_build_try", built, game, branch, build)
                : L10n.T("status.version_mismatch", built, game, build);
        }
        var fix = build == null ? $"there is no SpirePilot build for game {branch} yet: check for a SpirePilot update"
            : untested ? $"there is no SpirePilot build for game {branch} yet: check for a SpirePilot update, or try the {build} build (untested on this game)"
            : $"install the {build} build";
        return $"this SpirePilot build is for game {built}, but the game is {game}: the AI stays off; {fix} " +
               "('Run anyway' on the SpirePilot panel's Control tab overrides this, at your own risk)";
    }

    /// <summary>
    /// The settings the pilot loop runs on: the live ones, with mode Off while <see cref="VersionBlocked"/> (in memory
    /// only, so the saved mode comes back with a matching build or "run anyway"). Any thread.
    /// </summary>
    private static PilotSettings LoopSettings()
    {
        var live = Store.Current;
        if (!VersionBlocked) return live;
        var view = _offView;
        if (view == null || !ReferenceEquals(view.Saved, live))
        {
            var off = SettingsStore.Clone(live);
            off.Control.Mode = PilotMode.Off;
            _offView = view = new OffView(live, off);
        }
        return view.Off;
    }

    /// <summary>Its own method: a game update that changes the release info breaks this call, not the caller.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadGameVersion() => ReleaseInfoManager.Instance?.ReleaseInfo?.Version;

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
        if (!_versionChecked)
        {
            try { CheckGameVersion(); }
            catch (Exception ex) { ThrottledLog.Warn("version.check", "game version check failed: " + ex.Message); }
        }
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
    /// Throws when the file cannot be written, after applying the new settings for this session all the same.
    /// </summary>
    public static PilotSettings UpdateSettings(Action<PilotSettings> mutate)
    {
        PilotSettings saved;
        ExceptionDispatchInfo? saveError = null;
        lock (SettingsLock)
        {
            var s = SettingsStore.Clone(Store.Current);
            mutate(s);
            // The store takes the new settings before it writes the file: they are live even when writing fails.
            try { Store.Save(s); }
            catch (Exception ex) { saveError = ExceptionDispatchInfo.Capture(ex); }
            saved = Store.Current;
            L10n.Configure(saved.Ui.Language, _gameLanguage);
            Team.MaxAiMessagesPerMinute = saved.Team.MaxAiMessagesPerMinute;
            ApplyVersionOverride();
        }
        // The HTTP API is started / stopped / re-keyed from the main thread only (never from one of its own request
        // threads, which Dispose would wait on).
        if (MainThread.IsMainThread) ApplyHttpApi();
        else MainThread.Post(ApplyHttpApi);
        if (saveError != null)
        {
            // The store raises Saved only after writing the file; the panel's draft must follow the live settings anyway.
            SettingsChanged?.Invoke(saved);
            saveError.Throw();
        }
        return saved;
    }

    /// <summary>
    /// Under the settings lock, after a save: "run anyway" was switched on (the pilot starts, in the mode the panel set
    /// to Off with it) or off again (<see cref="LoopSettings"/> holds it at Off).
    /// </summary>
    private static void ApplyVersionOverride()
    {
        if (!_versionChecked || VersionBlocked == _wasBlocked) return;
        _wasBlocked = !_wasBlocked;
        if (_wasBlocked)
        {
            ModLog.Warn("'run anyway' is off: " + VersionProblemEnglish);
            return;
        }
        ModLog.Warn(VersionWarningEnglish!);
        Loop.Start();
    }

    /// <summary>
    /// Convenience for the UI and the HTTP API: change the mode and persist it. Any thread. Throws when the file cannot
    /// be written; the mode applies all the same.
    /// </summary>
    public static void SetMode(PilotMode mode)
    {
        try { UpdateSettings(s => s.Control.Mode = mode); }
        finally
        {
            if (mode != PilotMode.Off && !VersionBlocked) Loop.Paused = false;
        }
    }

    /// <summary>
    /// Writes a diagnostics report and returns its path, or null on failure. Any thread; the game is read on the main
    /// thread, the log and the file are handled off it.
    /// </summary>
    public static async Task<string?> WriteDiagnosticsAsync()
    {
        try { return (await Diagnostics.WriteAsync().ConfigureAwait(false)).Path; }
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
