using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Combat;
using SpirePilot.Infra;

namespace SpirePilot.Game;

/// <summary>
/// Helpers shared by the executors: pressing the game's own buttons, clicking card holders, and waiting for the
/// game to settle afterwards. All methods run on the main thread.
/// </summary>
internal static class UiActions
{
    /// <summary>Default time an executor waits for its effect to show up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Presses a game button the way the game does on mouse release (<see cref="NClickableControl.ForceClick"/>).
    /// ForceClick skips the button's own enabled check, so it is checked here first. A pressed room proceed button
    /// is remembered as the AI's own proceed (see <see cref="SinceOwnProceedHere"/>).
    /// </summary>
    public static ActionResult Press(NClickableControl? button, string what)
    {
        if (!SceneUtil.Valid(button)) return ActionResult.Fail(what + ": button not found");
        if (!SceneUtil.VisibleInTree(button)) return ActionResult.Fail(what + ": button not visible");
        if (!button!.IsEnabled) return ActionResult.Fail(what + ": button disabled");
        try
        {
            button.ForceClick();
            if (button is NProceedButton) NoteOwnProceed();
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(what + ": " + ex.Message);
        }
    }

    /// <summary>Card holders are not buttons: they are clicked by emitting their Pressed signal.</summary>
    public static ActionResult ClickHolder(NCardHolder? holder, string what)
    {
        if (!SceneUtil.Valid(holder) || !SceneUtil.Alive(holder)) return ActionResult.Fail(what + ": card not found");
        try
        {
            holder!.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(what + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Waits until <paramref name="done"/> holds (the action had its effect). Returns a failure naming
    /// <paramref name="what"/> on timeout. Use after pressing something whose result is observable.
    /// </summary>
    public static async Task<ActionResult> Expect(Func<bool> done, string what, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        bool ok = await MainThread.WaitUntil(done, timeout ?? DefaultTimeout, ct);
        return ok ? ActionResult.Ok() : ActionResult.Fail(what + ": no effect (timed out)");
    }

    /// <summary>
    /// True while a player-driven game action (a claim, purchase, pick, travel ...) is executing or queued to run, or a
    /// screen transition plays. System actions are ignored (some stay "running" while their screen is shown), and so is
    /// an action parked in <see cref="GameActionState.GatheringPlayerChoice"/>: it waits for the player, not the other way round.
    /// </summary>
    public static bool GameBusy()
    {
        try
        {
            if (NGame.Instance?.Transition is { InTransition: true }) return true;
            // The action queue only exists inside a run (reading it on the menus throws).
            var rm = RunManager.Instance;
            if (rm is not { IsInProgress: true }) return false;
            if (CombatFlow.PlayerActionRunning()) return true;
            return PlayerActionQueued(rm);
        }
        catch (Exception) { return true; }
    }

    private static bool PlayerActionQueued(RunManager rm)
    {
        var set = rm.ActionQueueSet;
        if (set == null) return false;
        try
        {
            var ready = set.GetReadyAction();
            return ready != null && ready.State != GameActionState.GatheringPlayerChoice && ActionQueueSet.IsGameActionPlayerDriven(ready);
        }
        catch (InvalidOperationException)
        {
            // Thrown while an action executes; whether that one is player-driven was checked already.
            return false;
        }
    }

    /// <summary>True while the game waits for a decision of the local player: a modal, a choice prompt or the card reward overlay.</summary>
    public static bool InputPending()
    {
        try
        {
            if (NModalContainer.Instance?.OpenModal is Node modal && SceneUtil.Alive(modal)) return true;
        }
        catch (Exception) { }
        if (CombatFlow.ChoiceOpen()) return true;
        try { return ActiveScreenContext.Instance?.GetCurrentScreen() is NCardRewardSelectionScreen; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Waits until no player-driven action runs and no transition plays (then a couple of frames for the UI to update).
    /// Stops early when the action opened a choice prompt or a modal: that action stays parked until it is answered.
    /// </summary>
    public static async Task WaitSettled(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await MainThread.Frames(2);
        await MainThread.WaitUntil(() => !GameBusy() || InputPending(), timeout ?? DefaultTimeout, ct);
        await MainThread.Frames(2);
    }

    /// <summary>Common pattern: press, then wait for an observable effect, then settle.</summary>
    public static async Task<ActionResult> PressAndExpect(NClickableControl? button, string what, Func<bool> done, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var pressed = Press(button, what);
        if (!pressed.Success) return pressed;
        var r = await Expect(done, what, timeout, ct);
        await WaitSettled(ct: ct);
        return r;
    }

    // ---------------------------------------------------------------- the AI's own room proceed

    private static WeakReference<AbstractRoom>? _proceedRoom;
    private static WeakReference<ActMap>? _proceedMap;
    private static MapCoord? _proceedCoord;
    private static long _proceedAt;

    /// <summary>
    /// Records that the AI just asked to leave the current room (a proceed button press or a programmatic proceed).
    /// Main thread.
    /// </summary>
    public static void NoteOwnProceed()
    {
        try
        {
            var run = RunManager.Instance is { IsInProgress: true } rm ? rm.DebugOnlyGetState() : null;
            if (run == null) return;
            _proceedRoom = run.CurrentRoom is { } room ? new WeakReference<AbstractRoom>(room) : null;
            _proceedMap = run.Map is { } map ? new WeakReference<ActMap>(map) : null;
            _proceedCoord = run.CurrentMapCoord;
            _proceedAt = Stopwatch.GetTimestamp();
        }
        catch (Exception) { /* run in flux */ }
    }

    /// <summary>
    /// How long ago the AI's own last proceed was made from the room the run is in now (same room object, or the same
    /// map node of the same act); null when the AI has not proceeded from this room. Main thread.
    /// </summary>
    public static TimeSpan? SinceOwnProceedHere()
    {
        if (_proceedAt == 0) return null;
        try
        {
            var run = RunManager.Instance is { IsInProgress: true } rm ? rm.DebugOnlyGetState() : null;
            if (run == null) return null;
            bool sameRoom = _proceedRoom != null && _proceedRoom.TryGetTarget(out var room) && ReferenceEquals(room, run.CurrentRoom);
            bool sameNode = _proceedCoord is { } c && run.CurrentMapCoord is { } now && c == now &&
                            _proceedMap != null && _proceedMap.TryGetTarget(out var map) && ReferenceEquals(map, run.Map);
            return sameRoom || sameNode ? Stopwatch.GetElapsedTime(_proceedAt) : null;
        }
        catch (Exception) { return null; }
    }
}
