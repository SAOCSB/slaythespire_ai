namespace SpirePilot.Team;

/// <summary>
/// What the team link knows about one other player in the current network session. Mutated only on the main
/// thread under <c>TeamLink</c>'s roster lock; read under the same lock from any thread.
/// Times are monotonic milliseconds (<see cref="Environment.TickCount64"/>), never wall-clock time.
/// </summary>
internal sealed class TeamPeer
{
    public TeamPeer(ulong netId) => NetId = netId;

    public ulong NetId { get; }

    /// <summary>The peer's handshake mod list names SpirePilot, so a Hello may be sent to it.</summary>
    public bool Advertised { get; set; }

    /// <summary>A valid team packet arrived from this peer: it runs SpirePilot (= <c>PlayerInfo.HasTeamLink</c>).</summary>
    public bool Confirmed { get; set; }

    /// <summary>The peer's last Hello said its AI pilot is active.</summary>
    public bool IsAi { get; set; }

    /// <summary>SpirePilot version from the peer's Hello (or its mod list entry), "" when unknown.</summary>
    public string Version { get; set; } = "";

    /// <summary>Display name (platform name, else the name it sent).</summary>
    public string Name { get; set; } = "";

    /// <summary>Last packet / discovery event for this peer (monotonic ms).</summary>
    public long LastSeenMs { get; set; }

    /// <summary>When we last sent this peer a Hello addressed to it (bounds Hello answers; monotonic ms).</summary>
    public long LastHelloSentMs { get; set; } = long.MinValue / 2;

    /// <summary>Host only: this client's Hello was already forwarded to the other clients (3-4 player discovery).</summary>
    public bool HelloForwarded { get; set; }

    // Hello probing of advertised but not yet confirmed peers.
    public int HelloAttempts { get; set; }
    public long NextHelloAtMs { get; set; }
    public bool GaveUp { get; set; }

    public string Display => Name.Length > 0 ? Name : NetId.ToString();
}
