using System.Diagnostics;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Combat info (hand, piles, enemies, intents) and actions: play card (every valid target), use potion (every valid
/// target incl. teammates), end turn; in the room left behind after a fight, its proceed button. Called for
/// ScreenKind.Combat. FillState is called for every snapshot while in combat (also when a selection screen is open).
/// </summary>
internal static class CombatReader
{
    /// <summary>Budget for a card play to leave the hand and resolve (client round trips included).</summary>
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PotionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndTurnTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Single player: how long the queued end-turn request gets before the direct path is tried.</summary>
    private static readonly TimeSpan SoloEndTurnFirstWait = TimeSpan.FromSeconds(1);
    /// <summary>Undo goes through the host on a client: allow round trips.</summary>
    private static readonly TimeSpan UndoEndTurnTimeout = TimeSpan.FromSeconds(6);
    /// <summary>A combat read slower than this is logged (once): reads run on the main thread several times a second.</summary>
    private static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(4);
    /// <summary>The first reads include JIT compilation and cold caches: not measured.</summary>
    private const int WarmUpReads = 20;
    private static int _fillCount, _readCount;
    private static bool _slowFillLogged, _slowReadLogged;

    /// <summary>
    /// Registers the legal combat actions of the local player: card plays (hand order, every valid target), potions
    /// (slot order, every valid target incl. teammates), end turn. Nothing is registered outside the local player's
    /// play window (enemy turn, dead), the readiness gate explains why. In co-op after readying end turn while
    /// teammates still play, the one action is "undo end turn". In a finished combat room: its proceed button.
    /// </summary>
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        CombatStrings.Ensure();
        if (probe.CombatFinished)
        {
            try { AddFinishedRoomProceed(probe, sink); }
            catch (Exception ex) { ThrottledLog.Warn("combat:proceed", "combat room proceed action failed: " + ex); }
            return;
        }
        var sw = Stopwatch.StartNew();
        var view = CombatView.Create(ctx);
        if (view == null) return;
        if (view.IsMultiplayer && CombatFlow.CanUndoEndTurn(ctx, view.Cs, view.Me))
        {
            try { AddUndoEndTurn(sink); }
            catch (Exception ex) { ThrottledLog.Warn("combat:undoendturn", "undo end turn action failed: " + ex); }
            return;
        }
        if (!CombatFlow.InPlayWindow(view.Cs, view.Me)) return;

        try { AddCardPlays(view, snap, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:cards", "combat card actions failed: " + ex); }
        try { AddPotions(view, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:potions", "combat potion actions failed: " + ex); }
        try { AddEndTurn(view, sink); }
        catch (Exception ex) { ThrottledLog.Warn("combat:endturn", "end turn action failed: " + ex); }

        if (_readCount <= WarmUpReads) _readCount++;
        else if (sw.Elapsed > SlowRead && !_slowReadLogged)
        {
            _slowReadLogged = true;
            ModLog.Info($"combat actions took {sw.Elapsed.TotalMilliseconds:0.0} ms ({sink.Count} actions); logged once");
        }
    }

    /// <summary>Fills <see cref="GameSnapshot.Combat"/> (round, hand, piles, enemies) whenever combat is in progress.</summary>
    public static void FillState(GameContext ctx, GameSnapshot snap)
    {
        var cs = ctx.Combat;
        if (cs == null) return;
        long t0 = Stopwatch.GetTimestamp();
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

        long tHand = t0, tPiles = t0;
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
                    CardInfo ci;
                    try { ci = ModelText.Card(card, "h" + i, inCombat: true); }
                    catch (Exception) { ci = new CardInfo { Key = "h" + i, Id = ModelText.SafeId(card) }; }
                    info.Hand.Add(ci);
                    // Keyword explanations for the cards the model can play (cached per card id, upgrade, enchantment
                    // and affliction: those decide which tips a card has).
                    if (Glossary.Collecting && ci.Id.Length > 0)
                    {
                        var c = card;
                        string extra = "";
                        try { extra = ModelText.SafeId(card.Enchantment) + "/" + ModelText.SafeId(card.Affliction); }
                        catch (Exception) { }
                        Glossary.Note("card:" + ci.Id + "+" + ModelText.UpgradeLevel(card) + "~" + extra, () => c.HoverTips);
                    }
                }
            });
            tHand = Stopwatch.GetTimestamp();
            Part(() => info.DrawPile = Pile(pcs.DrawPile?.Cards, sortByName: true));
            Part(() => info.DiscardPile = Pile(pcs.DiscardPile?.Cards, sortByName: false));
            Part(() => info.ExhaustPile = Pile(pcs.ExhaustPile?.Cards, sortByName: false));
            Part(() => info.DrawPileCount = pcs.DrawPile?.Cards.Count ?? 0);
            Part(() => info.DiscardPileCount = pcs.DiscardPile?.Cards.Count ?? 0);
            Part(() => info.ExhaustPileCount = pcs.ExhaustPile?.Cards.Count ?? 0);
            tPiles = Stopwatch.GetTimestamp();
        }

        Part(() =>
        {
            foreach (var e in CombatKeys.Enemies(cs))
            {
                try { info.Enemies.Add(ModelText.Creature(e.Creature, e.Key)); }
                catch (Exception) { info.Enemies.Add(new CreatureInfo { Key = e.Key, CombatId = SafeCombatId(e.Creature), Id = e.MonsterId, IsAlive = false }); }
            }
        });
        snap.Combat = info;

        var total = Stopwatch.GetElapsedTime(t0);
        if (_fillCount <= WarmUpReads) _fillCount++;
        else if (total > SlowRead && !_slowFillLogged)
        {
            _slowFillLogged = true;
            ModLog.Info($"combat state read took {total.TotalMilliseconds:0.0} ms (hand {Ms(t0, tHand)}, piles {Ms(tHand, tPiles)}, " +
                        $"enemies {Ms(tPiles, Stopwatch.GetTimestamp())}; {info.Hand.Count} hand, " +
                        $"{info.DrawPileCount + info.DiscardPileCount + info.ExhaustPileCount} pile cards, {info.Enemies.Count} enemies); logged once");
        }
    }

    private static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pile contents: names, types and costs only (the prompt lists pile cards by name; rules text would cost a preview
    /// per card on every snapshot). The draw pile is sorted by name: its real order is hidden information.
    /// </summary>
    private static List<CardInfo> Pile(IReadOnlyList<CardModel>? cards, bool sortByName)
    {
        var list = new List<CardInfo>();
        if (cards == null) return list;
        foreach (var card in cards)
        {
            if (card == null) continue;
            try { list.Add(ModelText.PileCard(card, inCombat: true)); }
            catch (Exception) { }
        }
        if (sortByName)
            list = list.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        return list;
    }

    // ------------------------------------------------------------------ card plays

    /// <summary>
    /// Identical hand cards (same semantic identity and text) collapse into one menu line per target; the line's key is
    /// "play:" + <see cref="CardKeys.Semantic"/> + "@" + target, so plan steps and re-validation find the same variant again.
    /// </summary>
    private sealed class HandGroup
    {
        public List<(CardModel Card, int Index)> Cards { get; } = new();
        public CardInfo Info { get; init; } = new();
        public string CardKey { get; set; } = "";
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
            string cardKey = CardKeys.Semantic(card);
            // The text joins the grouping (not the key): copies whose text differs must not share one menu line.
            string sig = string.Join("|", cardKey, info.Cost, info.StarCost, info.Description, string.Join(",", info.Keywords));
            if (!bySignature.TryGetValue(sig, out var group))
            {
                group = new HandGroup { Info = info, CardKey = cardKey };
                bySignature[sig] = group;
                groups.Add(group);
            }
            group.Cards.Add((card, i));
        }
        // Keys must stay unique in one menu: a second group with the same identity (text differs for a reason the key
        // does not capture) gets "#n".
        var keyCounts = new Dictionary<string, int>();
        foreach (var group in groups)
        {
            keyCounts.TryGetValue(group.CardKey, out int n);
            keyCounts[group.CardKey] = n + 1;
            if (n > 0) group.CardKey += "#" + n;
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
            string idKey = "play:" + group.CardKey + "@";

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

        // "Left the hand at some point" (a card may come back to the hand through its own effect). Done once the local
        // player's actions settled, or the play is parked waiting for a choice.
        bool left = false;
        bool Played()
        {
            if (!SafeBool(() => card.Pile?.Type == PileType.Hand)) left = true;
            return left && CombatFlow.LocalQueue(me, playerDrivenOnly: true, out _) != CombatFlow.QueueState.Busy;
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
                return !left && !CombatFlow.PlayerDrivenBusy(me)
                    ? ActionResult.Fail("card play had no effect (timed out)")
                    : ActionResult.Ok("still resolving");
        }
        await MainThread.Frames(2);
        return ParkedForChoice(me) ? ActionResult.Ok("waiting for a choice") : ActionResult.Ok();
    }

    /// <summary>The local player's action waits for a choice that no local prompt shows (yet).</summary>
    private static bool ParkedForChoice(Player me) =>
        CombatFlow.LocalQueue(me, playerDrivenOnly: true, out _) == CombatFlow.QueueState.OwnChoice && !CombatFlow.ChoiceOpen();

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
        var outcome = await CombatFlow.WaitFor(
            () => Gone() && CombatFlow.LocalQueue(me, playerDrivenOnly: true, out _) != CombatFlow.QueueState.Busy, PotionTimeout, ct);
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
        return ParkedForChoice(me) ? ActionResult.Ok("waiting for a choice") : ActionResult.Ok();
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
    /// <para>When the request evidently never ran (nothing of the local player's runs or waits, still in the play
    /// window, not marked ready): single player calls <see cref="PlayerCmd.EndTurn"/> after a short wait (STS2MCP's
    /// single-player path); co-op asks again with <c>PlayerCombatState.TurnNumber</c> when it differs from the round
    /// (the request is stamped with a turn number and a stale one is dropped), and otherwise presses the vanilla end
    /// turn button (its own release logic, which is what a click runs).</para>
    /// </summary>
    private static async Task<ActionResult> EndTurn(CancellationToken ct)
    {
        var ctx = GameContext.Capture();
        var cs = ctx.Combat;
        var me = ctx.Me;
        if (cs == null || me == null) return ActionResult.Fail("not in combat");
        if (!CombatFlow.InPlayWindow(cs, me)) return ActionResult.Fail("turn already ended or not your turn");
        // Mirrors NEndTurnButton.CanTurnBeEnded (private): not while a card is in play or the hand is selecting.
        if (HandBusy()) return ActionResult.Fail("hand is busy");
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
            // turnNumber = combat round, as STS2MCP does; PlayerCombatState.TurnNumber is the retry below.
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
        // The request evidently did not run: still this player's play window (not marked ready), nothing of this
        // player's runs, waits to run or waits for a choice, in co-op nothing at all runs (our request might sit behind
        // a teammate's action), no prompt.
        bool NotTaken() => CombatFlow.InPlayWindow(cs, me) && !HandBusy() && !CombatFlow.ChoiceOpen() &&
                           CombatFlow.LocalQueue(me, playerDrivenOnly: false, out _) == CombatFlow.QueueState.Idle &&
                           (!ctx.IsMultiplayer || CombatFlow.ExecutorIdle());

        bool multiplayer = ctx.IsMultiplayer;
        var outcome = await CombatFlow.WaitFor(Taken, multiplayer ? EndTurnTimeout : SoloEndTurnFirstWait, ct);
        if (outcome == CombatFlow.WaitOutcome.TimedOut && !NotTaken())
        {
            // Still resolving (the request may sit behind running actions): give it more time.
            outcome = await CombatFlow.WaitFor(Taken, EndTurnTimeout, ct);
        }
        // Taken, cancelled, a prompt, or still busy after the whole budget: nothing more to try.
        bool dropped = outcome == CombatFlow.WaitOutcome.TimedOut && NotTaken();
        if (dropped && !multiplayer)
        {
            // Single player only: PlayerCmd.EndTurn is a local command (never in co-op).
            try { PlayerCmd.EndTurn(me, canBackOut: false); }
            catch (Exception ex) { return ActionResult.Fail("end turn failed: " + ex.Message); }
            outcome = await CombatFlow.WaitFor(Taken, EndTurnTimeout, ct);
        }
        else if (dropped)
        {
            bool retried = false;
            int turn = SafeTurnNumber(me);
            if (turn > 0 && turn != round)
            {
                try
                {
                    sync.RequestEnqueue(new EndPlayerTurnAction(me, turn));
                    retried = true;
                }
                catch (Exception ex) { ModLog.Warn("end turn retry with turn number failed: " + ex.Message); }
                if (retried) outcome = await CombatFlow.WaitFor(Taken, EndTurnTimeout, ct);
            }
            if (!retried || outcome == CombatFlow.WaitOutcome.TimedOut && NotTaken())
            {
                var pressed = PressEndTurnButton();
                if (!pressed.Success) return ActionResult.Fail("end turn had no effect, and " + pressed.Message);
                outcome = await CombatFlow.WaitFor(() => Taken() || ModalOpen(), EndTurnTimeout, ct);
                if (ModalOpen() && !Taken()) return ActionResult.Fail("the end turn button opened a popup; answer it first");
            }
        }
        return outcome switch
        {
            CombatFlow.WaitOutcome.ChoicePending => ActionResult.Ok("choice pending"),
            CombatFlow.WaitOutcome.Cancelled => ActionResult.Fail("cancelled"),
            CombatFlow.WaitOutcome.TimedOut => ActionResult.Fail("end turn had no effect (timed out)"),
            _ => ActionResult.Ok(),
        };
    }

    private static bool HandBusy()
    {
        var hand = CombatFlow.Hand();
        try { return hand != null && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play); }
        catch (Exception) { return true; }
    }

    private static int SafeTurnNumber(Player me)
    {
        try { return me.PlayerCombatState?.TurnNumber ?? -1; }
        catch (Exception) { return -1; }
    }

    private static bool ModalOpen()
    {
        try { return NModalContainer.Instance?.OpenModal is Node modal && SceneUtil.Alive(modal); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The vanilla end turn button's release logic (what a click runs once the optional hold-to-confirm bar is full),
    /// after the button's own enabled state allows it. Co-op safe: the button goes through the game's synchronized path.
    /// </summary>
    private static ActionResult PressEndTurnButton()
    {
        NEndTurnButton? button = null;
        try
        {
            var room = NCombatRoom.Instance;
            if (SceneUtil.Alive(room))
            {
                try { button = room!.Ui?.EndTurnButton; } catch (Exception) { }
                if (!SceneUtil.Valid(button)) button = SceneUtil.Descendants<NEndTurnButton>(room).FirstOrDefault();
            }
        }
        catch (Exception) { }
        if (!SceneUtil.VisibleInTree(button)) return ActionResult.Fail("the end turn button was not found");
        if (!button!.IsEnabled) return ActionResult.Fail("the end turn button is disabled");
        try
        {
            button.CallReleaseLogic();
            return ActionResult.Ok();
        }
        catch (Exception ex) { return ActionResult.Fail("the end turn button failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ undo end turn (co-op)

    private static void AddUndoEndTurn(ActionSink sink) =>
        sink.Add(ActionKind.UndoEndTurn, L10n.T("act.undo_end_turn"), "undo_end_turn", UndoEndTurn, unpredictable: true);

    /// <summary>
    /// Takes back the local player's end-turn ready in co-op by enqueueing <see cref="UndoEndPlayerTurnAction"/>
    /// through the action queue synchronizer (never the local mutator <c>CombatManager.UndoReadyToEndTurn</c>). Done
    /// when the player is no longer ready in the same round of the player side; fails on timeout or when the turn
    /// ended meanwhile.
    /// </summary>
    private static async Task<ActionResult> UndoEndTurn(CancellationToken ct)
    {
        var ctx = GameContext.Capture();
        var cs = ctx.Combat;
        var me = ctx.Me;
        if (cs == null || me == null) return ActionResult.Fail("not in combat");
        if (!CombatFlow.CanUndoEndTurn(ctx, cs, me)) return ActionResult.Fail("end turn cannot be undone now");
        var cm = CombatManager.Instance;
        int round;
        try { round = cs.RoundNumber; }
        catch (Exception) { return ActionResult.Fail("round unknown"); }
        var sync = RunManager.Instance?.ActionQueueSynchronizer;
        if (sync == null) return ActionResult.Fail("action queue unavailable");
        try
        {
            // Same turnNumber convention as EndPlayerTurnAction (combat round; STS2MCP).
            sync.RequestEnqueue(new UndoEndPlayerTurnAction(me, round));
        }
        catch (Exception ex) { return ActionResult.Fail("undo end turn failed: " + ex.Message); }

        bool SameTurn()
        {
            try { return cm.IsInProgress && cs.RoundNumber == round && cs.CurrentSide == CombatSide.Player; }
            catch (Exception) { return false; }
        }
        bool Undone()
        {
            try { return SameTurn() && !cm.IsPlayerReadyToEndTurn(me); }
            catch (Exception) { return false; }
        }
        var outcome = await CombatFlow.WaitFor(() => Undone() || !SameTurn(), UndoEndTurnTimeout, ct, stopOnChoice: false);
        int turn = SafeTurnNumber(me);
        if (outcome == CombatFlow.WaitOutcome.TimedOut && turn > 0 && turn != round && SameTurn() && !Undone() &&
            CombatFlow.ExecutorIdle())
        {
            // Dropped as stale (the same question as for ending the turn): ask once more with the player's turn number.
            try
            {
                sync.RequestEnqueue(new UndoEndPlayerTurnAction(me, turn));
                outcome = await CombatFlow.WaitFor(() => Undone() || !SameTurn(), UndoEndTurnTimeout, ct, stopOnChoice: false);
            }
            catch (Exception ex) { ModLog.Warn("undo end turn retry with turn number failed: " + ex.Message); }
        }
        if (outcome == CombatFlow.WaitOutcome.Cancelled) return ActionResult.Fail("cancelled");
        if (Undone())
        {
            await MainThread.Frames(2);
            return ActionResult.Ok();
        }
        return ActionResult.Fail(outcome == CombatFlow.WaitOutcome.TimedOut
            ? "undo end turn had no effect (timed out)"
            : "the turn ended before the undo went through");
    }

    // ------------------------------------------------------------------ finished combat room

    /// <summary>
    /// The room left behind after a fight (no rewards, or the rewards overlay closed) shows its own proceed button: the
    /// way on to the map. Offered while the button is usable.
    /// </summary>
    private static void AddFinishedRoomProceed(ScreenProbe probe, ActionSink sink)
    {
        if (probe.Screen is not NCombatRoom room || !SceneUtil.Alive(room)) return;
        if (!SceneUtil.Usable(RoomProceedButton(room)))
        {
            _proceedSince = null;
            return;
        }
        // The button must have stayed usable for a moment: never press one that flashes up while the end-of-fight flow
        // (rewards) is still opening.
        ulong id = room.GetInstanceId();
        long now = Stopwatch.GetTimestamp();
        if (_proceedSince is not { } since || since.Room != id)
        {
            _proceedSince = (id, now);
            return;
        }
        if (Stopwatch.GetElapsedTime(since.At, now) < ProceedSettle) return;
        sink.Add(ActionKind.Proceed, L10n.T("act.combat_proceed"), "proceed", ct => ProceedFromRoom(room, ct), unpredictable: true);
    }

    /// <summary>How long the finished room's proceed button must stay usable before it is offered.</summary>
    private static readonly TimeSpan ProceedSettle = TimeSpan.FromMilliseconds(400);
    private static (ulong Room, long At)? _proceedSince;

    private static NProceedButton? RoomProceedButton(NCombatRoom room)
    {
        try
        {
            var button = room.ProceedButton;
            if (SceneUtil.Valid(button)) return button;
        }
        catch (Exception) { }
        return Reflect.GetOptional<NProceedButton>(room, "_proceedButton");
    }

    /// <summary>
    /// Presses the room's proceed button (its own click path), then waits until the room is left (map opened, next
    /// room), the button stops being usable, or the co-op "waiting for other players" overlay shows.
    /// </summary>
    private static async Task<ActionResult> ProceedFromRoom(NCombatRoom room, CancellationToken ct)
    {
        if (!SceneUtil.Alive(room)) return ActionResult.Fail("the combat room is gone");
        if (CombatFlow.CombatLive()) return ActionResult.Fail("the fight is still running");
        var button = RoomProceedButton(room);
        var pressed = UiActions.Press(button, "proceed");
        if (!pressed.Success) return pressed;
        bool Done()
        {
            if (!SceneUtil.Alive(room) || !SceneUtil.Usable(button)) return true;
            try
            {
                if (!ReferenceEquals(ActiveScreenContext.Instance?.GetCurrentScreen(), room)) return true;
                if (NMapScreen.Instance is { } map && SceneUtil.Alive(map) && map.IsOpen) return true;
            }
            catch (Exception) { }
            return SceneUtil.VisibleInTree(Reflect.GetOptional<Control>(room, "_waitingForOtherPlayersOverlay"));
        }
        var r = await UiActions.Expect(Done, "proceed", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }

    // ------------------------------------------------------------------ helpers

    private static uint? SafeCombatId(Creature creature)
    {
        try { return creature.CombatId; }
        catch (Exception) { return null; }
    }

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
