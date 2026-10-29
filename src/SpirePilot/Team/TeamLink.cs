using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
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
/// team packet; a vanilla peer would feed our bytes to its own deserializer.
/// All state changes happen on the main thread: the Harmony hooks only queue work that <see cref="Tick"/> drains.
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
    private const int RelayBudget = 30;
    private static readonly TimeSpan RelayWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HelloAnswerWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NeverConnectedTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentQueue<Action> Events = new();
    private static readonly ConcurrentQueue<Outbound> Outbox = new();
    private static readonly object RosterLock = new();
    private static readonly Dictionary<ulong, TeamPeer> Peers = new();
    private static readonly HashSet<ulong> LoggedPeerMods = new();
    private static readonly HashSet<ulong> PolledVersionInfo = new();
    private static readonly ConditionalWeakTable<INetGameService, object> DeadServices = new();
    private static readonly object DeadMarker = new();

    private static volatile ulong[] _linked = Array.Empty<ulong>();
    private static volatile INetGameService? _service;
    private static volatile bool _localAi;
    private static int _announcePending;
    private static int _queued;
    private static int _epoch;
    private static ulong _localId;
    private static ulong _nameResolvedFor;
    private static bool _initialized;
    private static bool _sawConnected;
    private static bool _localModsLogged;
    private static DateTime _observedAt;
    private static DateTime _nextSlowTick;
    private static DateTime _nextPrune;
    private static DateTime _nextAdopt;
    private static TeamChatHub? _hub;

    static TeamLink()
    {
        L10n.Add("team.link_up", "{0} 已接入 SpirePilot 队伍频道{1}", "{0} joined the SpirePilot team channel{1}");
        L10n.Add("team.link_up_ai", "（AI 领航中）", " (AI pilot active)");
        L10n.Add("team.link_ai_on", "{0} 的 AI 领航员已开启", "{0}'s AI pilot is now active");
        L10n.Add("team.link_ai_off", "{0} 的 AI 领航员已关闭", "{0}'s AI pilot was turned off");
        L10n.Add("team.link_down", "SpirePilot 队伍频道已断开", "SpirePilot team channel closed");
    }

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
                    $"client discovery={TeamPatches.ClientDiscovery}, host discovery={TeamPatches.HostDiscovery}, channel {Channel}");
    }

    /// <summary>Called every frame on the main thread (flush queued inbound messages, hello announcements ...).</summary>
    public static void Tick()
    {
        if (!_initialized) return;
        DrainEvents();
        var now = DateTime.UtcNow;
        CheckSession(now);
        FlushOutbox();
        if (now < _nextSlowTick) return;
        _nextSlowTick = now + TimeSpan.FromMilliseconds(500);

        UpdateLocalIdentity(force: false);
        if (_service == null) return;
        if (Interlocked.Exchange(ref _announcePending, 0) == 1) Route(0, HelloPacket(0), allowAdvertised: false);
        RetryHellos(now);
        if (now >= _nextPrune)
        {
            _nextPrune = now + TimeSpan.FromSeconds(5);
            PollHostVersionInfo();
            Prune(now);
        }
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
            var outcome = TeamWire.TryDecode(packet, out var message);
            if (outcome == TeamWire.ParseOutcome.NotOurs) return true;
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
            Post(() => HandlePeerVersion(service, peerId, gameplay, other, gameVersion));
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.version", "team link: could not read a peer's mod list: " + ex.Message);
        }
    }

    /// <summary>The whole network session of <paramref name="service"/> ended (local disconnect / host lost).</summary>
    internal static void OnSessionEnded(INetGameService service, string reason) =>
        Post(() => { if (ReferenceEquals(service, _service)) ResetSession(reason); });

    /// <summary>Host side: one client left.</summary>
    internal static void OnPeerLeft(INetGameService service, ulong peerId) =>
        Post(() => { if (ReferenceEquals(service, _service)) RemovePeer(peerId, "disconnected"); });

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

    // ------------------------------------------------------------------------------------------------------------
    // Session tracking (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static bool IsNetworked(INetGameService? svc)
    {
        if (svc is not (NetHostGameService or NetClientGameService)) return false;
        try { return svc.Type is NetGameType.Host or NetGameType.Client; }
        catch (Exception) { return false; }
    }

    /// <summary>True when our receive hook for the service's role is installed (else we must not announce).</summary>
    private static bool RoleReady(INetGameService? svc) => svc switch
    {
        NetHostGameService => TeamPatches.HostReceive,
        NetClientGameService => TeamPatches.ClientReceive,
        _ => false,
    };

    private static bool SafeConnected(INetGameService svc)
    {
        try { return svc.IsConnected; }
        catch (Exception) { return false; }
    }

    private static ulong SafeHostId(NetClientGameService client)
    {
        try { return client.HostNetId; }
        catch (Exception) { return 0; }
    }

    /// <summary>Adopts the live network service seen by a hook (or RunManager); a different instance is a new session.</summary>
    private static void ObserveService(INetGameService? svc)
    {
        if (svc == null || ReferenceEquals(svc, _service) || !IsNetworked(svc)) return;
        // Late events of a session that already ended must not revive it.
        if (DeadServices.TryGetValue(svc, out _)) return;
        if (_service != null) ResetSession("a new network session started");
        _service = svc;
        _observedAt = DateTime.UtcNow;
        _sawConnected = false;
        Interlocked.Increment(ref _epoch);
        UpdateLocalIdentity(force: true);
        LogLocalModsOnce(svc);
        string type;
        try { type = svc.Type.ToString(); } catch (Exception) { type = "?"; }
        ModLog.Info($"team link: observing {type} session (local id {_localId})");
    }

    private static void CheckSession(DateTime now)
    {
        var svc = _service;
        if (svc == null)
        {
            // Hooks normally hand us the service; RunManager is the fallback once a run exists.
            if (now < _nextAdopt) return;
            _nextAdopt = now + TimeSpan.FromSeconds(1);
            INetGameService? rm = null;
            try { rm = RunManager.Instance?.NetService; }
            catch (Exception) { /* run not set up */ }
            if (rm != null && IsNetworked(rm) && SafeConnected(rm)) ObserveService(rm);
            return;
        }
        if (SafeConnected(svc))
        {
            _sawConnected = true;
            return;
        }
        // IsConnected may only turn true after the join flow; give a fresh service time before calling it dead.
        if (_sawConnected || now - _observedAt > NeverConnectedTimeout) ResetSession("connection closed");
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
        if (_service != null) DeadServices.AddOrUpdate(_service, DeadMarker);
        _service = null;
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
        ulong id = 0;
        var platform = PlatformType.None;
        try
        {
            var svc = _service ?? RunManager.Instance?.NetService;
            if (svc != null)
            {
                id = svc.NetId;
                platform = svc.Platform;
            }
        }
        catch (Exception) { /* no service yet */ }
        if (id == 0)
        {
            try { id = LocalContext.NetId ?? 0; }
            catch (Exception) { /* not set */ }
        }
        if (id == 0) return;
        _localId = id;
        if (hub.LocalNetId != id) hub.LocalNetId = id;
        if (!force && _nameResolvedFor == id) return;
        _nameResolvedFor = id;
        var name = PlayerName(platform, id);
        if (name.Length > 0) hub.LocalName = name;
    }

    private static string PlayerName(PlatformType platform, ulong id)
    {
        try { return PlatformUtil.GetPlayerName(platform, id) ?? ""; }
        catch (Exception) { return ""; }
    }

    private static PlatformType CurrentPlatform()
    {
        try { return _service?.Platform ?? PlatformType.None; }
        catch (Exception) { return PlatformType.None; }
    }

    /// <summary>Self-test of the mod list format: our own list must name SpirePilot, or discovery cannot work.</summary>
    private static void LogLocalModsOnce(INetGameService svc)
    {
        if (_localModsLogged) return;
        _localModsLogged = true;
        try
        {
#if STS2_V0107
            // 0.107 sends these lists in InitialGameInfoMessage (assumed to be built from the mod manager).
            List<string> gameplay = ModManager.GetGameplayRelevantModNameList() ?? new List<string>();
            List<string> other = ModManager.GetNonGameplayRelevantModNameList() ?? new List<string>();
#else
            // What this game sends in its handshake.
            var local = svc.LocalVersion;
            List<string> gameplay = local.gameplayAffectingMods ?? new List<string>();
            List<string> other = local.otherMods ?? new List<string>();
#endif
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

    /// <summary>Mod list entries are probably "id-version"; anything starting with our id counts (also a bare id).</summary>
    private static string? FindOurEntry(IEnumerable<string> mods) =>
        mods.FirstOrDefault(m => m != null && m.StartsWith(PilotRuntime.ModId, StringComparison.Ordinal));

    private static string VersionFromEntry(string entry)
    {
        var id = PilotRuntime.ModId;
        return entry.Length > id.Length + 1 && entry[id.Length] == '-' ? entry[(id.Length + 1)..] : "";
    }

    // ------------------------------------------------------------------------------------------------------------
    // Discovery and roster (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static void HandlePeerVersion(INetGameService service, ulong peerId, string[] gameplay, string[] other, string? gameVersion)
    {
        ObserveService(service);
        if (!ReferenceEquals(service, _service)) return;
        // A client's only remote peer is the host.
        if (service is NetClientGameService client && SafeHostId(client) is var hostId && hostId != 0) peerId = hostId;
        if (peerId == 0 || peerId == _localId) return;

        var ours = FindOurEntry(gameplay) ?? FindOurEntry(other);
        if (LoggedPeerMods.Add(peerId))
        {
            ModLog.Info($"team link: peer {peerId} (game {gameVersion ?? "?"}) mods gameplay=[{string.Join(", ", gameplay)}] " +
                        $"other=[{string.Join(", ", other)}] → SpirePilot {(ours != null ? "found (\"" + ours + "\")" : "not found")}");
        }

        var now = DateTime.UtcNow;
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
            p.LastSeen = now;
            if (p.Version.Length == 0) p.Version = VersionFromEntry(ours);
            if (!p.Advertised)
            {
                p.Advertised = true;
                p.GaveUp = false;
                p.HelloAttempts = 0;
                p.NextHelloAt = now;
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
        if (removed.Confirmed) ModLog.Info($"team link: {removed.Display} left ({reason})");
    }

    /// <summary>Records a peer proven to run SpirePilot; returns true when it was not linked before.</summary>
    private static bool Confirm(ulong peerId, string sentName, DateTime now, out TeamPeer peer)
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
            peer.LastSeen = now;
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
        if (TeamPatches.HostDiscovery || _service is not NetHostGameService host) return;
        foreach (var id in ConnectedClientIds(host) ?? new HashSet<ulong>())
        {
            if (!PolledVersionInfo.Add(id)) continue;
            lock (RosterLock) if (Peers.ContainsKey(id)) continue;
            try
            {
                var info = host.GetVersionInfoForPeer(id);
                if (info.HasValue) HandlePeerVersion(host, id, info.Value.gameplayAffectingMods?.ToArray() ?? Array.Empty<string>(),
                    info.Value.otherMods?.ToArray() ?? Array.Empty<string>(), info.Value.version);
                else PolledVersionInfo.Remove(id); // handshake not finished yet, ask again later
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn("team.poll", "team link: GetVersionInfoForPeer failed: " + ex.Message);
            }
        }
#endif
    }

    private static HashSet<ulong>? ConnectedClientIds(NetHostGameService host)
    {
        try { return host.NetHost?.ConnectedPeerIds?.ToHashSet(); }
        catch (Exception) { return null; }
    }

    /// <summary>Connected players of the current run (client side), or null when not readable.</summary>
    private static HashSet<ulong>? RunPlayerIds()
    {
        try
        {
            var lobby = RunManager.Instance?.RunLobby;
            if (lobby == null) return null;
#if STS2_V0107
            return lobby.ConnectedPlayerIds?.ToHashSet();
#else
            return lobby.PlayerIds?.ToHashSet();
#endif
        }
        catch (Exception) { return null; }
    }

    /// <summary>Drops peers that are no longer part of the session (missed disconnect events, relayed clients).</summary>
    private static void Prune(DateTime now)
    {
        HashSet<ulong>? present = null;
        ulong keep = 0;
        switch (_service)
        {
            case NetHostGameService host:
                present = ConnectedClientIds(host);
                break;
            case NetClientGameService client:
                present = RunPlayerIds();
                keep = SafeHostId(client);
                break;
        }
        if (present == null || present.Count == 0) return;
        List<ulong> gone;
        lock (RosterLock)
        {
            gone = Peers.Values
                .Where(p => p.NetId != keep && !present.Contains(p.NetId) && now - p.LastSeen > PeerGrace)
                .Select(p => p.NetId).ToList();
        }
        foreach (var id in gone) RemovePeer(id, "no longer in the session");
    }

    private static void RetryHellos(DateTime now)
    {
        List<ulong> due = new();
        lock (RosterLock)
        {
            foreach (var p in Peers.Values)
                if (p.Advertised && !p.Confirmed && !p.GaveUp && now >= p.NextHelloAt) due.Add(p.NetId);
        }
        foreach (var id in due)
        {
            bool sent = SendHello(id, allowAdvertised: true);
            lock (RosterLock)
            {
                if (!Peers.TryGetValue(id, out var p)) continue;
                if (sent) p.HelloAttempts++;
                p.NextHelloAt = now + TimeSpan.FromSeconds(1 << Math.Min(p.HelloAttempts, 5));
                if (p.HelloAttempts < MaxHelloAttempts) continue;
                p.GaveUp = true;
            }
            ModLog.Warn($"team link: peer {id} lists SpirePilot but never answered our Hello (old version or its hooks failed?)");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Inbound processing and relay (main thread)
    // ------------------------------------------------------------------------------------------------------------

    private static void HandleInbound(INetGameService service, ulong transportSender, TeamMessage message)
    {
        ObserveService(service);
        if (!ReferenceEquals(service, _service) || _hub == null) return;
        var now = DateTime.UtcNow;
        switch (service)
        {
            case NetHostGameService:
                if (transportSender == 0 || transportSender == _localId) return;
                // Anti-spoof: only the transport knows who really sent it. The display name is re-derived too.
                message.From = transportSender;
                var platformName = PlayerName(CurrentPlatform(), transportSender);
                if (platformName.Length > 0) message.FromName = platformName;
                if (!WithinRelayBudget(transportSender, now))
                {
                    ThrottledLog.Warn("team.flood", $"team link: peer {transportSender} sends too many team messages, dropping");
                    return;
                }
                Process(message, now);
                Relay(message, transportSender);
                break;

            case NetClientGameService client:
                // A client has exactly one connection, so everything comes from (or through) the host, which already
                // overwrote From with the real origin.
                ulong host = SafeHostId(client);
                if (host == 0) host = transportSender;
                if (transportSender != 0 && transportSender != host)
                    ThrottledLog.Warn("team.sender", $"team link: client got a packet from {transportSender}, host is {host}");
                if (message.From == 0) message.From = host;
                if (message.From == _localId) return;
                // A relayed message also proves that the host runs SpirePilot.
                if (message.From != host && host != 0 && host != _localId && Confirm(host, "", now, out _))
                    OnNewlyLinked(host, null);
                Process(message, now);
                break;
        }
    }

    private static bool WithinRelayBudget(ulong sender, DateTime now)
    {
        lock (RosterLock)
        {
            if (!Peers.TryGetValue(sender, out var p)) return true;
            if (now - p.RelayWindowStart > RelayWindow)
            {
                p.RelayWindowStart = now;
                p.RelayCount = 0;
            }
            return ++p.RelayCount <= RelayBudget;
        }
    }

    /// <summary>Roster update + local delivery of one message whose <see cref="TeamMessage.From"/> is trusted.</summary>
    private static void Process(TeamMessage m, DateTime now)
    {
        if (m.From == 0 || m.From == _localId) return;
        bool newly = Confirm(m.From, m.FromName, now, out var peer);
        if (m.Kind == TeamMessageKind.Hello)
        {
            ParseHello(m.Text, out var version, out var ai);
            bool aiChanged, answer;
            string name;
            lock (RosterLock)
            {
                aiChanged = peer.IsAi != ai;
                peer.IsAi = ai;
                if (version.Length > 0) peer.Version = version;
                name = peer.Display;
                // A Hello addressed to us from a peer we have not greeted lately is a probe (it does not know us yet,
                // e.g. our first answer was lost): answer it. Our own answers are recent, so this never ping-pongs.
                answer = !newly && m.To == _localId && now - peer.LastHelloSent > HelloAnswerWindow;
            }
            if (newly) OnNewlyLinked(m.From, version);
            else
            {
                if (aiChanged) _hub?.AddSystemNote(L10n.T(ai ? "team.link_ai_on" : "team.link_ai_off", name));
                if (answer) SendHello(m.From, allowAdvertised: false);
            }
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
        if (_service is not NetHostGameService host || m.To == _localId || m.To == source) return;
        byte[] bytes;
        try { bytes = TeamWire.Encode(m); }
        catch (Exception) { return; }
        if (m.To != 0)
        {
            if (CanSendTo(m.To, allowAdvertised: false)) SendToClient(host, m.To, bytes);
            return;
        }
        foreach (var id in _linked)
            if (id != source) SendToClient(host, id, bytes);
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
                if (Peers.TryGetValue(peerId, out var p)) p.LastHelloSent = DateTime.UtcNow;
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
    /// host, which relays. <paramref name="allowAdvertised"/> lets Hello probes reach peers known only from their mod
    /// list. Returns true when at least one send was handed to the transport.
    /// </summary>
    private static bool Route(ulong to, byte[] bytes, bool allowAdvertised)
    {
        var svc = _service;
        if (!RoleReady(svc)) return false;
        switch (svc)
        {
            case NetClientGameService client:
            {
                ulong host = SafeHostId(client);
                return CanSendTo(host, allowAdvertised) && SendToHost(client, bytes);
            }
            case NetHostGameService host:
            {
                if (to != 0) return CanSendTo(to, allowAdvertised) && SendToClient(host, to, bytes);
                bool any = false;
                foreach (var id in _linked) any |= SendToClient(host, id, bytes);
                return any;
            }
            default:
                return false;
        }
    }

    // Send pattern adapted from RitsuLib's RitsuLibSidecarSend (MIT, BAKAOLC/STS2-RitsuLib): connected check,
    // reliable mode, own channel, and a failing transport (peer gone / not started) is a soft failure.
    private static bool SendToHost(NetClientGameService client, byte[] bytes)
    {
        try
        {
            if (!client.IsConnected || client.NetClient == null) return false;
            client.NetClient.SendMessageToHost(bytes, bytes.Length, NetTransferMode.Reliable, Channel);
            return true;
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.send.host", "team link: send to host failed: " + ex.Message);
            return false;
        }
    }

    private static bool SendToClient(NetHostGameService host, ulong peerId, byte[] bytes)
    {
        try
        {
            if (!host.IsConnected || host.NetHost == null) return false;
            host.NetHost.SendMessageToClient(peerId, bytes, bytes.Length, NetTransferMode.Reliable, Channel);
            return true;
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.send.client", $"team link: send to {peerId} failed: {ex.Message}");
            return false;
        }
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
    /// <see cref="ITeamTransport.Send"/>: on the main thread the packet goes out immediately; from other threads (the
    /// pilot loop) it is queued for the next frame and the result reflects the roster check, which is what decides
    /// whether anyone can receive it.
    /// </summary>
    private static bool SendFromHub(TeamMessage message)
    {
        try
        {
            if (message == null || !IsLinkUp) return false;
            if (message.To != 0 && !_linked.Contains(message.To)) return false;
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
