using System.Diagnostics;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms;

/// <summary>
/// Timing facts about the map screen that the game does not keep: when the map last opened, how long it has stayed
/// open with travel disabled, when the AI itself last closed it, and how long the current screen has offered nothing.
/// Updated once per in-run snapshot (<see cref="Observe"/>). Main thread only.
/// </summary>
internal static class MapWatch
{
    /// <summary>The map must stay open with travel disabled this long before "close map" is offered.</summary>
    public static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(20);

    /// <summary>After the AI closed the map itself, "open map" stays off this long (no open/close ping-pong).</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromSeconds(60);

    private static WeakReference<NMapScreen>? _screen;
    private static bool _wasOpen;
    private static long _openedAt;
    private static long _blockedSince;
    private static long _closedByAiAt;
    private static object? _idleScreen;
    private static long _idleSince;

    /// <summary>Tracks the map's open state (IsOpen false → true) and how long it has been open with travel disabled.</summary>
    public static void Observe()
    {
        NMapScreen? screen = null;
        try { screen = NMapScreen.Instance; }
        catch (Exception) { }
        long now = Stopwatch.GetTimestamp();
        if (_screen == null || !_screen.TryGetTarget(out var known) || !ReferenceEquals(known, screen))
        {
            _screen = screen != null ? new WeakReference<NMapScreen>(screen) : null;
            _wasOpen = false;
            _blockedSince = 0;
        }
        bool open = false, blocked = false;
        try
        {
            open = SceneUtil.VisibleInTree(screen) && screen!.IsOpen;
            blocked = open && !screen!.IsTraveling && !(screen.IsTravelEnabled || screen.IsDebugTravelEnabled);
        }
        catch (Exception) { }
        if (open && !_wasOpen) _openedAt = now;
        _wasOpen = open;
        if (!blocked) _blockedSince = 0;
        else if (_blockedSince == 0) _blockedSince = Math.Max(now, _openedAt);
    }

    /// <summary>How long the map has been open with travel disabled since it last opened (zero when not).</summary>
    public static TimeSpan BlockedFor => _blockedSince == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_blockedSince);

    /// <summary>
    /// Whether "close map" may be offered: the map has been open with travel disabled for <see cref="CloseAfter"/>,
    /// and the AI has not proceeded from the current room itself (then the map is open because the AI asked to leave;
    /// closing it would only loop with the room's proceed). <paramref name="note"/> explains a refusal.
    /// </summary>
    public static bool CloseAllowed(out string? note)
    {
        note = null;
        if (UiActions.SinceOwnProceedHere() != null)
        {
            note = RoomUtil.T("rooms.map_after_proceed");
            return false;
        }
        var blocked = BlockedFor;
        if (blocked >= CloseAfter) return true;
        note = RoomUtil.T("rooms.map_close_wait", (int)Math.Ceiling((CloseAfter - blocked).TotalSeconds));
        return false;
    }

    public static void NoteClosedByAi() => _closedByAiAt = Stopwatch.GetTimestamp();

    /// <summary>False for a while after the AI closed the map itself.</summary>
    public static bool ReopenAllowed => _closedByAiAt == 0 || Stopwatch.GetElapsedTime(_closedByAiAt) >= ReopenGuard;

    /// <summary>
    /// Records whether <paramref name="screen"/> offered any action this snapshot and returns how long the same screen
    /// has offered nothing (zero when it offers something or just changed).
    /// </summary>
    public static TimeSpan IdleFor(object? screen, bool idle)
    {
        long now = Stopwatch.GetTimestamp();
        if (!idle || screen == null || !ReferenceEquals(screen, _idleScreen))
        {
            _idleScreen = idle ? screen : null;
            _idleSince = now;
            return TimeSpan.Zero;
        }
        return Stopwatch.GetElapsedTime(_idleSince);
    }
}
