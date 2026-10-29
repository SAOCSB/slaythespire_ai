using SpirePilot.Core.Model;

namespace SpirePilot.Game;

// OWNER: combat agent. STUB — to be replaced by the full implementation.
/// <summary>Fills run-level info and every player's public state (local player and teammates).</summary>
internal static class PlayerReader
{
    /// <summary>Fills <see cref="GameSnapshot.Run"/> (act, floor, ascension, seed, boss).</summary>
    public static void FillRun(GameContext ctx, GameSnapshot snap) { }

    /// <summary>
    /// Fills <see cref="GameSnapshot.Players"/>: local player first, then teammates in slot order. Includes HP, block,
    /// gold, energy/stars/orbs/companions (in combat), powers, potions (with slots), relics, deck (local player full,
    /// teammates too), hand count and "ended turn" (co-op combat).
    /// </summary>
    public static void FillPlayers(GameContext ctx, GameSnapshot snap) { }
}
