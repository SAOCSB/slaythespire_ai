using SpirePilot.Core.Model;

namespace SpirePilot.Game.Readers;

// OWNER: rooms1 agent. STUB — to be replaced by the full implementation.
/// <summary>Map (nodes, edges, current, reachable, teammate votes) and travel/vote actions. Also Ping(nodeKey) for teammates. Called for ScreenKind.Map.</summary>
internal static class MapReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
    /// <summary>Pings a map node ("col,row") for teammates with the game's own map ping (co-op). Main thread.</summary>
    public static void Ping(string nodeKey) { }
}
