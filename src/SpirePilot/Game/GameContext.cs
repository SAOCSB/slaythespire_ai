using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePilot.Game;

/// <summary>
/// The game objects relevant for one snapshot, captured once on the main thread so every reader sees the same
/// state. Readers must treat everything here as possibly null and never cache it across frames.
/// </summary>
internal sealed class GameContext
{
    public RunManager? RunManager { get; private init; }
    /// <summary>Current run, null on menus.</summary>
    public RunState? Run { get; private init; }
    /// <summary>Live combat state, only while <see cref="CombatManager.IsInProgress"/>.</summary>
    public CombatState? Combat { get; private init; }
    /// <summary>The local player (from the combat state when in combat, else from the run state).</summary>
    public Player? Me { get; private init; }
    public bool InRun { get; private init; }
    public bool IsMultiplayer { get; private init; }
    public ulong LocalNetId { get; private init; }

    public static GameContext Capture()
    {
        var rm = RunManager.Instance;
        RunState? run = null;
        bool inRun = false;
        try
        {
            inRun = rm != null && rm.IsInProgress;
            run = inRun ? rm!.DebugOnlyGetState() : null;
        }
        catch (Exception) { /* run is being torn down */ }

        CombatState? combat = null;
        try
        {
            var cm = CombatManager.Instance;
            if (cm != null && cm.IsInProgress) combat = cm.DebugOnlyGetState();
        }
        catch (Exception) { /* combat is being torn down */ }

        Player? me = null;
        try
        {
            if (combat != null) me = LocalContext.GetMe((ICombatState)combat);
            if (me == null && run != null) me = LocalContext.GetMe((IPlayerCollection)run);
        }
        catch (Exception) { /* not ready */ }

        bool mp = false;
        ulong netId = 0;
        try
        {
            var net = rm?.NetService;
            if (net != null)
            {
                mp = net.Type is NetGameType.Host or NetGameType.Client;
                netId = net.NetId;
            }
            if (me != null) netId = me.NetId;
        }
        catch (Exception) { /* no net service yet */ }

        return new GameContext
        {
            RunManager = rm,
            Run = run,
            Combat = combat,
            Me = me,
            InRun = inRun && run != null,
            IsMultiplayer = mp,
            LocalNetId = netId,
        };
    }
}
