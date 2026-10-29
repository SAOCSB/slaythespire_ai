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

    /// <summary>Applies <paramref name="change"/> to the draft and schedules a save (or saves now).</summary>
    public static void Edit(Action<PilotSettings> change, bool now = false)
    {
        try { change(Value); }
        catch (Exception ex)
        {
            ThrottledLog.Warn("ui.draft", "UI: settings edit failed: " + ex.Message);
            return;
        }
        Pending.Add(change);
        _saveAtMs = Time.GetTicksMsec() + DebounceMs;
        if (now) Flush();
    }

    /// <summary>Saves pending edits right away.</summary>
    public static void Flush()
    {
        if (Pending.Count == 0) return;
        var pending = Pending.ToList();
        Pending.Clear();
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
            SaveError = null;
            LastSavedMs = Time.GetTicksMsec();
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
            ModLog.Warn("UI: saving settings failed: " + ex.Message);
        }
        // Our own save raised SettingsChanged synchronously; one reload covers it.
        _reloadRequested = false;
        Reload();
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
