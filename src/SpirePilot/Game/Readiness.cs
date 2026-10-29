using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePilot.Core.Model;
using SpirePilot.Game.Combat;

namespace SpirePilot.Game;

/// <summary>Decides whether the local player can act now (no animations / queued actions / transitions).</summary>
internal static class Readiness
{
    /// <summary>
    /// The snapshot must look the same for this long before the AI may act on it: hides mid-resolution flicker
    /// (energy refunds, draw-after-play, reward buttons animating in). STS2-Agent uses 200 ms; the pilot polls every
    /// 250 ms, so 150 ms costs at most one poll.
    /// </summary>
    private static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(150);

    private static string? _lastSignature;
    private static readonly Stopwatch SinceChange = Stopwatch.StartNew();

    /// <summary>
    /// Returns (true, null) when the AI may act on <paramref name="probe"/>'s screen right now, otherwise false and a
    /// short reason ("animating", "enemy turn", "waiting for teammates", "transition", ...). One case acts with a
    /// reason: in co-op after readying end turn, (true, "waiting for teammates") — the only action then is "undo end
    /// turn", which the pilot takes only when a teammate needs it.
    /// </summary>
    public static (bool canAct, string? reason) Check(GameContext ctx, ScreenProbe probe, GameSnapshot snap)
    {
        if (probe.HumanOnlyReason != null) return (false, probe.HumanOnlyReason);
        if (probe.Kind == ScreenKind.Loading) return (false, "loading");
        if (CombatFlow.InTransition()) return (false, "transition");

        string? note = null;
        var reason = probe.Kind switch
        {
            // The room left behind after a fight (its proceed button, or the map button) is an ordinary screen.
            ScreenKind.Combat when probe.CombatFinished => ScreenGate(ctx, probe),
            ScreenKind.Combat => CombatGate(ctx, out note),
            ScreenKind.HandSelect => HandSelectGate(ctx),
            _ => ScreenGate(ctx, probe),
        };
        if (reason != null) return (false, reason);
        return Stable(Signature(ctx, probe, snap)) ? (true, note) : (false, "settling");
    }

    /// <summary>Full gate for the local player's combat turn (order follows STS2-Agent's combat action gate).</summary>
    private static string? CombatGate(GameContext ctx, out string? note)
    {
        note = null;
        var cm = CombatManager.Instance;
        var cs = ctx.Combat;
        var me = ctx.Me;
        if (cm == null || cs == null || !cm.IsInProgress) return "combat not running";
        if (cm.IsOverOrEnding) return "combat ending";
        if (cm.IsPaused) return "paused";
        if (me == null) return "no local player";
        if (!CombatFlow.LocalAlive(me)) return "dead";
        if (cs.CurrentSide != CombatSide.Player) return "enemy turn";
        if (cm.IsPlayerReadyToEndTurn(me))
        {
            if (!ctx.IsMultiplayer) return "turn ending";
            // Co-op: teammates still play. Acting is possible (undo end turn), but nothing else is.
            if (!CombatFlow.CanUndoEndTurn(ctx, cs, me)) return "waiting for teammates";
            if (ScreenResolver.FindActiveCombatRoom(MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance?.GetCurrentScreen()) == null)
                return "combat room not active";
            var readiedHand = CombatFlow.Hand();
            if (readiedHand == null || readiedHand.InCardPlay || readiedHand.IsInCardSelection) return "waiting for teammates";
            note = "waiting for teammates";
            return null;
        }
        // Only signals the reference mods gate on (IsEnemyTurnStarted / EndingPlayerTurnPhase* are not: unverified).
        if (cm.PlayerActionsDisabled) return "turn transition";
        // Requests outside the synchronizer's play phase are held for the next player turn and then dropped as stale.
        if (CombatFlow.SyncPlayPhase() == false) return "turn transition";
        if (!cm.IsPartOfPlayerTurn(me)) return ctx.IsMultiplayer ? "teammate's turn" : "not your turn";
        try
        {
            if (me.PlayerCombatState?.Phase is { } phase && phase != PlayerTurnPhase.Play) return "turn starting";
        }
        catch (Exception) { return "turn starting"; }
        if (ScreenResolver.FindActiveCombatRoom(MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance?.GetCurrentScreen()) == null)
            return "combat room not active";
        var hand = CombatFlow.Hand();
        if (hand == null) return "hand unavailable";
        if (hand.InCardPlay) return "card in play";
        if (hand.IsInCardSelection || hand.CurrentMode != NPlayerHand.Mode.Play) return "hand busy";
        // The local player's own actions and the game's (enemy reactions, triggered effects) must finish first.
        // Teammates' actions run in their own queues: what they change resets the stability signature instead.
        switch (CombatFlow.LocalQueue(me, playerDrivenOnly: false, out _))
        {
            case CombatFlow.QueueState.Busy:
                return "animating";
            case CombatFlow.QueueState.OwnChoice when !CombatFlow.ChoiceOpen():
                // Our own action waits for a choice this client does not show (remote or unrecognised prompt): the next
                // card would queue behind it.
                return "waiting for a choice";
        }
        return null;
    }

    /// <summary>
    /// Hand selection opened by a card / end-of-turn effect: the action that asked is parked (and may belong to the
    /// turn-ending phase), so neither the play phase nor the action queue is checked; the prompt itself must be live.
    /// </summary>
    private static string? HandSelectGate(GameContext ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsInProgress) return "combat not running";
        if (!CombatFlow.LocalAlive(ctx.Me)) return "dead";
        var hand = CombatFlow.Hand();
        if (hand == null || !ScreenResolver.IsHandSelection()) return "selection closing";
        if (hand.InCardPlay) return "card in play";
        return null;
    }

    /// <summary>
    /// Every other screen: no player-driven action still executing (the previous claim / purchase / pick), no map
    /// travel. System actions are not waited for: some may stay "running" while their screen is shown (assumption:
    /// rewards are offered from inside the end-of-combat sequence). Choice screens and popups are opened while their
    /// action waits, so nothing is checked there.
    /// </summary>
    private static string? ScreenGate(GameContext ctx, ScreenProbe probe)
    {
        bool choiceScreen = probe.Kind is ScreenKind.CardGridSelect or ScreenKind.ChooseOne or ScreenKind.CardReward or ScreenKind.Popup;
        // The action queue only exists inside a run (reading it on the menus throws, STS2-Agent lesson).
        if (ctx.InRun && !choiceScreen && CombatFlow.PlayerActionRunning()) return "busy";
        if (probe.Kind == ScreenKind.Map)
        {
            try
            {
                var map = NMapScreen.Instance;
                if (map != null && Infra.SceneUtil.Alive(map) && map.IsTraveling) return "map travel";
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>True once the same signature was seen for <see cref="StableFor"/>.</summary>
    private static bool Stable(string signature)
    {
        lock (SinceChange)
        {
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                SinceChange.Restart();
                return false;
            }
            return SinceChange.Elapsed >= StableFor;
        }
    }

    /// <summary>
    /// Cheap fingerprint of what the AI decides on (game model values, never animation state). Only the local player
    /// and the enemies: teammates acting (HP, block, energy, potions, hand, ended turn) must not keep resetting the
    /// settle timer and discard finished decisions; anything of theirs that matters for this player's actions shows up
    /// on the enemies or the local player, or is re-validated through the action's semantic key.
    /// </summary>
    private static string Signature(GameContext ctx, ScreenProbe probe, GameSnapshot snap)
    {
        var sb = new StringBuilder(256);
        sb.Append(probe.Kind).Append('|').Append(probe.Name).Append('|');
        if (probe.Screen != null) sb.Append(RuntimeHelpers.GetHashCode(probe.Screen)).Append('|');
        if (snap.LocalPlayer is { } p)
            sb.Append(p.NetId).Append(':').Append(p.Hp).Append('/').Append(p.Block).Append('/').Append(p.Energy).Append('/')
              .Append(p.Stars).Append('/').Append(p.Gold).Append('/').Append(p.Potions.Count).Append('/').Append(p.DeckCount)
              .Append('/').Append(p.HandCount).Append('/').Append(p.EndedTurn).Append(';');
        if (snap.Combat is { } c)
        {
            sb.Append("|R").Append(c.Round).Append(c.IsPlayerTurn ? 'P' : 'E');
            foreach (var h in c.Hand) sb.Append(h.Id).Append(h.Cost).Append(h.Playable == true ? '+' : '-').Append(',');
            sb.Append('|').Append(c.DrawPileCount).Append('/').Append(c.DiscardPileCount).Append('/').Append(c.ExhaustPileCount).Append('|');
            foreach (var e in c.Enemies)
                sb.Append(e.Key).Append(':').Append(e.Hp).Append('/').Append(e.Block).Append('/').Append(e.IsAlive)
                  .Append('/').Append(e.Intents.Count).Append('/').Append(e.Powers.Count).Append(';');
        }
        if (snap.Selection is { } s)
            sb.Append("|S").Append(s.Candidates.Count).Append('/').Append(string.Join(",", s.SelectedKeys)).Append('/').Append(s.CanConfirm);
        if (snap.Map is { } m) sb.Append("|M").Append(m.Current).Append('/').Append(m.Reachable.Count);
        if (snap.Rewards is { } r) sb.Append("|W").Append(r.Items.Count).Append('/').Append(r.CardChoices.Count);
        if (snap.Shop is { } sh) sb.Append("|H").Append(sh.Items.Count(i => i.Affordable)).Append('/').Append(sh.Items.Count);
        if (snap.Event is { } ev) sb.Append("|V").Append(ev.Id).Append('/').Append(ev.Options.Count).Append('/').Append(ev.Options.Count(o => o.Locked));
        if (snap.RestSite is { } rs) sb.Append("|T").Append(rs.Options.Count).Append('/').Append(rs.Options.Count(o => o.Enabled));
        if (snap.CharacterSelect is { } cs)
            sb.Append("|C").Append(cs.Characters.FirstOrDefault(ch => ch.Selected)?.Id).Append('/').Append(cs.Ascension).Append('/').Append(cs.LocalReady);
        if (snap.GameOver is { } go) sb.Append("|G").Append(go.Victory);
        return sb.ToString();
    }
}
