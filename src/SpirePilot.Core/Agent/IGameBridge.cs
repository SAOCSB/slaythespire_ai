using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Agent;

/// <summary>
/// The pilot's view of the game. Implemented by the mod; every method marshals to the Godot main thread
/// itself, so callers may use it from any thread.
/// </summary>
public interface IGameBridge
{
    /// <summary>Builds a fresh snapshot (null when no game state is available yet).</summary>
    Task<GameSnapshot?> CaptureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes an action taken from the most recent snapshot and waits until the game has settled
    /// enough that a new snapshot is meaningful (or a timeout elapsed).
    /// </summary>
    Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken cancellationToken);

    /// <summary>Shows a speech bubble above a player's character (visual only, local).</summary>
    void ShowSpeech(ulong playerNetId, string text);

    /// <summary>Pings a map node for teammates using the game's own map ping (co-op only).</summary>
    void PingMapNode(string nodeKey);

    /// <summary>
    /// For teammates without SpirePilot (they cannot read team messages): shows the closest vanilla co-op signal
    /// instead (a reaction emote such as thumbs up / question mark). Returns false when there is none.
    /// </summary>
    bool TrySignal(TeamMessageKind kind) => false;
}
