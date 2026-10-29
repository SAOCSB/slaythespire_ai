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
/// Harmony hooks of the team link. Each target is patched on its own (a renamed method in a game update only
/// disables that hook) and every hook body is wrapped so it can never throw into the game's network code.
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

    private static readonly Type[] PacketArgs = { typeof(ulong), typeof(byte[]), typeof(NetTransferMode), typeof(int) };

    public static void Install(Harmony harmony)
    {
        HarmonyNativePreload.EnsureLoaded();

        HostReceive = Patch(harmony, typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived), PacketArgs,
            prefix: nameof(HostPacketPrefix));
        ClientReceive = Patch(harmony, typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived), PacketArgs,
            prefix: nameof(ClientPacketPrefix));

#if STS2_V0107
        // 0.107 has no handshake with version info. The host sends its version and both mod lists to every joining
        // client in InitialGameInfoMessage; we add our own handler for that vanilla message on each client service.
        // Hosts learn about clients only from their first (valid) team packet.
        ClientDiscovery = Patch(harmony, typeof(NetClientGameService), nameof(NetClientGameService.Initialize),
            new[] { typeof(NetClient), typeof(PlatformType) }, postfix: nameof(ClientInitializePostfix));
        HostDiscovery = false;
#else
        ClientDiscovery = Patch(harmony, typeof(NetClientGameService), nameof(NetClientGameService.HandshakeSucceeded),
            new[] { typeof(ulong), typeof(PeerVersionInfo) }, postfix: nameof(ClientHandshakePostfix));
        HostDiscovery = Patch(harmony, typeof(NetHostGameService), nameof(NetHostGameService.HandshakeSucceeded),
            new[] { typeof(ulong), typeof(PeerVersionInfo) }, postfix: nameof(HostHandshakePostfix));
#endif

        // Session end. Optional: Tick also polls IsConnected and the host's peer list.
        Patch(harmony, typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost),
            new[] { typeof(ulong), typeof(NetErrorInfo) }, postfix: nameof(SessionLostPostfix));
        Patch(harmony, typeof(NetClientGameService), nameof(NetClientGameService.Disconnect),
            new[] { typeof(NetError), typeof(bool) }, postfix: nameof(LocalDisconnectPostfix));
        Patch(harmony, typeof(NetHostGameService), nameof(NetHostGameService.OnDisconnected),
            new[] { typeof(NetErrorInfo) }, postfix: nameof(SessionLostPostfix));
        Patch(harmony, typeof(NetHostGameService), nameof(NetHostGameService.Disconnect),
            new[] { typeof(NetError), typeof(bool) }, postfix: nameof(LocalDisconnectPostfix));
        Patch(harmony, typeof(NetHostGameService), nameof(NetHostGameService.OnPeerDisconnected),
            new[] { typeof(ulong), typeof(NetErrorInfo) }, postfix: nameof(HostPeerDisconnectedPostfix));
    }

    private static bool Patch(Harmony harmony, Type type, string method, Type[] args, string? prefix = null, string? postfix = null)
    {
        var what = $"{type.Name}.{method}";
        try
        {
            var target = AccessTools.Method(type, method, args);
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
            ModLog.Error($"team link: hooking {what} failed: {ex.Message}");
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

    private static bool HostPacketPrefix(NetHostGameService __instance, ulong senderId, byte[] packetBytes) =>
        TeamLink.OnPacket(__instance, senderId, packetBytes);

    private static bool ClientPacketPrefix(NetClientGameService __instance, ulong senderId, byte[] packetBytes) =>
        TeamLink.OnPacket(__instance, senderId, packetBytes);

    // ---- discovery ----

#if STS2_V0107
    private static readonly ConditionalWeakTable<NetClientGameService, object> Registered = new();

    private static void ClientInitializePostfix(NetClientGameService __instance)
    {
        try
        {
            if (__instance == null || Registered.TryGetValue(__instance, out _)) return;
            Registered.Add(__instance, new object());
            var service = __instance;
            // Vanilla's JoinFlow has its own handler; the message bus keeps a list of handlers per message type.
            service.RegisterMessageHandler<InitialGameInfoMessage>((message, senderId) =>
            {
                try { TeamLink.OnPeerVersion(service, senderId, message.gameplayAffectingMods, message.otherMods, message.version); }
                catch (Exception) { /* never throw into the message bus */ }
            });
        }
        catch (Exception ex)
        {
            ThrottledLogWarn("team link: could not watch InitialGameInfoMessage: " + ex.Message);
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
