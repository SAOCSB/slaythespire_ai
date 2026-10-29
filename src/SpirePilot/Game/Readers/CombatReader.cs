using SpirePilot.Core.Model;

namespace SpirePilot.Game.Readers;

// OWNER: combat agent. STUB — to be replaced by the full implementation.
/// <summary>Combat info (hand, piles, enemies, intents) and actions: play card (every valid target), use potion (every valid target incl. teammates), end turn. Called for ScreenKind.Combat. FillState is called for every snapshot while in combat (also when a selection screen is open).</summary>
internal static class CombatReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
    /// <summary>Fills <see cref="GameSnapshot.Combat"/> (round, hand, piles, enemies) whenever combat is in progress.</summary>
    public static void FillState(GameContext ctx, GameSnapshot snap) { }
}
