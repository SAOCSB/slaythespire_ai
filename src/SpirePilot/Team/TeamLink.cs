using HarmonyLib;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Team;

// OWNER: team agent. STUB — to be replaced by the full implementation.
/// <summary>
/// Co-op team channel between players who run SpirePilot: raw packets over the game's own multiplayer transport,
/// capability discovery (who has the mod / an active AI), host relay, and delivery into <see cref="TeamChatHub"/>.
/// </summary>
internal static class TeamLink
{
    /// <summary>Transport used by <see cref="TeamChatHub"/>.</summary>
    public static ITeamTransport Transport { get; } = new NullTransport();

    /// <summary>Installs Harmony patches (packet receive hooks, handshake hooks). Called once at startup.</summary>
    public static void Initialize(Harmony harmony, TeamChatHub hub) { }

    /// <summary>Called every frame on the main thread (flush queued inbound messages, hello announcements ...).</summary>
    public static void Tick() { }

    /// <summary>Marks players in the snapshot that have SpirePilot (<see cref="PlayerInfo.HasTeamLink"/>) or an active AI.</summary>
    public static void Annotate(GameSnapshot snap) { }

    /// <summary>Tells linked teammates whether our AI pilot is active (sent on change and when peers join).</summary>
    public static void SetLocalAiActive(bool active) { }

    private sealed class NullTransport : ITeamTransport
    {
        public bool IsAvailable => false;
        public IReadOnlyCollection<ulong> LinkedPeers => Array.Empty<ulong>();
        public bool Send(TeamMessage message) => false;
    }
}
