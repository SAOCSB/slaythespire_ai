using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Game;

namespace SpirePilot.Team;

/// <summary>
/// Every game-API call of the team link, one small method each. A game update that removes or changes a member makes
/// the method that references it fail when it is JIT-compiled, which happens at its first call: the exception then
/// surfaces at the call site, where a try inside the method itself cannot catch it. So every method here is
/// <see cref="MethodImplOptions.NoInlining"/> (it can never be compiled into its caller, which would break the caller
/// instead) and every caller wraps the call in its own try/catch. The try inside each method covers ordinary runtime
/// failures (a service torn down mid-call).
/// </summary>
internal static class TeamNet
{
    private const MethodImplOptions NoInline = MethodImplOptions.NoInlining;

    [MethodImpl(NoInline)]
    public static bool IsHost(INetGameService? svc) => svc is NetHostGameService;

    [MethodImpl(NoInline)]
    public static bool IsClient(INetGameService? svc) => svc is NetClientGameService;

    /// <summary>A real host / client session (not single player, not a replay).</summary>
    [MethodImpl(NoInline)]
    public static bool IsNetworked(INetGameService? svc)
    {
        if (svc is not (NetHostGameService or NetClientGameService)) return false;
        try { return svc.Type is NetGameType.Host or NetGameType.Client; }
        catch (Exception) { return false; }
    }

    [MethodImpl(NoInline)]
    public static bool IsConnected(INetGameService? svc)
    {
        try { return svc?.IsConnected == true; }
        catch (Exception) { return false; }
    }

    /// <summary>Client side: the host's net id, 0 when not a client or unknown.</summary>
    [MethodImpl(NoInline)]
    public static ulong HostId(INetGameService? svc)
    {
        try { return svc is NetClientGameService client ? client.HostNetId : 0; }
        catch (Exception) { return 0; }
    }

    [MethodImpl(NoInline)]
    public static string TypeName(INetGameService svc)
    {
        try { return svc.Type.ToString(); }
        catch (Exception) { return "?"; }
    }

    /// <summary>The run's network service (fallback when no hook handed us one).</summary>
    [MethodImpl(NoInline)]
    public static INetGameService? RunNetService()
    {
        try { return RunManager.Instance?.NetService; }
        catch (Exception) { return null; }
    }

    /// <summary>Our own net id and platform: from the service, else the run's service, else the local context.</summary>
    [MethodImpl(NoInline)]
    public static (ulong id, PlatformType platform) LocalIdentity(INetGameService? svc)
    {
        ulong id = 0;
        var platform = PlatformType.None;
        try
        {
            svc ??= RunManager.Instance?.NetService;
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
        return (id, platform);
    }

    [MethodImpl(NoInline)]
    public static PlatformType Platform(INetGameService? svc)
    {
        try { return svc?.Platform ?? PlatformType.None; }
        catch (Exception) { return PlatformType.None; }
    }

    [MethodImpl(NoInline)]
    public static string PlayerName(PlatformType platform, ulong id)
    {
        try { return PlatformUtil.GetPlayerName(platform, id) ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>The mod lists this game sends to peers (0.111 handshake / 0.107 initial game info).</summary>
    [MethodImpl(NoInline)]
    public static (List<string> gameplay, List<string> other) LocalModLists(INetGameService svc)
    {
#if STS2_V0107
        // 0.107 sends these lists in InitialGameInfoMessage (assumed to be built from the mod manager).
        return (ModManager.GetGameplayRelevantModNameList() ?? new List<string>(),
            ModManager.GetNonGameplayRelevantModNameList() ?? new List<string>());
#else
        var local = svc.LocalVersion;
        return (local.gameplayAffectingMods ?? new List<string>(), local.otherMods ?? new List<string>());
#endif
    }

    /// <summary>Host side: the connected clients, or null when the list is not readable.</summary>
    [MethodImpl(NoInline)]
    public static HashSet<ulong>? HostPeerIds(INetGameService? svc)
    {
        try { return svc is NetHostGameService host ? host.NetHost?.ConnectedPeerIds?.ToHashSet() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Connected players of the current run (client side), or null when not readable.</summary>
    [MethodImpl(NoInline)]
    public static HashSet<ulong>? RunPlayerIds()
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

#if !STS2_V0107
    /// <summary>0.111 host: a client's handshake mod lists (null until its handshake finished).</summary>
    [MethodImpl(NoInline)]
    public static (string[] gameplay, string[] other, string? version)? HostVersionInfo(INetGameService? svc, ulong peerId)
    {
        if (svc is not NetHostGameService host) return null;
        var info = host.GetVersionInfoForPeer(peerId);
        if (!info.HasValue) return null;
        return (info.Value.gameplayAffectingMods?.ToArray() ?? Array.Empty<string>(),
            info.Value.otherMods?.ToArray() ?? Array.Empty<string>(), info.Value.version);
    }
#endif

    // Send pattern adapted from RitsuLib's RitsuLibSidecarSend (MIT, BAKAOLC/STS2-RitsuLib): connected check,
    // reliable mode, own channel, and a failing transport (peer gone / not started) is a soft failure.

    /// <summary>Client side: one reliable packet to the host on our channel.</summary>
    [MethodImpl(NoInline)]
    public static bool SendToHost(INetGameService? svc, byte[] bytes)
    {
        try
        {
            if (svc is not NetClientGameService client || !client.IsConnected || client.NetClient == null) return false;
            client.NetClient.SendMessageToHost(bytes, bytes.Length, NetTransferMode.Reliable, TeamLink.Channel);
            return true;
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.send.host", "team link: send to host failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Host side: one reliable packet to a client on our channel.</summary>
    [MethodImpl(NoInline)]
    public static bool SendToClient(INetGameService? svc, ulong peerId, byte[] bytes)
    {
        try
        {
            if (svc is not NetHostGameService host || !host.IsConnected || host.NetHost == null) return false;
            host.NetHost.SendMessageToClient(peerId, bytes, bytes.Length, NetTransferMode.Reliable, TeamLink.Channel);
            return true;
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("team.send.client", $"team link: send to {peerId} failed: {ex.Message}");
            return false;
        }
    }
}
