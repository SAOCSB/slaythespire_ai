using SpirePilot.Core.Model;

namespace SpirePilot.Game.Readers;

// OWNER: rooms2 agent. STUB — to be replaced by the full implementation.
/// <summary>Main menu (continue / new run), character select (characters, ascension, embark; co-op lobby picks/ready), game over (continue / main menu), timeline/unlock flow. ReadMainMenu, ReadCharacterSelect, ReadGameOver.</summary>
internal static class MenuReader
{
    public static void ReadMainMenu(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
    public static void ReadCharacterSelect(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
    public static void ReadGameOver(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink) { }
}
