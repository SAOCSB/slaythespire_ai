using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Game;
using SpirePilot.Infra;

namespace SpirePilot.Ui;

/// <summary>
/// The overlay's editable copy of the settings. Every UI edit is recorded as a small mutation, applied to the draft
/// immediately and, about half a second after the last edit, re-applied to a fresh copy of the live settings and saved.
/// Replaying mutations (instead of saving the whole draft) keeps changes made elsewhere in the meantime (the pilot's
/// round-robin cursor, the HTTP API, mode buttons). Mutations must therefore locate objects by key (e.g. profile name),
/// never capture objects of the draft. Main thread only, except <see cref="OnSaved"/>.
/// </summary>
internal static class SettingsDraft
{
    private const ulong DebounceMs = 500;
    private static readonly List<Action<PilotSettings>> Pending = new();
    private static ulong _saveAtMs;
    private static volatile bool _reloadRequested;

    public static PilotSettings Value { get; private set; } = new();

    /// <summary>Increments whenever <see cref="Value"/> was reloaded; the overlay re-syncs its controls then.</summary>
    public static int Version { get; private set; }

    public static bool Dirty => Pending.Count > 0;

    /// <summary>Last save problem (null after a successful save).</summary>
    public static string? SaveError { get; private set; }

    public static ulong LastSavedMs { get; private set; }

    public static void Initialize()
    {
        Value = SettingsStore.Clone(PilotRuntime.Settings);
        Version++;
        PilotRuntime.SettingsChanged += OnSaved;
    }

    /// <summary>Raised on whatever thread saved the settings.</summary>
    private static void OnSaved(PilotSettings _) => _reloadRequested = true;

    /// <summary>
    /// Applies <paramref name="change"/> to the draft and schedules a save (or saves now). Returns false when the edit
    /// failed or, with <paramref name="now"/>, the save failed (<see cref="SaveError"/> then says why).
    /// </summary>
    public static bool Edit(Action<PilotSettings> change, bool now = false)
    {
        try { change(Value); }
        catch (Exception ex)
        {
            ThrottledLog.Warn("ui.draft", "UI: settings edit failed: " + ex.Message);
            return false;
        }
        Pending.Add(change);
        _saveAtMs = Time.GetTicksMsec() + DebounceMs;
        return !now || Flush();
    }

    /// <summary>
    /// Saves pending edits right away; after a failed save it also retries with nothing pending (the footer's "Save"
    /// button), so a fixed settings file clears the error. Returns false when saving failed.
    /// </summary>
    public static bool Flush()
    {
        if (Pending.Count == 0 && SaveError == null) return true;
        var pending = Pending.ToList();
        Pending.Clear();
        bool ok;
        try
        {
            // Replayed on a fresh copy of the live settings inside the runtime's settings lock, so a change made
            // elsewhere meanwhile (HTTP API, the pilot's rotation cursor) is kept.
            PilotRuntime.UpdateSettings(fresh =>
            {
                foreach (var change in pending)
                {
                    try { change(fresh); }
                    catch (Exception ex) { ThrottledLog.Warn("ui.draft.replay", "UI: settings edit could not be replayed: " + ex.Message); }
                }
            });
            Report(null);
            ok = true;
        }
        catch (Exception ex)
        {
            // The settings still apply for this session (the store keeps them); only the file is behind.
            Report(ex);
            ok = false;
        }
        // Our own save raised SettingsChanged synchronously (a failed one raised nothing); one reload covers both.
        _reloadRequested = false;
        Reload();
        return ok;
    }

    /// <summary>
    /// Records the outcome of a save: from <see cref="Flush"/>, or from a write made outside the draft through
    /// <see cref="PilotRuntime.UpdateSettings"/> (e.g. <see cref="PilotRuntime.SetMode"/>). A failure shows in the
    /// panel's footer until a later save succeeds.
    /// </summary>
    public static void Report(Exception? error)
    {
        if (error == null)
        {
            SaveError = null;
            LastSavedMs = Time.GetTicksMsec();
            return;
        }
        SaveError = error.Message;
        ModLog.Warn("UI: saving settings failed: " + error.Message);
        // A failed save raises no SettingsChanged, but the live settings did change: pick them up.
        _reloadRequested = true;
    }

    public static void Tick()
    {
        if (Pending.Count > 0 && Time.GetTicksMsec() >= _saveAtMs) Flush();
        if (_reloadRequested)
        {
            _reloadRequested = false;
            Reload();
        }
    }

    /// <summary>Draft = live settings + edits not saved yet.</summary>
    private static void Reload()
    {
        var s = SettingsStore.Clone(PilotRuntime.Settings);
        foreach (var change in Pending)
        {
            try { change(s); }
            catch (Exception) { /* logged on flush */ }
        }
        Value = s;
        Version++;
    }
}
