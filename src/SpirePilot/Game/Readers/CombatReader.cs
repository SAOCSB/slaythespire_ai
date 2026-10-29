using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Combat info (hand, piles, enemies, intents) and actions: play card (every valid target), use potion (every valid
/// target incl. teammates), end turn. Called for ScreenKind.Combat. FillState is called for every snapshot while in
/// combat (also when a selection screen is open).
/// </summary>
internal static class CombatReader
{
    /// <summary>Budget for a card play to leave the hand and resolve (client round trips included).</summary>
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PotionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndTurnTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers the legal combat actions of the local player: card plays (hand order, every valid target), potions
    /// (slot order, every valid target incl. teammates), end turn. Nothing is registered outside the local player's
    /// play window (enemy turn, turn already ended, dead), the readiness gate explains why.
    /// </summary>
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        CombatStrings.Ensure();
        var view = CombatView.Create(ctx);
        if (view == null) return;
        if (!CombatFlow.InPlayWindow(view.Cs, view.Me)) return;

        try { AddCardPlays(view, snap, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:cards", "combat card actions failed: " + ex); }
        try { AddPotions(view, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:potions", "combat potion actions failed: " + ex); }
        try { AddEndTurn(view, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:endturn", "end turn action failed: " + ex); }
    }

    /// <summary>Fills <see cref="GameSnapshot.Combat"/> (round, hand, piles, enemies) whenever combat is in progress.</summary>
    public static void FillState(GameContext ctx, GameSnapshot snap)
    {
        var cs = ctx.Combat;
        if (cs == null) return;
        var info = new CombatInfo();
        Part(() => info.Round = cs.RoundNumber);
        Part(() => info.IsPlayerTurn = cs.CurrentSide == CombatSide.Player);
        Part(() =>
        {
            var enc = cs.Encounter;
            if (enc == null) return;
            info.EncounterType = enc.RoomType.ToString();
            var name = ModelText.Loc(enc.Title);
            info.EncounterName = name.Length > 0 ? name : null;
        });

        var pcs = SafeGet(() => ctx.Me?.PlayerCombatState);
        if (pcs != null)
        {
            Part(() =>
            {
                var hand = pcs.Hand?.Cards;
                if (hand == null) return;
                for (int i = 0; i < hand.Count; i++)
                {
                    var card = hand[i];
                    if (card == null) continue;
                    try { info.Hand.Add(ModelText.Card(card, "h" + i, inCombat: true)); }
                    catch (Exception) { info.Hand.Add(new CardInfo { Key = "h" + i, Id = ModelText.SafeId(card) }); }
                }
            });
            Part(() => info.DrawPile = Pile(pcs.DrawPile?.Cards, sortByName: true));
            Part(() => info.DiscardPile = Pile(pcs.DiscardPile?.Cards, sortByName: false));
            Part(() => info.ExhaustPile = Pile(pcs.ExhaustPile?.Cards, sortByName: false));
            Part(() => info.DrawPileCount = pcs.DrawPile?.Cards.Count ?? 0);
            Part(() => info.DiscardPileCount = pcs.DiscardPile?.Cards.Count ?? 0);
            Part(() => info.ExhaustPileCount = pcs.ExhaustPile?.Cards.Count ?? 0);
        }

        Part(() =>
        {
            foreach (var e in CombatKeys.Enemies(cs))
            {
                try { info.Enemies.Add(ModelText.Creature(e.Creature, e.Key)); }
                catch (Exception) { info.Enemies.Add(new CreatureInfo { Key = e.Key, Id = e.MonsterId, IsAlive = false }); }
            }
        });
        snap.Combat = info;
    }

    /// <summary>Pile contents. The draw pile is sorted by name: its real order is hidden information.</summary>
    private static List<CardInfo> Pile(IReadOnlyList<CardModel>? cards, bool sortByName)
    {
        var list = new List<CardInfo>();
        if (cards == null) return list;
        foreach (var card in cards)
        {
            if (card == null) continue;
            try { list.Add(ModelText.Card(card, "", inCombat: true)); }
            catch (Exception) { }
        }
        if (sortByName)
            list = list.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        return list;
    }

    // ------------------------------------------------------------------ card plays

    /// <summary>Identical hand cards (same id, upgrade, cost and text) collapse into one menu line per target.</summary>
    private sealed class HandGroup
    {
        public List<(CardModel Card, int Index)> Cards { get; } = new();
        public CardInfo Info { get; init; } = new();
    }

    private static void AddCardPlays(CombatView view, GameSnapshot snap, ActionSink sink)
    {
        var hand = SafeGet(() => view.Me.PlayerCombatState?.Hand?.Cards);
        if (hand == null) return;

        var groups = new List<HandGroup>();
        var bySignature = new Dictionary<string, HandGroup>();
        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (card == null) continue;
            bool playable;
            try { playable = card.CanPlay(); }
            catch (Exception) { playable = false; }
            if (!playable) continue;
            var info = i < (snap.Combat?.Hand.Count ?? 0) && snap.Combat!.Hand[i].Id == ModelText.SafeId(card)
                ? snap.Combat.Hand[i]
                : ModelText.Card(card, "h" + i, inCombat: true);
            string sig = string.Join("|", info.Id, ModelText.UpgradeLevel(card), info.Cost, info.StarCost, info.Description, string.Join(",", info.Keywords));
            if (!bySignature.TryGetValue(sig, out var group))
            {
                group = new HandGroup { Info = info };
                bySignature[sig] = group;
                groups.Add(group);
            }
            group.Cards.Add((card, i));
        }

        foreach (var group in groups)
        {
            var (first, firstIndex) = group.Cards[0];
            List<TargetOption> targets;
            try { targets = view.CardTargets(first); }
            catch (Exception) { continue; }
            if (targets.Count == 0) continue;

            bool unpredictable = IsUnpredictable(first, group.Info);
            string name = group.Cards.Count > 1 ? L10n.T("lbl.copies", group.Info.Name, group.Cards.Count) : group.Info.Name;
            string idKey = "play:" + group.Info.Id + "+" + ModelText.UpgradeLevel(first) + "@";

            List<int>? aoeNumbers = null;
            foreach (var target in targets)
            {
                string label = target.Label == null ? L10n.T("act.play", name) : L10n.T("act.play_target", name, target.Label);
                var numbers = PlayNumbers(view, first, group.Info, target, ref aoeNumbers);
                if (numbers.Length > 0) label += " (" + numbers + ")";

                var args = new Dictionary<string, string>
                {
                    [ActionArgs.Index] = firstIndex.ToString(),
                    [ActionArgs.Card] = "h" + firstIndex,
                };
                if (target.Key != null) args[ActionArgs.Target] = target.Key;

                var cards = group.Cards.Select(c => c.Card).ToList();
                var tgt = target;
                sink.Add(ActionKind.PlayCard, label, idKey + target.Semantic,
                    ct => PlayCard(cards, tgt, ct), args, unpredictable);
            }
        }
    }

    /// <summary>"6 dmg, kills" / "5 block" / "X=3" for the label; "" when nothing cheap is known.</summary>
    private static string PlayNumbers(CombatView view, CardModel card, CardInfo info, TargetOption target, ref List<int>? aoeCache)
    {
        var parts = new List<string>();
        try
        {
            if (info.Cost == -1)
            {
                int energy = SafeInt(() => view.Me.PlayerCombatState.Energy);
                parts.Add(L10n.T("lbl.x_energy", energy));
            }
            var n = ModelText.Numbers(card, target.IsEnemy ? target.Creature : null);
            if (n is { } nums)
            {
                if (nums.Damage is { } dmg && IsAttackLike(card, info))
                {
                    parts.Add(nums.Hits > 1 ? L10n.T("lbl.dmg_hits", dmg, nums.Hits) : L10n.T("lbl.dmg", dmg));
                    TargetType type = SafeTargetType(card);
                    if (target.IsEnemy && target.Creature != null && dmg > 0)
                    {
                        if (Kills(target.Creature, dmg * nums.Hits)) parts.Add(L10n.T("lbl.kills"));
                    }
                    else if (type == TargetType.AllEnemies && dmg > 0)
                    {
                        // Per-enemy previews (Vulnerable differs per enemy); cached per card for this snapshot.
                        aoeCache ??= view.TargetableEnemies()
                            .Select(e => ModelText.Numbers(card, e.Creature) is { Damage: { } d } p && Kills(e.Creature, d * p.Hits) ? 1 : 0)
                            .ToList();
                        int kills = aoeCache.Sum();
                        if (kills > 0) parts.Add(L10n.T("lbl.kills_n", kills));
                    }
                }
                if (nums.Block is { } block && block > 0) parts.Add(L10n.T("lbl.block", block));
            }
        }
        catch (Exception) { /* numbers are optional */ }
        return string.Join(", ", parts);
    }

    private static bool IsAttackLike(CardModel card, CardInfo info)
    {
        if (info.Type == nameof(CardType.Attack)) return true;
        // Skills/powers with damage numbers (e.g. "deal 5 damage to ALL enemies") still deal damage.
        return SafeTargetType(card) is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy;
    }

    private static bool Kills(Creature enemy, int damage)
    {
        try { return enemy.IsAlive && damage >= enemy.CurrentHp + enemy.Block; }
        catch (Exception) { return false; }
    }

    private static readonly string[] UnpredictableMarkers =
    {
        "draw", "random", "choose", "discard", "exhaust a", "exhaust 1", "exhaust any", "scry", "discover", "transform",
        "shuffle", "抽", "随机", "选择", "丢弃", "消耗一", "消耗1", "预见", "发现", "变化", "洗入",
    };

    /// <summary>Draw / random / choice / X-cost / discard / exhaust-choice cards end a multi-step plan.</summary>
    private static bool IsUnpredictable(CardModel card, CardInfo info)
    {
        if (info.Cost == -1 || info.StarCost == -1) return true;
        var type = SafeTargetType(card);
        if (type == TargetType.RandomEnemy) return true;
        try { if (card.DynamicVars != null && card.DynamicVars.ContainsKey(CardsVar.defaultName)) return true; }
        catch (Exception) { }
        foreach (var m in UnpredictableMarkers)
            if (info.Description.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Plays one of <paramref name="cards"/> (interchangeable copies) at <paramref name="target"/> through
    /// <see cref="CardModel.TryManualPlay"/> (the vanilla UI's path: it enqueues a PlayCardAction through the action
    /// queue synchronizer, so it is co-op safe), then waits until the card left the hand and the player-driven actions
    /// settled, or a choice prompt opened.
    /// </summary>
    private static async Task<ActionResult> PlayCard(List<CardModel> cards, TargetOption target, CancellationToken ct)
    {
        var ctx = GameContext.Capture();
        var me = ctx.Me;
        if (ctx.Combat == null || me == null) return ActionResult.Fail("not in combat");
        if (!CombatFlow.InPlayWindow(ctx.Combat, me)) return ActionResult.Fail("not your turn");
        var hand = CombatFlow.Hand();
        if (hand != null && hand.InCardPlay) return ActionResult.Fail("a card is already being played");
        if (CombatFlow.ChoiceOpen()) return ActionResult.Fail("a choice is pending");

        var handCards = SafeGet(() => me.PlayerCombatState?.Hand?.Cards);
        if (handCards == null) return ActionResult.Fail("hand unavailable");
        // Only cards from the local player's own hand: the host rejects plays of cards owned by someone else.
        var card = cards.FirstOrDefault(c => c != null && handCards.Contains(c) && SafeBool(() => c.CanPlay()));
        if (card == null) return ActionResult.Fail("card is no longer playable");

        var creature = target.Creature;
        if (creature != null && !SafeBool(() => creature.IsAlive)) return ActionResult.Fail("target is dead");

        bool accepted;
        try { accepted = card.TryManualPlay(creature); }
        catch (Exception ex) { return ActionResult.Fail("play failed: " + ex.Message); }
        if (!accepted) return ActionResult.Fail("the game rejected the play (target or state not valid)");

        // "Left the hand at some point" (a card may come back to the hand through its own effect).
        bool left = false;
        bool Played()
        {
            if (!SafeBool(() => card.Pile?.Type == PileType.Hand)) left = true;
            return left && !CombatFlow.PlayerDrivenBusy();
        }
        var outcome = await CombatFlow.WaitFor(Played, PlayTimeout, ct);
        switch (outcome)
        {
            case CombatFlow.WaitOutcome.ChoicePending:
                return ActionResult.Ok("choice pending");
            case CombatFlow.WaitOutcome.Cancelled:
                return ActionResult.Fail("cancelled");
            case CombatFlow.WaitOutcome.TimedOut:
                // Never cancel a running action (co-op desync); report what we know.
                return !left && !CombatFlow.PlayerDrivenBusy()
                    ? ActionResult.Fail("card play had no effect (timed out)")
                    : ActionResult.Ok("still resolving");
        }
        await MainThread.Frames(2);
        return ActionResult.Ok();
    }

    // ------------------------------------------------------------------ potions

    private static void AddPotions(CombatView view, ActionSink sink)
    {
        var slots = SafeGet(() => view.Me.PotionSlots);
        if (slots == null) return;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            var potion = slots[slot];
            if (potion == null || !ModelText.PotionUsable(potion, inCombat: true)) continue;
            List<TargetOption> targets;
            try { targets = view.PotionTargets(potion); }
            catch (Exception) { continue; }
            string id = ModelText.SafeId(potion);
            string name = ModelText.Loc(SafeGet(() => potion.Title));
            if (name.Length == 0) name = id;
            string numbers = PotionNumbers(potion);
            foreach (var target in targets)
            {
                string label = target.Label == null ? L10n.T("act.use_potion", name) : L10n.T("act.use_potion_target", name, target.Label);
                if (numbers.Length > 0) label += " (" + numbers + ")";
                var args = new Dictionary<string, string> { [ActionArgs.Slot] = slot.ToString() };
                if (target.Key != null) args[ActionArgs.Target] = target.Key;
                var p = potion;
                int s = slot;
                var tgt = target;
                sink.Add(ActionKind.UsePotion, label, "potion:" + id + "@" + target.Semantic,
                    ct => UsePotion(p, s, tgt, ct), args, unpredictable: true);
            }
        }
    }

    private static string PotionNumbers(PotionModel potion)
    {
        var parts = new List<string>();
        if (ModelText.PotionVar(potion, DamageVar.defaultName) is { } dmg && dmg > 0) parts.Add(L10n.T("lbl.dmg", dmg));
        if (ModelText.PotionVar(potion, BlockVar.defaultName) is { } block && block > 0) parts.Add(L10n.T("lbl.block", block));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Uses the potion through <see cref="PotionModel.EnqueueManualUse"/> (the game's synchronized path; the target
    /// may be a teammate's creature), then waits until the slot changed and player-driven actions settled, or a choice
    /// prompt opened.
    /// </summary>
    private static async Task<ActionResult> UsePotion(PotionModel potion, int slot, TargetOption target, CancellationToken ct)
    {
        var ctx = GameContext.Capture();
        var me = ctx.Me;
        if (ctx.Combat == null || me == null) return ActionResult.Fail("not in combat");
        if (!CombatFlow.InPlayWindow(ctx.Combat, me)) return ActionResult.Fail("not your turn");
        if (CombatFlow.ChoiceOpen()) return ActionResult.Fail("a choice is pending");
        var hand = CombatFlow.Hand();
        if (hand != null && hand.InCardPlay) return ActionResult.Fail("a card is being played");
        if (!ReferenceEquals(SlotPotion(me, slot), potion)) return ActionResult.Fail("potion is no longer in that slot");
        if (!ModelText.PotionUsable(potion, inCombat: true)) return ActionResult.Fail("potion cannot be used now");
        var creature = target.Creature;
        if (creature != null && !SafeBool(() => creature.IsAlive)) return ActionResult.Fail("target is dead");

        try { potion.EnqueueManualUse(creature); }
        catch (Exception ex) { return ActionResult.Fail("potion use failed: " + ex.Message); }

        bool Gone() => SafeBool(() => potion.HasBeenRemovedFromState) || !ReferenceEquals(SlotPotion(me, slot), potion);
        var outcome = await CombatFlow.WaitFor(() => Gone() && !CombatFlow.PlayerDrivenBusy(), PotionTimeout, ct);
        switch (outcome)
        {
            case CombatFlow.WaitOutcome.ChoicePending:
                return ActionResult.Ok("choice pending");
            case CombatFlow.WaitOutcome.Cancelled:
                return ActionResult.Fail("cancelled");
            case CombatFlow.WaitOutcome.TimedOut:
                return !Gone() && !SafeBool(() => potion.IsQueued)
                    ? ActionResult.Fail("potion use had no effect (timed out)")
                    : ActionResult.Ok("still resolving");
        }
        await MainThread.Frames(2);
        return ActionResult.Ok();
    }

    private static PotionModel? SlotPotion(Player me, int slot)
    {
        try
        {
            var slots = me.PotionSlots;
            return slots != null && slot >= 0 && slot < slots.Count ? slots[slot] : null;
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ end turn

    private static void AddEndTurn(CombatView view, ActionSink sink)
    {
        sink.Add(ActionKind.EndTurn, L10n.T("act.end_turn"), "end_turn", EndTurn, unpredictable: true);
    }

    /// <summary>
    /// Ends (in co-op: readies) the local player's turn by enqueueing <see cref="EndPlayerTurnAction"/> through the
    /// action queue synchronizer, the co-op safe path STS2MCP uses (no FTUE popup, no local-only mutators). Waits until
    /// the game took it: the player is marked ready, the turn/side changed, or an end-of-turn prompt opened.
    /// </summary>
    private static async Task<ActionResult> EndTurn(CancellationToken ct)
    {
        var ctx = GameContext.Capture();
        var cs = ctx.Combat;
        var me = ctx.Me;
        if (cs == null || me == null) return ActionResult.Fail("not in combat");
        if (!CombatFlow.InPlayWindow(cs, me)) return ActionResult.Fail("turn already ended or not your turn");
        var hand = CombatFlow.Hand();
        // Mirrors NEndTurnButton.CanTurnBeEnded (private): not while a card is in play or the hand is selecting.
        if (hand != null && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play)) return ActionResult.Fail("hand is busy");
        if (CombatFlow.ChoiceOpen()) return ActionResult.Fail("a choice is pending");
        var cm = CombatManager.Instance;
        if (cm.PlayerActionsDisabled) return ActionResult.Fail("player actions are disabled right now");

        int round;
        try { round = cs.RoundNumber; }
        catch (Exception) { return ActionResult.Fail("round unknown"); }
        var sync = RunManager.Instance?.ActionQueueSynchronizer;
        if (sync == null) return ActionResult.Fail("action queue unavailable");
        try
        {
            // turnNumber = combat round, as STS2MCP does (unverified whether PlayerCombatState.TurnNumber is meant).
            sync.RequestEnqueue(new EndPlayerTurnAction(me, round));
        }
        catch (Exception ex) { return ActionResult.Fail("end turn failed: " + ex.Message); }

        bool Taken()
        {
            try
            {
                return cm.IsPlayerReadyToEndTurn(me) || cs.RoundNumber != round || cs.CurrentSide != CombatSide.Player ||
                       !cm.IsPartOfPlayerTurn(me);
            }
            catch (Exception) { return true; }
        }
        var outcome = await CombatFlow.WaitFor(Taken, EndTurnTimeout, ct);
        if (outcome == CombatFlow.WaitOutcome.TimedOut && !ctx.IsMultiplayer && CombatFlow.InPlayWindow(cs, me) &&
            !CombatFlow.AnyActionBusy(out _))
        {
            // Single player only, and only when the queued request evidently never ran: STS2MCP's single-player path.
            // (Never in co-op: PlayerCmd.EndTurn is a local command there.)
            try { PlayerCmd.EndTurn(me, canBackOut: false); }
            catch (Exception ex) { return ActionResult.Fail("end turn failed: " + ex.Message); }
            outcome = await CombatFlow.WaitFor(Taken, EndTurnTimeout, ct);
        }
        return outcome switch
        {
            CombatFlow.WaitOutcome.ChoicePending => ActionResult.Ok("choice pending"),
            CombatFlow.WaitOutcome.Cancelled => ActionResult.Fail("cancelled"),
            CombatFlow.WaitOutcome.TimedOut => ActionResult.Fail("end turn had no effect (timed out)"),
            _ => ActionResult.Ok(),
        };
    }

    // ------------------------------------------------------------------ helpers

    private static TargetType SafeTargetType(CardModel card)
    {
        try { return card.TargetType; }
        catch (Exception) { return TargetType.None; }
    }

    private static T? SafeGet<T>(Func<T?> get) where T : class
    {
        try { return get(); }
        catch (Exception) { return null; }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); }
        catch (Exception) { return 0; }
    }

    private static void Part(Action read)
    {
        try { read(); }
        catch (Exception) { /* fail soft: this field stays at its default */ }
    }
}
