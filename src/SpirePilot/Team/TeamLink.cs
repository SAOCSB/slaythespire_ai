using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Core.Text;
using SpirePilot.Game;
using SpirePilot.Infra;

namespace SpirePilot.Team;

/// <summary>
/// Co-op team channel between players who run SpirePilot: raw packets over the game's own multiplayer transport,
/// capability discovery (who has the mod / an active AI), host relay, and delivery into <see cref="TeamChatHub"/>.
/// </summary>
/// <remarks>
/// Topology is the game's star: clients only talk to the host, which delivers locally and relays to the addressed
/// linked peers, overwriting <see cref="TeamMessage.From"/> with the transport sender so nobody can spoof.
/// A packet is only ever sent to a peer whose handshake mod list names SpirePilot or that already sent us a valid
/// team packet; a vanilla peer would feed our bytes to its own deserializer. Clients learn about each other through
/// the host: it forwards each newly linked client's Hello to the other linked clients, whose answers travel back the
/// same way.
/// All state changes happen on the main thread: the Harmony hooks only queue work that <see cref="Tick"/> drains.
/// Every game-API call goes through <see cref="TeamNet"/> and is wrapped here (see there why).
/// </remarks>
internal static class TeamLink
{
    /// <summary>
    /// ENet channel for our packets. Vanilla uses channels 0 and 1 (<c>NetTransferModeExtensions.ToChannelId</c>) and
    /// RitsuLib's sidecar uses 48-50, which proves the game's ENet hosts allocate more than 50 channels. 47 collides
    /// with neither, so on ENet our reliable stream is ordered on its own and never waits behind vanilla or RitsuLib
    /// bulk traffic. Steam connections have no channels (the value is ignored there), which is why receivers demux
    /// by the <see cref="TeamWire"/> magic and never by channel.
    /// </summary>
    internal const int Channel = 47;

    private const int MaxQueuedEvents = 512;
    private const int MaxOutbox = 64;
    private const int MaxHelloAttempts = 6;
    /// <summary>Messages accepted per original sender and window (host: per client; client: per relayed origin).</summary>
    private const int InboundBudget = 30;
    private const long InboundWindowMs = 10_000;
    private const long PeerGraceMs = 10_000;
    private const long HelloAnswerWindowMs = 10_000;
    private const long NeverConnectedTimeoutMs = 30_000;
    private const long SlowTickMs = 500;
    private const long PruneEveryMs = 5_000;
    private const long AdoptEveryMs = 1_000;

    // Per-sender token bucket applied in the receive hook before decoding (any thread): a burst of 40 packets, then
    // 4 per second. Far above what SpirePilot sends; it only stops a peer from making us decode a flood.
    private const double PacketBurst = 40;
    private const double PacketsPerSecond = 4;
    private const int MaxPacketBuckets = 64;

    /// <summary>Data work from the receive hooks (bounded; dropped when full).</summary>
    private static readonly ConcurrentQueue<Action> Events = new();
    /// <summary>Session control (session ended, peer left, peer mod lists): rare, small, never dropped.</summary>
    private static readonly ConcurrentQueue<Action> Control = new();
    private static readonly ConcurrentQueue<Outbound> Outbox = new();
    private static readonly ConcurrentDictionary<ulong, PacketBucket> PacketBuckets = new();
    private static readonly object RosterLock = new();
    private static readonly Dictionary<ulong, TeamPeer> Peers = new();
    private static readonly Dictionary<ulong, (long start, int count)> Inbound = new();
    private static readonly HashSet<ulong> LoggedPeerMods = new();
    private static readonly HashSet<ulong> PolledVersionInfo = new();
    private static readonly HashSet<string> BrokenCalls = new();
    private static readonly ConditionalWeakTable<INetGameService, object> DeadServices = new();
    private static readonly object DeadMarker = new();

    private static volatile ulong[] _linked = Array.Empty<ulong>();
    private static volatile INetGameService? _service;
    private static volatile bool _localAi;
    private static int _announcePending;
    private static int _queued;
    private static int _epoch;
    private static ulong _localId;
    /// <summary>Client side: the host's net id, refreshed on the main thread (read by the hub's worker thread).</summary>
    private static ulong _hostId;
    private static ulong _nameResolvedFor;
    private static bool _initialized;
    private static bool _sawConnected;
    private static bool _localModsLogged;
    private static long _observedAtMs;
    private static long _nextSlowTickMs;
    private static long _nextPruneMs;
    private static long _nextAdoptMs;
    private static TeamChatHub? _hub;

    static TeamLink()
    {
        L10n.Add("team.link_up", "{0} 已接入 SpirePilot 队伍频道{1}", "{0} joined the SpirePilot team channel{1}");
        L10n.Add("team.link_up_ai", "（AI 领航中）", " (AI pilot active)");
        L10n.Add("team.link_ai_on", "{0} 的 AI 领航员已开启", "{0}'s AI pilot is now active");
        L10n.Add("team.link_ai_off", "{0} 的 AI 领航员已关闭", "{0}'s AI pilot was turned off");
        L10n.Add("team.link_down", "SpirePilot 队伍频道已断开", "SpirePilot team channel closed");
    }

    private static long NowMs => Environment.TickCount64;

    /// <summary>Transport used by <see cref="TeamChatHub"/>.</summary>
    public static ITeamTransport Transport { get; } = new LinkTransport();

    /// <summary>Installs Harmony patches (packet receive hooks, handshake hooks). Called once at startup.</summary>
    public static void Initialize(Harmony harmony, TeamChatHub hub)
    {
        if (_initialized) return;
        _initialized = true;
        _hub = hub;
        try { SpeechBubbles.Attach(hub); }
        catch (Exception ex) { ModLog.Warn("team link: speech bubbles unavailable: " + ex.Message); }

        TeamPatches.Install(harmony);
        if (!TeamPatches.HostReceive && !TeamPatches.ClientReceive)
            ModLog.Error("team link disabled: no packet receive hook could be installed (nothing will ever be sent)");
        else if (!TeamPatches.ClientDiscovery)
#if STS2_V0107
            // On 0.107 hosts cannot read clients' mod lists, so without this hook a client never makes the first move.
            ModLog.Warn("team link: no safe peer discovery on this game version (InitialGameInfoMessage hook missing); " +
                        "team chat stays local-only when we join someone else's game");
#else
            ModLog.Warn("team link: no client-side discovery hook; as a client we only link when the host announces itself");
#endif
        ModLog.Info($"team link ready: host receive={TeamPatches.HostReceive}, client receive={TeamPatches.ClientReceive}, " +
                    $"client discovery={TeamPatches.ClientDiscovery}, host discovery={TeamPatches.HostDiscovery}, " +
                    $"teammate emotes={TeamPatches.ReactionReceive}, channel {Channel}");
    }

    /// <summary>
    /// Called every frame on the main thread (flush queued inbound messages, hello announcements ...). Each stage is
    /// guarded on its own, so one that breaks (e.g. a game member it needs is gone) does not stop the others.
    /// </summary>
    public static void Tick()
    {
        if (!_initialized) return;
        Stage("control", DrainControl);
        Stage("events", DrainEvents);
        long now = NowMs;
        Stage("session", () => CheckSession(now));
        Stage("outbox", FlushOutbox);
        if (now < _nextSlowTickMs) return;
        _nextSlowTickMs = now + SlowTickMs;

        Stage("identity", () => UpdateLocalIdentity(force: false));
        if (_service == null) return;
        Stage("announce", Announce);
        Stage("hello", () => RetryHellos(now));
        if (now >= _nextPruneMs)
        {
            _nextPruneMs = now + PruneEveryMs;
            Stage("poll", PollHostVersionInfo);
            Stage("prune", () => Prune(now));
        }
    }

    private static void Stage(string name, Action stage)
    {
        try { stage(); }
        catch (Exception ex) { ThrottledLog.Warn("team.tick." + name, $"team link: tick stage '{name}' failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Marks players in the snapshot that have SpirePilot (<see cref="PlayerInfo.HasTeamLink"/>) or an active AI.</summary>
    public static void Annotate(GameSnapshot snap)
    {
        if (snap?.Players == null) return;
        lock (RosterLock)
        {
            foreach (var p in snap.Players)
            {
                if (p.IsLocal)
                {
                    p.HasTeamLink = true;
                    p.IsAiControlled = _localAi;
                }
                else if (Peers.TryGetValue(p.NetId, out var peer) && peer.Confirmed)
                {
                    p.HasTeamLink = true;
                    p.IsAiControlled = peer.IsAi;
                }
                else
                {
                    p.HasTeamLink = false;
                    p.IsAiControlled = false;
                }
            }
        }
    }

    /// <summary>
    /// Whether a teammate who runs SpirePilot would read a message to <paramref name="to"/> (0 = everyone), by the
    /// live roster (<see cref="Annotate"/>'s rule) rather than a possibly older snapshot. Any thread.
    /// </summary>
    public static bool HasReader(ulong to)
    {
        var linked = _linked;
        return to == 0 ? linked.Length > 0 : linked.Contains(to);
    }

    /// <summary>Tells linked teammates whether our AI pilot is active (sent on change and when peers join).</summary>
    public static void SetLocalAiActive(bool active)
    {
        if (_localAi == active) return;
        _localAi = active;
        // Any thread (the pilot loop raises status changes on its worker); the Hello goes out from Tick.
        Interlocked.Exchange(ref _announcePending, 1);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Entry points for the Harmony hooks (any thread; they never throw and only queue work for Tick)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Inspects one inbound packet. Returns false (= skip the game's deserializer) for every packet with our magic,
    /// valid or not, and true for everything else.
    /// </summary>
    internal static bool OnPacket(INetGameService service, ulong senderId, byte[]? packet)
    {
        try
        {
            if (packet == null || packet.Length == 0 || packet[0] != TeamWire.Magic[0]) return true;
            if (!TeamWire.MatchesMagic(packet)) return true;
            // Ours from here on: never hand it to the game. Flood control before the (comparatively) costly decode.
            if (!TakePacketToken(senderId))
            {
                ThrottledLog.Warn("team.flood.packets", $"team link: peer {senderId} sends too many team packets, dropping");
                return false;
            }
            var outcome = TeamWire.TryDecode(packet, out var message);
            if (outcome == TeamWire.ParseOutcome.Ok && message != null)
                Post(() => HandleInbound(service, senderId, message));
            else
                ThrottledLog.Warn("team.rejected", $"team link: dropped a malformed team packet ({packet.Length} bytes) from {senderId}");
            return false;
        }
        catch (Exception)
        {
            try { return packet == null || !TeamWire.MatchesMagic(packet); }
            catch (Exception) { return true; }
        }
    }

    /// <summary>A peer's mod lists became known (handshake / initial game info).</summary>
    internal static void OnPeerVersion(INetGameService service, ulong peerId, IEnumerable<string>? gameplayMods,
        IEnumerable<string>? otherMods, string? gameVersion)
    {
        try
        {
            var gameplay = gameplayMods?.Where(s => s != null).ToArray() ?? Array.Empty<string>();
            var other = otherMods?.Where(s => s != null).ToArray() ?? Array.Empty<string>();
            Control.Enqueue(() => HandlePeerVersion(service, peerId, gameplay, other, gameVersion));
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.version", "team link: could not read a peer's mod list: " + ex.Message);
        }
    }

    /// <summary>The whole network session of <paramref name="service"/> ended (local disconnect / host lost).</summary>
    internal static void OnSessionEnded(INetGameService service, string reason) =>
        Control.Enqueue(() => { if (ReferenceEquals(service, _service)) ResetSession(reason); });

    /// <summary>Host side: one client left.</summary>
    internal static void OnPeerLeft(INetGameService service, ulong peerId) =>
        Control.Enqueue(() => { if (ReferenceEquals(service, _service)) RemovePeer(peerId, "disconnected"); });

    /// <summary>A player's vanilla reaction emote arrived (<paramref name="reaction"/> = the game's reaction type name).</summary>
    internal static void OnReaction(INetGameService? service, ulong senderId, string reaction) =>
        Post(() => HandleReaction(service, senderId, reaction));

    private static void Post(Action work)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedEvents)
        {
            Interlocked.Decrement(ref _queued);
            ThrottledLog.Warn("team.queue", "team link: inbound queue full, dropping events");
            return;
        }
        Events.Enqueue(work);
    }

    private static void DrainControl()
    {
        while (Control.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex) { ThrottledLog.Warn("team.control", "team link: session event failed: " + ex); }
        }
    }

    private static void DrainEvents()
    {
        int budget = 128;
        while (budget-- > 0 && Events.TryDequeue(out var work))
        {
            Interlocked.Decrement(ref _queued);
            try { work(); }
            catch (Exception ex) { ThrottledLog.Warn("team.event", "team link: event failed: " + ex); }
        }
    }

    private sealed class PacketBucket
    {
        public double Tokens = PacketBurst;
        public long LastMs = Environment.TickCount64;
    }

    /// <summary>Any thread. False when <paramref name="sender"/> exceeded its packet rate.</summary>
    private static bool TakePacketToken(ulong sender)
    {
        // Senders come from the transport (bounded by the session's connections); the cap only guards against a leak.
        if (PacketBuckets.Count > MaxPacketBuckets) PacketBuckets.Clear();
        var bucket = PacketBuckets.GetOrAdd(sender, _ => new PacketBucket());
        lock (bucket)
        {
            long now = NowMs;
            bucket.Tokens = Math.Min(PacketBurst, bucket.Tokens + (now - bucket.LastMs) / 1000.0 * PacketsPerSecond);
            bucket.LastMs = now;
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Guarded game calls (see TeamNet: a missing member surfaces at these call sites, never in the game's code)
    // ------------------------------------------------------------------------------------------------------------

    private static T Broken<T>(string what, Exception ex, T fallback)
    {
        bool first;
        lock (BrokenCalls) first = BrokenCalls.Add(what);
        if (first) ModLog.Warn($"team link: game call {what} unavailable ({ex.GetType().Name}: {ex.Message}); that part of the team link is disabled");
        return fallback;
    }

    private static bool IsHost(INetGameService? svc)
    {
        try { return TeamNet.IsHost(svc); }
        catch (Exception ex) { return Broken("IsHost", ex, false); }
    }

    private static bool IsClient(INetGameService? svc)
    {
        try { return TeamNet.IsClient(svc); }
        catch (Exception ex) { return Broken("IsClient", ex, false); }
    }

    private static bool IsNetworked(INetGameService? svc)
    {
        try { return TeamNet.IsNetworked(svc); }
        catch (Exception ex) { return Broken("IsNetworked", ex, false); }
    }

    private static bool SafeConnected(INetGameService? svc)
    {
        try { return TeamNet.IsConnected(svc); }
        catch (Exception ex) { return Broken("IsConnected", ex, false); }
    }

    private static ulong SafeHostId(INetGameService? svc)
    {
        try { return TeamNet.HostId(svc); }
        catch (Exception ex) { return Broken("HostNetId", ex, 0UL); }
    }

    private static string PlayerName(PlatformType platform, ulong id)
    {
        try { return TeamNet.PlayerName(platform, id); }
        catch (Exception ex) { return Broken("GetPlayerName", ex, ""); }
    }

    private static PlatformType CurrentPlatform()
    {
        try { return TeamNet.Platform(_service); }
        catch (Exception ex) { return Broken("Platform", ex, PlatformType.None); }
    }

    private static HashSet<ulong>? ConnectedClientIds(INetGameService? svc)
    {
        try { return TeamNet.HostPeerIds(svc); }
        catch (Exception ex) { return Broken("ConnectedPeerIds", ex, (HashSet<ulong>?)null); }
    }

    private static HashSet<ulong>? RunPlayerIds()
    {
        try { return TeamNet.RunPlayerIds(); }
        catch (Exception ex) { return Broken("RunLobby players", ex, (HashSet<ulong>?)null); }
    }

    private static bool SendToHost(INetGameService? svc, byte[] bytes)
    {
        try { return TeamNet.SendToHost(svc, bytes); }
        catch (Exception ex) { return Broken("SendMessageToHost", ex, false); }
    }

    private static bool SendToClient(INetGameService? svc, ulong peerId, byte[] bytes)
    {
        try { return TeamNet.SendToClient(svc, peerId, bytes); }
        catch (Exception ex) { return Broken("SendMessageToClient", ex, false); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Session tracking (main thread)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>True when our receive hook for the service's role is installed (else we must not announce).</summary>
    private static bool RoleReady(INetGameService? svc) =>
        IsHost(svc) ? TeamPatches.HostReceive : IsClient(svc) && TeamPatches.ClientReceive;

    /// <summary>Adopts the live network service seen by a hook (or RunManager); a different instance is a new session.</summary>
    private static void ObserveService(INetGameService? svc)
    {
        if (svc == null || ReferenceEquals(svc, _service) || !IsNetworked(svc)) return;
        // Late events of a session that already ended must not revive it.
        if (DeadServices.TryGetValue(svc, out _)) return;
        if (_service != null) ResetSession("a new network session started");
        _service = svc;
        Volatile.Write(ref _hostId, SafeHostId(svc));
        _observedAtMs = NowMs;
        _sawConnected = false;
        Interlocked.Increment(ref _epoch);
        UpdateLocalIdentity(force: true);
        LogLocalModsOnce(svc);
        string type;
        try { type = TeamNet.TypeName(svc); }
        catch (Exception ex) { type = Broken("Type", ex, "?"); }
        ModLog.Info($"team link: observing {type} session (local id {_localId})");
    }

    private static void CheckSession(long now)
    {
        var svc = _service;
        if (svc == null)
        {
            // Hooks normally hand us the service; RunManager is the fallback once a run exists.
            if (now < _nextAdoptMs) return;
            _nextAdoptMs = now + AdoptEveryMs;
            INetGameService? rm;
            try { rm = TeamNet.RunNetService(); }
            catch (Exception ex) { rm = Broken("RunManager.NetService", ex, (INetGameService?)null); }
            if (rm != null && IsNetworked(rm) && SafeConnected(rm)) ObserveService(rm);
            return;
        }
        if (SafeConnected(svc))
        {
            _sawConnected = true;
            if (IsClient(svc)) Volatile.Write(ref _hostId, SafeHostId(svc));
            return;
        }
        // IsConnected may only turn true after the join flow; give a fresh service time before calling it dead.
        if (_sawConnected || now - _observedAtMs > NeverConnectedTimeoutMs) ResetSession("connection closed");
    }

    private static void ResetSession(string reason)
    {
        bool hadLinks;
        lock (RosterLock)
        {
            hadLinks = Peers.Values.Any(p => p.Confirmed);
            Peers.Clear();
            _linked = Array.Empty<ulong>();
        }
        LoggedPeerMods.Clear();
        PolledVersionInfo.Clear();
        Inbound.Clear();
        PacketBuckets.Clear();
        if (_service != null) DeadServices.AddOrUpdate(_service, DeadMarker);
        _service = null;
        Volatile.Write(ref _hostId, 0UL);
        _sawConnected = false;
        Interlocked.Increment(ref _epoch);
        while (Outbox.TryDequeue(out _)) { }
        ModLog.Info("team link: session reset (" + reason + ")");
        if (hadLinks) _hub?.AddSystemNote(L10n.T("team.link_down"));
    }

    private static void UpdateLocalIdentity(bool force)
    {
        var hub = _hub;
        if (hub == null) return;
        (ulong id, PlatformType platform) identity;
        try { identity = TeamNet.LocalIdentity(_service); }
        catch (Exception ex) { identity = Broken("local identity", ex, (0UL, PlatformType.None)); }
        var (id, platform) = identity;
        if (id == 0) return;
        _localId = id;
        if (hub.LocalNetId != id) hub.LocalNetId = id;
        if (!force && _nameResolvedFor == id) return;
        _nameResolvedFor = id;
        var name = PlayerName(platform, id);
        if (name.Length > 0) hub.LocalName = name;
    }

    /// <summary>Self-test of the mod list format: our own list must name SpirePilot, or discovery cannot work.</summary>
    private static void LogLocalModsOnce(INetGameService svc)
    {
        if (_localModsLogged) return;
        _localModsLogged = true;
        try
        {
            var (gameplay, other) = TeamNet.LocalModLists(svc);
            var ours = FindOurEntry(gameplay) ?? FindOurEntry(other);
            var line = $"team link: local mod lists gameplay=[{string.Join(", ", gameplay)}] other=[{string.Join(", ", other)}]";
            if (ours != null) ModLog.Info(line + $" (SpirePilot entry \"{ours}\")");
            else ModLog.Warn(line + " — SpirePilot is NOT listed, so peers cannot discover us by mod list (entry format changed?)");
        }
        catch (Exception ex)
        {
            ModLog.Warn("team link: could not read the local mod lists: " + ex.Message);
        }
    }

    /// <summary>
    /// Our entry in a peer's mod list: exactly "SpirePilot", or "SpirePilot-&lt;version&gt;" / "SpirePilot &lt;version&gt;"
    /// (case-insensitive). Other mods whose id merely starts with ours ("SpirePilotTweaks") do not count.
    /// </summary>
    internal static string? FindOurEntry(IEnumerable<string> mods)
    {
        var id = PilotRuntime.ModId;
        return mods.FirstOrDefault(m => m != null &&
                                        (m.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                                         m.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) ||
                                         m.StartsWith(id + " ", StringComparison.OrdinalIgnoreCase)));
    }

    private static string VersionFromEntry(string entry)
    {
        var id = PilotRuntime.ModId;
        return entry.Length > id.Length + 1 && entry[id.Length] is '-' or ' ' ? entry[(id.Length + 1)..].Trim() : "";
    }

    // ------------------------------------------------------------------------------------------------------------
    // Discovery and roster (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static void HandlePeerVersion(INetGameService service, ulong peerId, string[] gameplay, string[] other, string? gameVersion)
    {
        ObserveService(service);
        if (!ReferenceEquals(service, _service)) return;
        // A client's only remote peer is the host.
        if (IsClient(service) && SafeHostId(service) is var hostId && hostId != 0) peerId = hostId;
        if (peerId == 0 || peerId == _localId) return;

        var ours = FindOurEntry(gameplay) ?? FindOurEntry(other);
        if (LoggedPeerMods.Add(peerId))
        {
            ModLog.Info($"team link: peer {peerId} (game {gameVersion ?? "?"}) mods gameplay=[{string.Join(", ", gameplay)}] " +
                        $"other=[{string.Join(", ", other)}] → SpirePilot {(ours != null ? "found (\"" + ours + "\")" : "not found")}");
        }

        long now = NowMs;
        lock (RosterLock)
        {
            Peers.TryGetValue(peerId, out var p);
            if (ours == null)
            {
                // Not advertised. A peer that already sent us valid team packets does run SpirePilot (the list format
                // may differ from what we expect), so keep it; otherwise never send to it.
                if (p is { Confirmed: false }) Peers.Remove(peerId);
                return;
            }
            if (p == null)
            {
                p = new TeamPeer(peerId) { Name = PlayerName(CurrentPlatform(), peerId) };
                Peers[peerId] = p;
            }
            p.LastSeenMs = now;
            if (p.Version.Length == 0) p.Version = VersionFromEntry(ours);
            if (!p.Advertised)
            {
                p.Advertised = true;
                p.GaveUp = false;
                p.HelloAttempts = 0;
                p.NextHelloAtMs = now;
            }
        }
    }

    private static void RebuildLinkedLocked() =>
        _linked = Peers.Values.Where(p => p.Confirmed && p.NetId != _localId).Select(p => p.NetId).ToArray();

    private static void RemovePeer(ulong peerId, string reason)
    {
        TeamPeer? removed;
        lock (RosterLock)
        {
            if (!Peers.Remove(peerId, out removed)) return;
            RebuildLinkedLocked();
        }
        LoggedPeerMods.Remove(peerId);
        PolledVersionInfo.Remove(peerId);
        Inbound.Remove(peerId);
        PacketBuckets.TryRemove(peerId, out _);
        if (removed.Confirmed) ModLog.Info($"team link: {removed.Display} left ({reason})");
    }

    /// <summary>Records a peer proven to run SpirePilot; returns true when it was not linked before.</summary>
    private static bool Confirm(ulong peerId, string sentName, long now, out TeamPeer peer)
    {
        lock (RosterLock)
        {
            if (!Peers.TryGetValue(peerId, out var existing))
            {
                existing = new TeamPeer(peerId);
                Peers[peerId] = existing;
            }
            peer = existing;
            bool newly = !peer.Confirmed;
            peer.Confirmed = true;
            peer.LastSeenMs = now;
            if (peer.Name.Length == 0)
            {
                var platformName = PlayerName(CurrentPlatform(), peerId);
                peer.Name = platformName.Length > 0 ? platformName : sentName;
            }
            if (newly) RebuildLinkedLocked();
            return newly;
        }
    }

    /// <summary>
    /// 0.111 host fallback when the handshake postfix is missing: ask the host service for each client's mod list.
    /// </summary>
    private static void PollHostVersionInfo()
    {
#if !STS2_V0107
        var svc = _service;
        if (TeamPatches.HostDiscovery || !IsHost(svc)) return;
        foreach (var id in ConnectedClientIds(svc) ?? new HashSet<ulong>())
        {
            if (!PolledVersionInfo.Add(id)) continue;
            lock (RosterLock) if (Peers.ContainsKey(id)) continue;
            try
            {
                var info = TeamNet.HostVersionInfo(svc, id);
                if (info is { } v) HandlePeerVersion(svc!, id, v.gameplay, v.other, v.version);
                else PolledVersionInfo.Remove(id); // handshake not finished yet, ask again later
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn("team.poll", "team link: GetVersionInfoForPeer failed: " + ex.Message);
            }
        }
#endif
    }

    /// <summary>Drops peers that are no longer part of the session (missed disconnect events, relayed clients).</summary>
    private static void Prune(long now)
    {
        var svc = _service;
        HashSet<ulong>? present = null;
        ulong keep = 0;
        if (IsHost(svc))
        {
            // Readable and empty means every client left: prune them all (after the grace period).
            present = ConnectedClientIds(svc);
            if (present == null) return;
        }
        else if (IsClient(svc))
        {
            // The run's player list is empty / unreadable before a run exists: not a reason to drop anyone.
            present = RunPlayerIds();
            keep = SafeHostId(svc);
            if (present == null || present.Count == 0) return;
        }
        else
        {
            return;
        }
        List<ulong> gone;
        lock (RosterLock)
        {
            gone = Peers.Values
                .Where(p => p.NetId != keep && !present.Contains(p.NetId) && now - p.LastSeenMs > PeerGraceMs)
                .Select(p => p.NetId).ToList();
        }
        foreach (var id in gone) RemovePeer(id, "no longer in the session");
    }

    private static void RetryHellos(long now)
    {
        List<ulong> due = new();
        lock (RosterLock)
        {
            foreach (var p in Peers.Values)
                if (p.Advertised && !p.Confirmed && !p.GaveUp && now >= p.NextHelloAtMs) due.Add(p.NetId);
        }
        foreach (var id in due)
        {
            bool sent = SendHello(id, allowAdvertised: true);
            lock (RosterLock)
            {
                if (!Peers.TryGetValue(id, out var p)) continue;
                if (sent) p.HelloAttempts++;
                p.NextHelloAtMs = now + 1000L * (1 << Math.Min(p.HelloAttempts, 5));
                if (p.HelloAttempts < MaxHelloAttempts) continue;
                p.GaveUp = true;
            }
            ModLog.Warn($"team link: peer {id} lists SpirePilot but never answered our Hello (old version or its hooks failed?)");
        }
    }

    /// <summary>
    /// Sends a pending "AI on/off" announcement to everyone. Stays pending until a send actually went out (nobody may
    /// be linked yet); a change raised meanwhile re-arms it.
    /// </summary>
    private static void Announce()
    {
        if (Interlocked.Exchange(ref _announcePending, 0) != 1) return;
        bool sent = false;
        try { sent = Route(0, HelloPacket(0), allowAdvertised: false); }
        finally
        {
            if (!sent) Interlocked.Exchange(ref _announcePending, 1);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Inbound processing and relay (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static void HandleInbound(INetGameService service, ulong transportSender, TeamMessage message)
    {
        ObserveService(service);
        if (!ReferenceEquals(service, _service) || _hub == null) return;
        long now = NowMs;
        if (IsHost(service))
        {
            if (transportSender == 0 || transportSender == _localId) return;
            // Anti-spoof: only the transport knows who really sent it. The display name is re-derived too.
            message.From = transportSender;
            var platformName = PlayerName(CurrentPlatform(), transportSender);
            if (platformName.Length > 0) message.FromName = platformName;
            if (!WithinInboundBudget(transportSender, now))
            {
                ThrottledLog.Warn("team.flood", $"team link: peer {transportSender} sends too many team messages, dropping");
                return;
            }
            Process(message, now);
            Relay(message, transportSender);
        }
        else if (IsClient(service))
        {
            // A client has exactly one connection, so everything comes from (or through) the host, which already
            // overwrote From with the real origin.
            ulong host = SafeHostId(service);
            if (host == 0) host = transportSender;
            if (transportSender != 0 && transportSender != host)
                ThrottledLog.Warn("team.sender", $"team link: client got a packet from {transportSender}, host is {host}");
            if (message.From == 0) message.From = host;
            if (message.From == _localId) return;
            // Relayed traffic is limited per original sender too (the host's budget is per client, not per origin).
            if (!WithinInboundBudget(message.From, now))
            {
                ThrottledLog.Warn("team.flood.relayed", $"team link: too many team messages from {message.From}, dropping");
                return;
            }
            // A relayed message also proves that the host runs SpirePilot.
            if (message.From != host && host != 0 && host != _localId && Confirm(host, "", now, out _))
                OnNewlyLinked(host, null);
            Process(message, now);
        }
    }

    /// <summary>
    /// Emotes are all a teammate without SpirePilot can say to the AI: they go to the inbox as messages (see
    /// <see cref="TeamEmotes"/>). Our own are skipped, and so are those of a linked peer whose AI is active (its AI
    /// signals to players who cannot read it). A linked peer's "!" or "?" still asks a teammate without the mod
    /// something: a thumbs up after it answers that, never an older request of ours. A linked peer who plays by hand, or
    /// whose player is an external agent, can answer us in text: in a party with a teammate without the mod its emotes
    /// are most likely aimed at that one (the Team tab's and POST /chat's fallback does exactly that), so they are
    /// context only, never an answer or a reason to wake the AI. With everyone linked they can only be meant for the
    /// linked players, and a thumbs up answers our newest open request it read.
    /// </summary>
    private static void HandleReaction(INetGameService? service, ulong senderId, string reaction)
    {
        var hub = _hub;
        if (hub == null || senderId == 0) return;
        (ulong id, PlatformType platform) local;
        try { local = TeamNet.LocalIdentity(service); }
        catch (Exception ex) { local = Broken("local identity", ex, (0UL, PlatformType.None)); }
        if (senderId == local.id || senderId == _localId || senderId == hub.LocalNetId) return;
        // The party as last captured, read outside the roster lock (the capture takes its own); unknown counts as mixed.
        var mates = PilotRuntime.Bridge?.Latest?.Teammates.Select(p => p.NetId).ToList();
        bool linked, ai, forOthers = false;
        lock (RosterLock)
        {
            linked = Peers.TryGetValue(senderId, out var peer) && peer.Confirmed;
            ai = linked && peer!.IsAi;
            if (linked && !ai)
                forOthers = mates is not { Count: > 0 } ||
                            mates.Any(id => id != senderId && !(Peers.TryGetValue(id, out var mate) && mate.Confirmed));
        }
        var memory = PilotRuntime.Engine.Memory;
        // Outside the roster lock: the memory takes its own.
        if (linked && TeamEmotes.Asks(reaction)) memory.NoteSignal(TeamMessage.NewId(), 0);
        if (ai) return;
        var name = PlayerName(local.platform, senderId);
        var asked = forOthers ? null : linked ? memory.LastOpenTo(senderId) : memory.LastSignalTo(senderId);
        hub.Receive(TeamEmotes.ToMessage(reaction, senderId, name, hub.LocalNetId, asked, fromLinkedPeer: forOthers));
    }

    private static bool WithinInboundBudget(ulong sender, long now)
    {
        if (!Inbound.TryGetValue(sender, out var w) || now - w.start > InboundWindowMs) w = (now, 0);
        w.count++;
        Inbound[sender] = w;
        return w.count <= InboundBudget;
    }

    /// <summary>Roster update + local delivery of one message whose <see cref="TeamMessage.From"/> is trusted.</summary>
    private static void Process(TeamMessage m, long now)
    {
        if (m.From == 0 || m.From == _localId) return;
        bool newly = Confirm(m.From, m.FromName, now, out var peer);
        if (m.Kind == TeamMessageKind.Hello)
        {
            ParseHello(m.Text, out var version, out var ai);
            bool aiChanged, answer, forward;
            string name;
            lock (RosterLock)
            {
                aiChanged = peer.IsAi != ai;
                peer.IsAi = ai;
                if (version.Length > 0) peer.Version = version;
                name = peer.Display;
                // A Hello addressed to us from a peer we have not greeted lately is a probe (it does not know us yet,
                // e.g. our first answer was lost): answer it. Our own answers are recent, so this never ping-pongs.
                answer = !newly && m.To == _localId && now - peer.LastHelloSentMs > HelloAnswerWindowMs;
                // Host: the other clients learn about this one from a copy of its first Hello. A Hello for everyone
                // is relayed anyway.
                forward = IsHost(_service) && !peer.HelloForwarded;
                peer.HelloForwarded = true;
                if (m.To == 0) forward = false;
            }
            if (newly) OnNewlyLinked(m.From, version);
            else
            {
                if (aiChanged) _hub?.AddSystemNote(L10n.T(ai ? "team.link_ai_on" : "team.link_ai_off", name));
                if (answer) SendHello(m.From, allowAdvertised: false);
            }
            if (forward) ForwardHello(m);
            return; // Hellos are link plumbing, not chat
        }
        if (newly) OnNewlyLinked(m.From, null);
        if (m.To == 0 || m.To == _localId) _hub?.Receive(m);
    }

    /// <summary>First contact with a peer in this session: answer with our Hello so it learns about us too.</summary>
    private static void OnNewlyLinked(ulong peerId, string? version)
    {
        string name;
        bool ai;
        lock (RosterLock)
        {
            if (!Peers.TryGetValue(peerId, out var p)) return;
            name = p.Display;
            ai = p.IsAi;
            if (string.IsNullOrEmpty(version)) version = p.Version;
        }
        ModLog.Info($"team link: linked with {name} ({peerId}), SpirePilot {(string.IsNullOrEmpty(version) ? "?" : version)}, ai={ai}");
        if (!string.IsNullOrEmpty(version) && version != PilotRuntime.Version)
            ModLog.Warn($"team link: {name} runs SpirePilot {version}, we run {PilotRuntime.Version}");
        _hub?.AddSystemNote(L10n.T("team.link_up", name, ai ? L10n.T("team.link_up_ai") : ""));
        SendHello(peerId, allowAdvertised: false);
    }

    private static void ParseHello(string text, out string version, out bool ai)
    {
        version = "";
        ai = false;
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.Split('|');
        version = parts[0].Trim();
        if (version.Length > 32) version = version[..32];
        foreach (var part in parts.Skip(1))
        {
            var kv = part.Trim();
            if (kv.StartsWith("ai=", StringComparison.OrdinalIgnoreCase)) ai = kv[3..].Trim() is "1" or "true";
        }
    }

    /// <summary>Host only: forwards a client's message to the addressed linked peers (never back to its source).</summary>
    private static void Relay(TeamMessage m, ulong source)
    {
        var svc = _service;
        if (!IsHost(svc) || m.To == _localId || m.To == source) return;
        byte[] bytes;
        try { bytes = TeamWire.Encode(m); }
        catch (Exception) { return; }
        if (m.To != 0)
        {
            // Clients address teammates they may not know yet; only the host knows who can receive.
            if (CanSendTo(m.To, allowAdvertised: false)) SendToClient(svc, m.To, bytes);
            return;
        }
        foreach (var id in _linked)
            if (id != source) SendToClient(svc, id, bytes);
    }

    /// <summary>
    /// Host only: introduces a newly linked client to the other linked clients by forwarding a copy of its Hello to
    /// each of them, addressed to that client. Addressed, it is a probe even for a client that still lists the newcomer
    /// from before it left and rejoined: every client answers it (through us), so the newcomer learns about them as
    /// well. The answers are addressed to the newcomer, which answers back only when it has not greeted that client
    /// lately (it just did), so this never ping-pongs.
    /// </summary>
    private static void ForwardHello(TeamMessage hello)
    {
        var svc = _service;
        if (!IsHost(svc)) return;
        foreach (var id in _linked)
        {
            if (id == hello.From) continue;
            byte[] bytes;
            try
            {
                bytes = TeamWire.Encode(new TeamMessage
                {
                    Kind = TeamMessageKind.Hello,
                    From = hello.From,
                    To = id,
                    FromName = hello.FromName,
                    Text = hello.Text,
                    Time = hello.Time,
                });
            }
            catch (Exception) { return; }
            SendToClient(svc, id, bytes);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Outbound (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static byte[] HelloPacket(ulong to) => TeamWire.Encode(new TeamMessage
    {
        Kind = TeamMessageKind.Hello,
        From = _localId,
        To = to,
        FromName = _hub?.LocalName ?? "",
        Text = $"{PilotRuntime.Version}|ai={(_localAi ? 1 : 0)}",
        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });

    /// <summary>Sends our Hello addressed to one peer and remembers when.</summary>
    private static bool SendHello(ulong peerId, bool allowAdvertised)
    {
        bool sent = Route(peerId, HelloPacket(peerId), allowAdvertised);
        if (sent)
        {
            lock (RosterLock)
                if (Peers.TryGetValue(peerId, out var p)) p.LastHelloSentMs = NowMs;
        }
        return sent;
    }

    private static bool CanSendTo(ulong peerId, bool allowAdvertised)
    {
        if (peerId == 0) return false;
        lock (RosterLock)
            return Peers.TryGetValue(peerId, out var p) && (p.Confirmed || allowAdvertised && p.Advertised);
    }

    /// <summary>
    /// Sends one encoded packet towards <paramref name="to"/> (0 = every linked peer). Clients always send to the
    /// host, which relays (and checks whether <paramref name="to"/> can receive). <paramref name="allowAdvertised"/>
    /// lets Hello probes reach peers known only from their mod list. Returns true when at least one send was handed
    /// to the transport.
    /// </summary>
    private static bool Route(ulong to, byte[] bytes, bool allowAdvertised)
    {
        var svc = _service;
        if (!RoleReady(svc)) return false;
        if (IsClient(svc))
        {
            ulong host = SafeHostId(svc);
            return CanSendTo(host, allowAdvertised) && SendToHost(svc, bytes);
        }
        if (IsHost(svc))
        {
            if (to != 0) return CanSendTo(to, allowAdvertised) && SendToClient(svc, to, bytes);
            bool any = false;
            foreach (var id in _linked) any |= SendToClient(svc, id, bytes);
            return any;
        }
        return false;
    }

    private static void FlushOutbox()
    {
        int budget = 32;
        int epoch = Volatile.Read(ref _epoch);
        while (budget-- > 0 && Outbox.TryDequeue(out var o))
        {
            if (o.Epoch != epoch) continue;
            if (!Route(o.To, o.Bytes, allowAdvertised: false))
                ThrottledLog.Warn("team.outbox", "team link: a queued team message could not be sent (peer left?)");
        }
    }

    private static bool IsLinkUp => _initialized && RoleReady(_service) && _linked.Length > 0;

    /// <summary>
    /// Whether a message to <paramref name="to"/> can go out: a linked peer, or (as a client with a linked host) any
    /// other player, because the host relays and knows who can receive.
    /// </summary>
    private static bool CanAddress(ulong to)
    {
        if (to == 0) return true;
        if (to == _localId) return false;
        var linked = _linked;
        if (linked.Contains(to)) return true;
        ulong host = Volatile.Read(ref _hostId);
        return IsClient(_service) && host != 0 && linked.Contains(host);
    }

    /// <summary>
    /// <see cref="ITeamTransport.Send"/>: on the main thread the packet goes out immediately; from other threads (the
    /// pilot loop) it is queued for the next frame and the result reflects the roster check, which is what decides
    /// whether anyone can receive it.
    /// </summary>
    private static bool SendFromHub(TeamMessage message)
    {
        try
        {
            if (message == null || !IsLinkUp) return false;
            if (!CanAddress(message.To)) return false;
            if (message.From == 0) message.From = _localId;
            var bytes = TeamWire.Encode(message); // throws when the payload exceeds TeamWire.MaxPayloadBytes
            if (MainThread.IsMainThread) return Route(message.To, bytes, allowAdvertised: false);
            if (Outbox.Count >= MaxOutbox) return false;
            Outbox.Enqueue(new Outbound(message.To, bytes, Volatile.Read(ref _epoch)));
            return true;
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.send", "team link: message not sent: " + ex.Message);
            return false;
        }
    }

    private readonly record struct Outbound(ulong To, byte[] Bytes, int Epoch);

    private sealed class LinkTransport : ITeamTransport
    {
        public bool IsAvailable => IsLinkUp;
        public IReadOnlyCollection<ulong> LinkedPeers => _linked;
        public bool Send(TeamMessage message) => SendFromHub(message);
    }
}
