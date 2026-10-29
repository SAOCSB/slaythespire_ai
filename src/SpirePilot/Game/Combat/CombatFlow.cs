using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Players;
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
    /// True when this client is in its own play window: player side, part of the current player turn, not yet ended
    /// (co-op: readied) turn. Animations are not checked here (see <see cref="Readiness"/>).
    /// </summary>
    public static bool InPlayWindow(CombatState? cs, Player? me)
    {
        if (cs == null || me == null || !CombatLive() || !LocalAlive(me)) return false;
        try
        {
            var cm = CombatManager.Instance;
            if (cs.CurrentSide != CombatSide.Player) return false;
            if (cm.IsPlayerReadyToEndTurn(me)) return false;
            if (!cm.IsPartOfPlayerTurn(me)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when a player-driven action is running or ready to run (the one waiting for a player choice does not
    /// count). <c>GetReadyAction</c> throws while an action executes; that counts as busy (STS2-Agent lesson).
    /// </summary>
    public static bool PlayerDrivenBusy()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return false;
            var running = rm.ActionExecutor?.CurrentlyRunningAction;
            if (running != null && running.State != GameActionState.GatheringPlayerChoice &&
                ActionQueueSet.IsGameActionPlayerDriven(running)) return true;
            var set = rm.ActionQueueSet;
            if (set == null) return false;
            try
            {
                var ready = set.GetReadyAction();
                if (ready != null && ActionQueueSet.IsGameActionPlayerDriven(ready)) return true;
            }
            catch (InvalidOperationException) { return true; }
            return false;
        }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// True while a player-driven action (a claim, purchase, choice, card play ...) is executing. Unlike
    /// <see cref="PlayerDrivenBusy"/> it does not query the ready queue, so a long-lived system action can never block it.
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
    /// True when anything is running or queued (stronger than <see cref="PlayerDrivenBusy"/>): enemy moves, triggered
    /// effects, draw animations. Actions paused for a player choice are not "running".
    /// </summary>
    public static bool AnyActionBusy(out string? what)
    {
        what = null;
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return false;
            var running = rm.ActionExecutor?.CurrentlyRunningAction;
            if (running != null && running.State != GameActionState.GatheringPlayerChoice)
            {
                what = running.GetType().Name;
                return true;
            }
            var set = rm.ActionQueueSet;
            if (set == null) return false;
            try
            {
                var ready = set.GetReadyAction();
                if (ready != null)
                {
                    what = ready.GetType().Name;
                    return true;
                }
            }
            catch (InvalidOperationException) { return true; }
            return false;
        }
        catch (Exception) { return true; }
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
    /// <paramref name="stopOnChoice"/>), the fight ends, the timeout passes or the token is cancelled.
    /// </summary>
    public static async Task<WaitOutcome> WaitFor(Func<bool> done, TimeSpan timeout, CancellationToken ct, bool stopOnChoice = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            if (sw.Elapsed > timeout) return WaitOutcome.TimedOut;
            await MainThread.NextFrame();
        }
    }
}
