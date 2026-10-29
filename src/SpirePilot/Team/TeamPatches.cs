using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;
using SpirePilot.Infra;
#if STS2_V0107
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
#endif

namespace SpirePilot.Team;

/// <summary>
/// Harmony hooks of the team link. Each target is patched on its own: its type and argument types are resolved
/// inside that patch's own try (a renamed or moved type in a game update only disables that hook), and every hook
/// body is wrapped so it can never throw into the game's network code.
/// </summary>
internal static class TeamPatches
{
    /// <summary>Prefix on <c>NetHostGameService.OnPacketReceived</c> is active (we can be a linked host).</summary>
    public static bool HostReceive { get; private set; }

    /// <summary>Prefix on <c>NetClientGameService.OnPacketReceived</c> is active (we can be a linked client).</summary>
    public static bool ClientReceive { get; private set; }

    /// <summary>A client learns the host's mod list (0.111 handshake / 0.107 initial game info).</summary>
    public static bool ClientDiscovery { get; private set; }

    /// <summary>The host learns each client's mod list from its handshake (0.111 only).</summary>
    public static bool HostDiscovery { get; private set; }

    public static void Install(Harmony harmony)
    {
        HarmonyPreload.EnsureLoaded();

        HostReceive = Patch(harmony, "NetHostGameService.OnPacketReceived",
            () => AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived),
                new[] { typeof(ulong), typeof(byte[]), typeof(NetTransferMode), typeof(int) }),
            prefix: nameof(HostPacketPrefix));
        ClientReceive = Patch(harmony, "NetClientGameService.OnPacketReceived",
            () => AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived),
                new[] { typeof(ulong), typeof(byte[]), typeof(NetTransferMode), typeof(int) }),
            prefix: nameof(ClientPacketPrefix));

#if STS2_V0107
        // 0.107 has no handshake with version info. The host sends its version and both mod lists to every joining
        // client in InitialGameInfoMessage; we add our own handler for that vanilla message on each client service.
        // Three independent ways to register it, since NetClientGameService.Initialize is tiny and may be inlined
        // away: after Initialize, before the client's first packet is dispatched (inside ClientPacketPrefix), and a
        // postfix on JoinFlow's private handler of the message itself.
        // Hosts learn about clients only from their first (valid) team packet.
        bool init = Patch(harmony, "NetClientGameService.Initialize",
            () => AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.Initialize),
                new[] { typeof(NetClient), typeof(PlatformType) }),
            postfix: nameof(ClientInitializePostfix));
        bool join = Patch(harmony, "JoinFlow.HandleInitialGameInfoMessage",
            () => AccessTools.Method(typeof(JoinFlow), "HandleInitialGameInfoMessage",
                new[] { typeof(InitialGameInfoMessage), typeof(ulong) }),
            postfix: nameof(JoinFlowInitialInfoPostfix));
        ClientDiscovery = init || join || ClientReceive;
        HostDiscovery = false;
#else
        ClientDiscovery = Patch(harmony, "NetClientGameService.HandshakeSucceeded",
            () => AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.HandshakeSucceeded),
                new[] { typeof(ulong), typeof(PeerVersionInfo) }),
            postfix: nameof(ClientHandshakePostfix));
        HostDiscovery = Patch(harmony, "NetHostGameService.HandshakeSucceeded",
            () => AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.HandshakeSucceeded),
                new[] { typeof(ulong), typeof(PeerVersionInfo) }),
            postfix: nameof(HostHandshakePostfix));
#endif

        // Session end. Optional: Tick also polls IsConnected and the host's peer list.
        Patch(harmony, "NetClientGameService.OnDisconnectedFromHost",
            () => AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost),
                new[] { typeof(ulong), typeof(NetErrorInfo) }),
            postfix: nameof(SessionLostPostfix));
        Patch(harmony, "NetClientGameService.Disconnect",
            () => AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect),
                new[] { typeof(NetError), typeof(bool) }),
            postfix: nameof(LocalDisconnectPostfix));
        Patch(harmony, "NetHostGameService.OnDisconnected",
            () => AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.OnDisconnected),
                new[] { typeof(NetErrorInfo) }),
            postfix: nameof(SessionLostPostfix));
        Patch(harmony, "NetHostGameService.Disconnect",
            () => AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect),
                new[] { typeof(NetError), typeof(bool) }),
            postfix: nameof(LocalDisconnectPostfix));
        Patch(harmony, "NetHostGameService.OnPeerDisconnected",
            () => AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.OnPeerDisconnected),
                new[] { typeof(ulong), typeof(NetErrorInfo) }),
            postfix: nameof(HostPeerDisconnectedPostfix));
    }

    /// <summary>
    /// Patches one target. <paramref name="resolve"/> runs inside the try: it is the only place that names the
    /// target's game types, so a type that moved fails here (logged) instead of failing <see cref="Install"/>.
    /// </summary>
    private static bool Patch(Harmony harmony, string what, Func<MethodInfo?> resolve, string? prefix = null, string? postfix = null)
    {
        try
        {
            var target = resolve();
            if (target == null)
            {
                ModLog.Warn($"team link: {what} not found, hook skipped (game version changed?)");
                return false;
            }
            harmony.Patch(target,
                prefix: prefix == null ? null : Hook(prefix),
                postfix: postfix == null ? null : Hook(postfix));
            ModLog.Info($"team link: hooked {what} ({(prefix != null ? "prefix" : "postfix")})");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"team link: hooking {what} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static HarmonyMethod Hook(string name)
    {
        var method = typeof(TeamPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(nameof(TeamPatches), name);
        // Run before other mods' receive prefixes so nobody else ever looks at (or forwards) our packets.
        return new HarmonyMethod(method) { priority = Priority.First };
    }

    // ---- receive (parameter names must match the originals: Harmony binds by name) ----

    private static bool HostPacketPrefix(NetHostGameService __instance, ulong senderId, byte[] packetBytes)
    {
        try { return TeamLink.OnPacket(__instance, senderId, packetBytes); }
        catch (Exception) { return true; }
    }

    private static bool ClientPacketPrefix(NetClientGameService __instance, ulong senderId, byte[] packetBytes)
    {
#if STS2_V0107
        // Runs before vanilla dispatches this packet, so a handler registered now already sees it.
        try { InitialInfoWatch.Ensure(__instance); }
        catch (Exception ex) { ThrottledLogWarn("team link: could not watch InitialGameInfoMessage: " + ex.Message); }
#endif
        try { return TeamLink.OnPacket(__instance, senderId, packetBytes); }
        catch (Exception) { return true; }
    }

    // ---- discovery ----

#if STS2_V0107
    private static void ClientInitializePostfix(NetClientGameService __instance)
    {
        try { InitialInfoWatch.Ensure(__instance); }
        catch (Exception ex) { ThrottledLogWarn("team link: could not watch InitialGameInfoMessage: " + ex.Message); }
    }

    /// <summary>Private <c>JoinFlow.HandleInitialGameInfoMessage(InitialGameInfoMessage, ulong)</c>; __0 = the message.</summary>
    private static void JoinFlowInitialInfoPostfix(JoinFlow __instance, InitialGameInfoMessage __0)
    {
        try { InitialInfoWatch.FromJoinFlow(__instance, __0); }
        catch (Exception ex) { ThrottledLogWarn("team link: JoinFlow initial info hook failed: " + ex.Message); }
    }

    /// <summary>Registers our InitialGameInfoMessage handler once per client service (0.107).</summary>
    private static class InitialInfoWatch
    {
        private static readonly ConditionalWeakTable<NetClientGameService, object> Registered = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Ensure(NetClientGameService? service)
        {
            if (service == null) return;
            lock (Registered)
            {
                if (Registered.TryGetValue(service, out _)) return;
                Registered.Add(service, new object());
            }
            // Vanilla's JoinFlow has its own handler; the message bus keeps a list of handlers per message type.
            service.RegisterMessageHandler<InitialGameInfoMessage>((message, senderId) =>
            {
                try { TeamLink.OnPeerVersion(service, senderId, message.gameplayAffectingMods, message.otherMods, message.version); }
                catch (Exception) { /* never throw into the message bus */ }
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FromJoinFlow(JoinFlow flow, InitialGameInfoMessage message)
        {
            var service = flow?.NetService;
            if (service == null) return;
            ulong host = 0;
            try { host = service.HostNetId; } catch (Exception) { /* TeamLink maps it to the host anyway */ }
            TeamLink.OnPeerVersion(service, host, message.gameplayAffectingMods, message.otherMods, message.version);
        }
    }
#else
    private static void ClientHandshakePostfix(NetClientGameService __instance, ulong peerId, PeerVersionInfo versionInfo)
    {
        try { TeamLink.OnPeerVersion(__instance, peerId, versionInfo.gameplayAffectingMods, versionInfo.otherMods, versionInfo.version); }
        catch (Exception ex) { ThrottledLogWarn("team link: client handshake hook failed: " + ex.Message); }
    }

    private static void HostHandshakePostfix(NetHostGameService __instance, ulong senderId, PeerVersionInfo versionInfo)
    {
        try { TeamLink.OnPeerVersion(__instance, senderId, versionInfo.gameplayAffectingMods, versionInfo.otherMods, versionInfo.version); }
        catch (Exception ex) { ThrottledLogWarn("team link: host handshake hook failed: " + ex.Message); }
    }
#endif

    // ---- session end ----

    private static void SessionLostPostfix(INetGameService __instance)
    {
        try { TeamLink.OnSessionEnded(__instance, "connection lost"); }
        catch (Exception) { /* never throw into the game */ }
    }

    private static void LocalDisconnectPostfix(INetGameService __instance)
    {
        try { TeamLink.OnSessionEnded(__instance, "left the session"); }
        catch (Exception) { /* never throw into the game */ }
    }

    private static void HostPeerDisconnectedPostfix(NetHostGameService __instance, ulong peerId)
    {
        try { TeamLink.OnPeerLeft(__instance, peerId); }
        catch (Exception) { /* never throw into the game */ }
    }

    private static void ThrottledLogWarn(string message) => SpirePilot.Game.ThrottledLog.Warn("team.hook", message);
}
