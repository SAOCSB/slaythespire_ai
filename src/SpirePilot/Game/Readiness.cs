using SpirePilot.Core.Model;

namespace SpirePilot.Game;

// OWNER: combat agent. STUB — to be replaced by the full implementation.
/// <summary>Decides whether the local player can act now (no animations / queued actions / transitions).</summary>
internal static class Readiness
{
    /// <summary>
    /// Returns (true, null) when the AI may act on <paramref name="probe"/>'s screen right now, otherwise false and a
    /// short reason ("animating", "enemy turn", "waiting for teammates", "transition", ...).
    /// </summary>
    public static (bool canAct, string? reason) Check(GameContext ctx, ScreenProbe probe, GameSnapshot snap)
    {
        if (probe.HumanOnlyReason != null) return (false, probe.HumanOnlyReason);
        if (UiActions.GameBusy()) return (false, "busy");
        return (true, null);
    }
}
