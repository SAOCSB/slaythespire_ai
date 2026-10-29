using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Infra;

namespace SpirePilot.Game.Combat;

/// <summary>
/// Combat-specific "is the game busy / did my action land" probes shared by the combat readers, executors and the
/// readiness gate. All read-only, main thread only, never throw.
/// </summary>
internal static class CombatFlow
{
    /// <summary>How an executor's wait ended.</summary>
    public enum WaitOutcome { Done, ChoicePending, CombatOver, TimedOut, Cancelled }

    /// <summary>The live hand node, or null.</summary>
    public static NPlayerHand? Hand()
    {
        try
        {
            var hand = NPlayerHand.Instance;
            return SceneUtil.Alive(hand) ? hand : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True while a fight is running and not already being wrapped up.</summary>
    public static bool CombatLive()
    {
        try { return CombatManager.Instance is { IsInProgress: true, IsOverOrEnding: false }; }
        catch (Exception) { return false; }
    }

    /// <summary>The local player's combat state and creature are usable.</summary>
    public static bool LocalAlive(Player? me)
    {
        try { return me?.Creature != null && me.Creature.IsAlive; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when this client is in its own play window: player side, the action queue synchronizer in its play phase,
    /// part of the current player turn, not yet ended (co-op: readied) turn. Animations are not checked here (see
    /// <see cref="Readiness"/>).
    /// </summary>
    public static bool InPlayWindow(CombatState? cs, Player? me)
    {
        if (cs == null || me == null || !CombatLive() || !LocalAlive(me)) return false;
        try
        {
            var cm = CombatManager.Instance;
            if (cs.CurrentSide != CombatSide.Player) return false;
            // Requests made outside the play phase wait for the next player turn and are then dropped as stale.
            if (SyncPlayPhase() == false) return false;
            if (cm.IsPlayerReadyToEndTurn(me)) return false;
            if (!cm.IsPartOfPlayerTurn(me)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether the action queue synchronizer is in its play phase (player requests are enqueued right away). Null when
    /// it cannot be read: callers treat that as no objection. Compared by name: the enum's numbers differ between game
    /// versions (0.107: PlayPhase = 1, 0.111: PlayPhase = 2), and the runtime enum is the running game's.
    /// </summary>
    public static bool? SyncPlayPhase()
    {
        try
        {
            var sync = RunManager.Instance?.ActionQueueSynchronizer;
            if (sync == null) return null;
            return sync.CombatState.ToString() == "PlayPhase";
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Co-op only: the local player has readied end turn while teammates are still playing, and the player side of
    /// the round goes on, so the ready can still be taken back (<c>UndoEndPlayerTurnAction</c>). False in single
    /// player, once every player is ready (the turn is ending) and while player actions are disabled.
    /// </summary>
    public static bool CanUndoEndTurn(GameContext ctx, CombatState? cs, Player? me)
    {
        if (!ctx.IsMultiplayer || cs == null || me == null || !CombatLive() || !LocalAlive(me)) return false;
        try
        {
            var cm = CombatManager.Instance;
            if (cs.CurrentSide != CombatSide.Player) return false;
            if (!cm.IsPlayerReadyToEndTurn(me)) return false;
            if (cm.AllPlayersReadyToEndTurn()) return false;
            if (cm.PlayerActionsDisabled || cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo) return false;
            if (SyncPlayPhase() == false) return false;
            if (!cm.IsPartOfPlayerTurn(me)) return false;
            return cs.Players.Count > 1;
        }
        catch (Exception) { return false; }
    }

    /// <summary>What the action executor and queues mean for the local player.</summary>
    public enum QueueState
    {
        /// <summary>Nothing that concerns the local player runs or waits to run.</summary>
        Idle,
        /// <summary>An action of the local player or of the game itself runs or is ready to run.</summary>
        Busy,
        /// <summary>An action of the local player is parked waiting for a player choice (its queue is paused).</summary>
        OwnChoice,
    }

    /// <summary>
    /// Classifies the running and the next ready action for the local player. Teammates' actions (owned by another
    /// player of the fight) are ignored: they run in their own queues and do not block this player, and whatever they
    /// change shows up in the snapshot's stability signature. Actions owned by the local player or by no player (the
    /// game's own) count. An action parked in <see cref="GameActionState.GatheringPlayerChoice"/> is not running; when
    /// it is not a teammate's, the result is <see cref="QueueState.OwnChoice"/>.
    /// <c>GetReadyAction</c> throws while an action executes; that counts as busy unless the running action is a
    /// teammate's or a parked one (STS2-Agent lesson). In co-op the local player's own queue is also read, since the
    /// next ready action may be a teammate's while ours waits behind it.
    /// </summary>
    /// <param name="me">The local player (null: looked up).</param>
    /// <param name="playerDrivenOnly">Only player-driven actions count as busy (executors waiting for their own action).</param>
    public static QueueState LocalQueue(Player? me, bool playerDrivenOnly, out string? what)
    {
        what = null;
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return QueueState.Idle;
            me ??= LocalPlayer();
            ulong? mine = SafeNetId(me);
            var teammates = TeammateIds(mine);
            bool ownChoice = false, teammateRunning = false, parkedRunning = false;

            var running = rm.ActionExecutor?.CurrentlyRunningAction;
            if (running != null)
            {
                if (IsTeammates(running, teammates)) teammateRunning = true;
                else if (running.State == GameActionState.GatheringPlayerChoice)
                {
                    parkedRunning = true;
                    ownChoice = true;
                }
                else if (!playerDrivenOnly || ActionQueueSet.IsGameActionPlayerDriven(running))
                {
                    what = running.GetType().Name;
                    return QueueState.Busy;
                }
            }

            var set = rm.ActionQueueSet;
            if (set == null) return ownChoice ? QueueState.OwnChoice : QueueState.Idle;
            try
            {
                var ready = set.GetReadyAction();
                if (ready != null && !IsTeammates(ready, teammates))
                {
                    if (ready.State == GameActionState.GatheringPlayerChoice) ownChoice = true;
                    else if (!playerDrivenOnly || ActionQueueSet.IsGameActionPlayerDriven(ready))
                    {
                        what = ready.GetType().Name;
                        return QueueState.Busy;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                if (!teammateRunning && !parkedRunning)
                {
                    what = "executing";
                    return QueueState.Busy;
                }
            }
            if (mine is { } id)
            {
                // Co-op: the next ready action may be a teammate's while ours waits behind it in our own queue.
                if (teammates.Count > 0 && OwnQueueBusy(set, id, playerDrivenOnly, out var queued))
                {
                    what = queued;
                    return QueueState.Busy;
                }
                // A paused queue alone is not a pending choice: the game may keep queues paused around turn phases, and
                // treating that as "waiting for a choice" would stop the AI for good. Only a parked action counts.
            }
            return ownChoice ? QueueState.OwnChoice : QueueState.Idle;
        }
        catch (Exception)
        {
            what = "unreadable";
            return QueueState.Busy;
        }
    }

    /// <summary>
    /// The local player's own queue holds an action that is not parked, finished or cancelled (a paused queue does not
    /// count: it waits for a choice). Reads the private per-player queues (<c>ActionQueueSet._actionQueues</c>:
    /// <c>ownerId</c>, <c>isPaused</c>, <c>actions</c>); false when they cannot be read (no extra signal then).
    /// </summary>
    private static bool OwnQueueBusy(ActionQueueSet set, ulong me, bool playerDrivenOnly, out string? what)
    {
        what = null;
        try
        {
            if (Reflect.GetOptional<System.Collections.IEnumerable>(set, "_actionQueues") is not { } queues) return false;
            foreach (var queue in queues)
            {
                if (queue == null || Reflect.GetOptional<object>(queue, "ownerId") is not ulong owner || owner != me) continue;
                // A paused queue waits for a choice: reported as OwnChoice, not as busy.
                if (Reflect.GetOptional<object>(queue, "isPaused") is true) return false;
                if (Reflect.GetOptional<System.Collections.IEnumerable>(queue, "actions") is not { } actions) return false;
                foreach (var item in actions)
                {
                    if (item is not GameAction action) continue;
                    if (action.State is GameActionState.GatheringPlayerChoice or GameActionState.Finished or GameActionState.Canceled) continue;
                    if (playerDrivenOnly && !ActionQueueSet.IsGameActionPlayerDriven(action)) continue;
                    what = action.GetType().Name;
                    return true;
                }
                return false;
            }
        }
        catch (Exception) { /* queues changing or unreadable */ }
        return false;
    }

    /// <summary>
    /// True when a player-driven action of the local player (or of nobody) is running or ready to run; teammates'
    /// actions and parked ones do not count (see <see cref="LocalQueue"/>).
    /// </summary>
    public static bool PlayerDrivenBusy(Player? me = null) => LocalQueue(me, playerDrivenOnly: true, out _) == QueueState.Busy;

    /// <summary>
    /// True while a player-driven action (a claim, purchase, choice, card play ...) is executing. Unlike
    /// <see cref="PlayerDrivenBusy"/> it does not query the ready queue, so a long-lived system action can never block it.
    /// Used outside combat too (any owner counts there).
    /// </summary>
    public static bool PlayerActionRunning()
    {
        try
        {
            var running = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
            return running != null && running.State != GameActionState.GatheringPlayerChoice &&
                   ActionQueueSet.IsGameActionPlayerDriven(running);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when anything that blocks the local player runs or is queued (stronger than <see cref="PlayerDrivenBusy"/>):
    /// its own actions and the game's (enemy reactions, triggered effects, draw). Teammates' actions and actions parked
    /// for a choice are not "busy" (see <see cref="LocalQueue"/>).
    /// </summary>
    public static bool AnyActionBusy(Player? me, out string? what) =>
        LocalQueue(me, playerDrivenOnly: false, out what) == QueueState.Busy;

    /// <summary>
    /// Nothing runs or waits to run at all, whoever owns it (an action parked for a choice does not count). Stricter
    /// than <see cref="AnyActionBusy"/>: used before re-sending a request that may still sit in a queue.
    /// </summary>
    public static bool ExecutorIdle()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return true;
            var running = rm.ActionExecutor?.CurrentlyRunningAction;
            if (running != null && running.State != GameActionState.GatheringPlayerChoice) return false;
            return rm.ActionQueueSet?.GetReadyAction() == null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The local player of the running fight, or null.</summary>
    public static Player? LocalPlayer()
    {
        try
        {
            var cm = CombatManager.Instance;
            if (cm is not { IsInProgress: true }) return null;
            var cs = cm.DebugOnlyGetState();
            return cs == null ? null : LocalContext.GetMe((ICombatState)cs);
        }
        catch (Exception) { return null; }
    }

    private static ulong? SafeNetId(Player? p)
    {
        try { return p?.NetId; }
        catch (Exception) { return null; }
    }

    /// <summary>Net ids of the other players of the running fight (empty in single player or when the local player is unknown).</summary>
    private static HashSet<ulong> TeammateIds(ulong? mine)
    {
        var ids = new HashSet<ulong>();
        if (mine == null) return ids;
        try
        {
            var players = CombatManager.Instance?.DebugOnlyGetState()?.Players;
            if (players == null) return ids;
            foreach (var p in players)
            {
                if (p == null) continue;
                ulong id = p.NetId;
                if (id != mine.Value) ids.Add(id);
            }
        }
        catch (Exception) { }
        return ids;
    }

    private static bool IsTeammates(GameAction action, HashSet<ulong> teammates)
    {
        if (teammates.Count == 0) return false;
        try { return teammates.Contains(action.OwnerId); }
        catch (Exception) { return false; }
    }

    /// <summary>A screen transition (fade) is playing.</summary>
    public static bool InTransition()
    {
        try { return NGame.Instance?.Transition is { InTransition: true }; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A choice prompt for the local player is open: hand selection, a card grid / choose-a-card / bundle / relic
    /// selection (as the current screen or the top overlay).
    /// </summary>
    public static bool ChoiceOpen()
    {
        if (ScreenResolver.IsHandSelection()) return true;
        try
        {
            if (IsChoiceScreen(ActiveScreenContext.Instance?.GetCurrentScreen())) return true;
        }
        catch (Exception) { }
        try
        {
            var stack = NOverlayStack.Instance;
            if (SceneUtil.Alive(stack) && stack!.ScreenCount > 0 && IsChoiceScreen(stack.Peek())) return true;
        }
        catch (Exception) { }
        return false;
    }

    private static bool IsChoiceScreen(object? screen) =>
        screen is NCardGridSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen or NChooseARelicSelection;

    /// <summary>
    /// Waits frame by frame until <paramref name="done"/> holds, a choice prompt opens (when
    /// <paramref name="stopOnChoice"/>), the fight ends, the timeout (game time, <see cref="FrameTime"/>) passes or
    /// the token is cancelled.
    /// </summary>
    public static async Task<WaitOutcome> WaitFor(Func<bool> done, TimeSpan timeout, CancellationToken ct, bool stopOnChoice = true)
    {
        var time = new FrameTime();
        while (true)
        {
            if (ct.IsCancellationRequested) return WaitOutcome.Cancelled;
            // A prompt opened by the action wins: the action is parked until the player answers it.
            if (stopOnChoice && ChoiceOpen()) return WaitOutcome.ChoicePending;
            bool ok;
            try { ok = done(); }
            catch (Exception) { ok = false; }
            if (ok) return WaitOutcome.Done;
            bool over;
            try { over = CombatManager.Instance is not { IsInProgress: true }; }
            catch (Exception) { over = true; }
            if (over) return WaitOutcome.CombatOver;
            if (time.Elapsed > timeout) return WaitOutcome.TimedOut;
            await time.NextFrame();
        }
    }
}
