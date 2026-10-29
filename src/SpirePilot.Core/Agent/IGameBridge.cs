using SpirePilot.Core.Model;

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
}
