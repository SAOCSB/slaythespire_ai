using SpirePilot.Core.Model;

namespace SpirePilot.Game.Readers;

// OWNER: rooms1 agent. STUB — to be replaced by the full implementation.
/// <summary>Combat rewards screen (claim each reward / proceed) via Read, and the card reward screen (pick a card / skip / reroll) via ReadCardReward.</summary>
internal static class RewardsReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
    public static void ReadCardReward(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
}
