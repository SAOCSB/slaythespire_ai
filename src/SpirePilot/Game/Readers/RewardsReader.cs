using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>Combat rewards screen (claim each reward / proceed) via Read, and the card reward screen (pick a card / skip / reroll) via ReadCardReward.</summary>
internal static class RewardsReader
{
    /// <summary>Card rewards whose card screen was skipped: re-opening them would only loop.</summary>
    private static readonly ConditionalWeakTable<Reward, object> SkippedCardRewards = new();

    /// <summary>The card reward whose selection screen was opened last (to attribute a skip to it).</summary>
    private static WeakReference<Reward>? _lastOpenedCardReward;

    // ================================================================== combat rewards screen

    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var screen = probe.As<NRewardsScreen>();
        if (!SceneUtil.Alive(screen)) return;
        var info = new RewardInfo();
        snap.Rewards = info;
        var me = ctx.Me;
        bool freeSlot = SafeHasFreeSlot(me);

        if (WaitingOverlayVisible(screen!))
        {
            // Already proceeded (co-op: the overlay covers the screen until the others have finished their rewards).
            RoomUtil.Note(snap, RoomUtil.T("rooms.rewards_waiting"));
            return;
        }

        var age = RoomUtil.Age(screen);
        // Where skipping is not allowed, a skipped card reward must be taken after all (proceed stays disabled until then).
        bool skipDisallowed = SkipDisallowed(screen!);
        var rows = RewardButtons(screen!);
        int unclaimed = 0, disabledRows = 0;
        PotionReward? blockedPotion = null;
        var claims = new List<(string label, string key, ActionExecutor exec, Dictionary<string, string> args, bool unpredictable)>();

        for (int i = 0; i < rows.Count; i++)
        {
            var button = rows[i];
            try
            {
                var reward = button.Reward;
                if (reward == null) continue;
                var item = new RewardItemInfo { Index = i, Kind = KindName(reward), Description = Describe(reward) };
                if (reward is GoldReward gold) item.Amount = SafeGold(gold);
                if (reward.ParentRewardSet != null) item.Description += " " + RoomUtil.T("rooms.linked");
                info.Items.Add(item);

                if (!button.IsEnabled)
                {
                    item.Description += " " + RoomUtil.T("rooms.disabled");
                    disabledRows++;
                    continue;
                }
                if (reward is CardReward && !skipDisallowed && SkippedCardRewards.TryGetValue(reward, out _))
                {
                    item.Description += " " + RoomUtil.T("rooms.skipped");
                    continue;
                }
                unclaimed++;
                // A potion row stays enabled with a full belt but swallows the click: only offer it with a free slot.
                if (reward is PotionReward potionReward && !freeSlot)
                {
                    blockedPotion ??= potionReward;
                    RoomUtil.Note(snap, RoomUtil.T("rooms.potion_belt_full", item.Description));
                    continue;
                }
                var captured = reward;
                claims.Add((RoomUtil.T("act.claim_reward", item.Description), "reward:" + KindKey(reward) + ":" + RewardId(reward),
                    ct => Claim(screen!, button, captured, ct),
                    new Dictionary<string, string> { [ActionArgs.Index] = i.ToString(), ["kind"] = item.Kind },
                    reward is CardReward or CardRemovalReward or SpecialCardReward or LinkedRewardSet));
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn("reward-row", "reward row unreadable: " + ex.Message);
            }
        }

        foreach (var (label, key, exec, args, unpredictable) in claims)
            sink.Add(ActionKind.ClaimReward, label, key, exec, args, unpredictable);

        if (blockedPotion != null && me != null)
        {
            var rewardText = Describe(blockedPotion);
            // Drinking a potion that can be used here frees the slot without wasting it.
            try { PotionReader.AddDrinksForReward(sink, me, rewardText); }
            catch (Exception ex) { ThrottledLog.Warn("reward-drink", "potion drink actions failed: " + ex.Message); }
            try { AddPotionDiscards(sink, me, rewardText); }
            catch (Exception ex) { ThrottledLog.Warn("reward-discard", "potion discard actions failed: " + ex.Message); }
        }

        // Rows that are still disabled right after opening are animating in: a lone "proceed" now would be taken as
        // trivial and forfeit them.
        if (sink.Count == 0 && disabledRows > 0 && age < TimeSpan.FromSeconds(2.5))
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }

        var proceed = ProceedButton(screen!);
        if (SceneUtil.Usable(proceed))
        {
            var label = unclaimed > 0 ? RoomUtil.T("rooms.proceed_forfeit", unclaimed) : RoomUtil.T("act.proceed");
            sink.Add(ActionKind.Proceed, label, "proceed", ct => Proceed(screen!, ct), unpredictable: true);
            return;
        }

        // Dead end: nothing to claim, proceed disabled (_disableProceedForever / _skipDisallowed), no waiting overlay.
        // Without an action here the AI would wait forever.
        if (sink.Count == 0 && age >= DeadEndAfter)
        {
            try { AddDeadEndRecovery(screen!, skipDisallowed, sink); }
            catch (Exception ex) { ThrottledLog.Warn("reward-dead-end", "rewards dead-end recovery failed: " + ex.Message); }
        }
    }

    /// <summary>A rewards screen must look stuck at least this long before a recovery action is offered.</summary>
    private static readonly TimeSpan DeadEndAfter = TimeSpan.FromSeconds(1.5);

    private static bool SkipDisallowed(NRewardsScreen screen) => Reflect.GetOptional<object>(screen, "_skipDisallowed") is true;

    /// <summary>0.111 only (set when the room exits under the screen); absent in 0.107.</summary>
    private static bool ProceedDisabledForever(NRewardsScreen screen) => Reflect.GetOptional<object>(screen, "_disableProceedForever") is true;

    /// <summary>
    /// Recovery for a stuck rewards screen (behaviour after STS2-Agent's empty-rewards escape): re-open a skipped card
    /// reward when skipping is not allowed here, else one "leave" action that asks the screen to re-enable its proceed
    /// button, presses it, and as a last resort proceeds through <see cref="RunManager.ProceedFromTerminalRewardsScreen"/>.
    /// </summary>
    private static void AddDeadEndRecovery(NRewardsScreen screen, bool skipDisallowed, ActionSink sink)
    {
        if (UiActions.GameBusy()) return;
        try { if (NMapScreen.Instance is { IsTraveling: true }) return; }
        catch (Exception) { }

        if (skipDisallowed && SkippedCardRewardButton(screen) is { } skipped)
        {
            var reward = skipped.Reward;
            var label = RoomUtil.T("rooms.rewards_reopen_skipped", reward != null ? Describe(reward) : "");
            sink.Add(ActionKind.ClaimReward, label, "reward:card:" + (reward != null ? RewardId(reward) : "skipped"),
                ct => ReopenSkipped(screen, skipped, ct), new Dictionary<string, string> { ["kind"] = "Card" }, unpredictable: true);
            return;
        }
        // The room exiting under the screen sets _disableProceedForever: give the game a moment to take the screen away.
        if (ProceedDisabledForever(screen) && RoomUtil.Age(screen) < TimeSpan.FromSeconds(5)) return;
        sink.Add(ActionKind.Proceed, RoomUtil.T("rooms.rewards_recover"), "proceed", ct => RecoverProceed(screen, ct), unpredictable: true);
    }

    /// <summary>
    /// The skipped card reward's row, also when the screen hid it: from the screen's private skipped-rows list, else any
    /// row (visible or not) whose card reward was skipped. Only rows still in the tree and enabled count.
    /// </summary>
    private static NRewardButton? SkippedCardRewardButton(NRewardsScreen screen)
    {
        var candidates = new List<NRewardButton>();
        try
        {
            if (Reflect.GetOptional<System.Collections.IEnumerable>(screen, "_skippedRewardButtons") is { } list)
                candidates.AddRange(list.OfType<NRewardButton>());
        }
        catch (Exception) { }
        candidates.AddRange(SceneUtil.Descendants<NRewardButton>(screen, intoMatches: false)
            .Where(b => b.Reward is CardReward r && SkippedCardRewards.TryGetValue(r, out _)));
        return candidates.FirstOrDefault(b => SceneUtil.Alive(b) && b.Reward is CardReward && b.IsEnabled);
    }

    /// <summary>
    /// Clicks a skipped card reward row again. The screen may have hidden the row, so its own click handler is invoked
    /// directly (<see cref="NClickableControl.ForceClick"/>, the same path a mouse release takes).
    /// </summary>
    private static async Task<ActionResult> ReopenSkipped(NRewardsScreen screen, NRewardButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen) || RoomUtil.Left(screen)) return ActionResult.Fail("rewards screen is gone");
        if (!SceneUtil.Alive(button) || button.Reward is not CardReward reward) return ActionResult.Fail("skipped card reward is gone");
        if (!button.IsEnabled) return ActionResult.Fail("skipped card reward is disabled");
        SkippedCardRewards.Remove(reward);
        _lastOpenedCardReward = new WeakReference<Reward>(reward);
        try { button.ForceClick(); }
        catch (Exception ex) { return ActionResult.Fail("re-open card reward: " + ex.Message); }
        var r = await UiActions.Expect(() => RoomUtil.Left(screen) || !SceneUtil.Alive(button) || !button.IsEnabled, "re-open card reward",
            TimeSpan.FromSeconds(5), ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private static async Task<ActionResult> RecoverProceed(NRewardsScreen screen, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen) || RoomUtil.Left(screen)) return ActionResult.Fail("rewards screen is gone");
        bool wasComplete = false;
        try { wasComplete = screen.IsComplete; } catch (Exception) { }
        bool Done() => RoomUtil.Left(screen) || (!wasComplete && SafeComplete(screen)) || WaitingOverlayVisible(screen);

        // 1) Let the screen re-evaluate its proceed button (private, Godot-registered: callable by name).
        try { screen.Call(NRewardsScreen.MethodName.TryEnableProceedButton); }
        catch (Exception ex) { ModLog.Warn("TryEnableProceedButton failed: " + ex.Message); }
        if (await MainThread.WaitUntil(() => Done() || SceneUtil.Usable(ProceedButton(screen)), TimeSpan.FromSeconds(0.5), ct) && !Done())
        {
            var pressed = UiActions.Press(ProceedButton(screen), "proceed");
            if (pressed.Success)
            {
                // The game's own proceed ran: never follow it with a second, programmatic proceed (co-op).
                var r = await UiActions.Expect(Done, "proceed", ct: ct);
                await UiActions.WaitSettled(ct: ct);
                return r;
            }
        }
        if (Done()) return ActionResult.Ok();

        // 2) Last resort: the run manager's own proceed for a terminal rewards screen (what its proceed button ends in).
        if (Reflect.GetOptional<object>(screen, "_isTerminal") is false)
            return ActionResult.Fail("proceed stays disabled on a non-terminal rewards screen");
        var rm = RunManager.Instance;
        if (rm == null) return ActionResult.Fail("no run");
        try
        {
            UiActions.NoteOwnProceed();
            var task = rm.ProceedFromTerminalRewardsScreen();
            task?.ContinueWith(t => ModLog.Warn("ProceedFromTerminalRewardsScreen failed: " + t.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("proceed from rewards: " + ex.Message);
        }
        var result = await UiActions.Expect(Done, "proceed from rewards", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return result;
    }

    private static bool SafeComplete(NRewardsScreen screen)
    {
        try { return screen.IsComplete; }
        catch (Exception) { return false; }
    }

    private static List<NRewardButton> RewardButtons(NRewardsScreen screen)
    {
        var buttons = SceneUtil.Descendants<NRewardButton>(screen, intoMatches: false).Where(b => SceneUtil.VisibleInTree(b)).ToList();
        return SceneUtil.ByScreenPosition(buttons);
    }

    private static NProceedButton? ProceedButton(NRewardsScreen screen) =>
        RoomUtil.NodeOf<NProceedButton>(screen, "_proceedButton") ?? SceneUtil.First<NProceedButton>(screen);

    private static async Task<ActionResult> Claim(NRewardsScreen screen, NRewardButton button, Reward reward, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen) || !SceneUtil.Alive(button)) return ActionResult.Fail("reward is gone");
        if (!ReferenceEquals(button.Reward, reward)) return ActionResult.Fail("reward row changed");
        if (reward is PotionReward && !SafeHasFreeSlot(RoomUtil.Me())) return ActionResult.Fail("potion belt is full");

        int before = EnabledRows(screen);
        if (reward is CardReward) _lastOpenedCardReward = new WeakReference<Reward>(reward);
        var pressed = UiActions.Press(button, "reward");
        if (!pressed.Success) return pressed;

        // Done when the row disappears / disables, the count of claimable rows changes, or a follow-up screen
        // (card reward, card removal grid, relic tutorial modal) replaces the rewards screen.
        var r = await UiActions.Expect(() => !SceneUtil.Alive(button) || !button.IsEnabled || RoomUtil.Left(screen) ||
                                             EnabledRows(screen) != before, "claim reward", ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private static int EnabledRows(NRewardsScreen screen)
    {
        try { return SceneUtil.Alive(screen) ? RewardButtons(screen).Count(b => b.IsEnabled) : -1; }
        catch (Exception) { return -1; }
    }

    private static async Task<ActionResult> Proceed(NRewardsScreen screen, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("rewards screen is gone");
        var button = ProceedButton(screen);
        bool wasComplete = false;
        try { wasComplete = screen.IsComplete; } catch (Exception) { }
        var pressed = UiActions.Press(button, "proceed");
        if (!pressed.Success) return pressed;
        // In co-op a "waiting for other players" overlay may cover the screen until everyone has finished.
        var r = await UiActions.Expect(() => RoomUtil.Left(screen) || (!wasComplete && screen.IsComplete) || WaitingOverlayVisible(screen),
            "proceed", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }

    /// <summary>
    /// The co-op "waiting for other players" overlay is really shown: a multiplayer run, visible in the tree and not
    /// faded out (single player never shows it, and a faded-out overlay must not hide the rewards).
    /// </summary>
    private static bool WaitingOverlayVisible(NRewardsScreen screen)
    {
        try
        {
            return RoomUtil.MultiplayerRun() && RoomUtil.Shown(Reflect.Get<Control>(screen, "_waitingForOtherPlayersOverlay"));
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- potion discard (only to make room for a reward)

    private static void AddPotionDiscards(ActionSink sink, Player me, string rewardText)
    {
        if (!CanRemovePotions(me)) return;
        var slots = me.PotionSlots;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            var potion = slots[slot];
            if (potion == null) continue;
            try { if (potion.IsQueued) continue; }
            catch (Exception) { }
            string name;
            try { name = ModelText.Potion(potion, slot, false)?.Name ?? ""; }
            catch (Exception) { name = ""; }
            if (name.Length == 0) name = ModelText.SafeId(potion);
            int s = slot;
            sink.Add(ActionKind.DiscardPotion, RoomUtil.T("rooms.discard_for_reward", name, rewardText), "discard_potion:" + ModelText.SafeId(potion),
                ct => DiscardPotion(potion, s, ct),
                new Dictionary<string, string> { [ActionArgs.Slot] = s.ToString() });
        }
    }

    private static bool CanRemovePotions(Player me)
    {
        try
        {
#if STS2_V0107
            return me.CanRemovePotions && me.Creature is { IsDead: false };
#else
            return me.CanUseOrRemovePotions && me.Creature is { IsDead: false };
#endif
        }
        catch (Exception) { return false; }
    }

    private static async Task<ActionResult> DiscardPotion(PotionModel potion, int slot, CancellationToken ct)
    {
        var rm = RunManager.Instance;
        var me = RoomUtil.Me();
        if (rm == null || me == null) return ActionResult.Fail("no local player");
        bool Gone()
        {
            try
            {
                var slots = me.PotionSlots;
                return slot >= slots.Count || !ReferenceEquals(slots[slot], potion);
            }
            catch (Exception) { return false; }
        }
        if (Gone()) return ActionResult.Fail("potion is no longer in that slot");
        if (!CanRemovePotions(me)) return ActionResult.Fail("potions cannot be removed right now");
        try
        {
            // The networked, co-op safe path (the action queue), not PotionCmd.Discard.
            rm.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(me, (uint)slot, RoomUtil.CombatInProgress()));
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("discard potion: " + ex.Message);
        }
        var r = await UiActions.Expect(Gone, "discard potion", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }

    // ================================================================== card reward overlay

    public static void ReadCardReward(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var screen = probe.As<NCardRewardSelectionScreen>();
        if (!SceneUtil.Alive(screen)) return;
        var info = new RewardInfo();
        snap.Rewards = info;
        var age = RoomUtil.Age(screen);

        var cards = RewardCards(screen!);
        for (int i = 0; i < cards.Count; i++)
        {
            try
            {
                var card = cards[i];
                var ci = RoomUtil.CardInfo(card, "r" + i, false);
                if (!ci.Upgraded) ci.UpgradedDescription = ModelText.UpgradePreview(card, ci.Description, inCombat: false);
                info.CardChoices.Add(ci);
                sink.Add(ActionKind.PickRewardCard, RoomUtil.T("act.pick_card", RoomUtil.CardText(ci)), "cardreward:" + RoomUtil.CardKey(card),
                    ct => PickCard(screen!, card, ct),
                    new Dictionary<string, string> { [ActionArgs.Index] = i.ToString(), [ActionArgs.Card] = "r" + i },
                    unpredictable: true);
            }
            catch (Exception ex)
            {
                ThrottledLog.Warn("card-reward", "card reward option unreadable: " + ex.Message);
            }
        }
        if (cards.Count == 0 && age < TimeSpan.FromSeconds(3))
        {
            // Holders spawn a few frames after opening; a lone "skip" now would be taken as trivial.
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }

        try { AddAlternatives(screen!, info, sink); }
        catch (Exception ex) { ThrottledLog.Warn("card-reward-alt", "card reward alternatives unreadable: " + ex.Message); }
    }

    /// <summary>The offered cards, in on-screen order (model list first; holders as fallback).</summary>
    private static List<CardModel> RewardCards(NCardRewardSelectionScreen screen)
    {
        var holders = SceneUtil.ByScreenPosition(SceneUtil.Descendants<NCardHolder>(screen, intoMatches: false)
            .Where(h => SceneUtil.VisibleInTree(h) && h.CardModel != null).ToList());
        var fromHolders = holders.Select(h => h.CardModel!).Distinct().ToList();
        var options = Reflect.Get<IReadOnlyList<CardCreationResult>>(screen, "_options");
        if (options == null || options.Count == 0) return fromHolders;
        var fromModel = new List<CardModel>();
        foreach (var o in options)
        {
            try { if (o?.Card is { } c) fromModel.Add(c); }
            catch (Exception) { }
        }
        if (fromModel.Count == 0) return fromHolders;
        // Keep the on-screen order when the holders show exactly the model's cards.
        if (fromHolders.Count == fromModel.Count && fromHolders.All(h => fromModel.Contains(h))) return fromHolders;
        return fromModel;
    }

    private static NCardHolder? HolderFor(NCardRewardSelectionScreen screen, CardModel card)
    {
        try
        {
            var h = screen.GetCardHolder(card);
            if (SceneUtil.Alive(h)) return h;
        }
        catch (Exception) { /* not shown */ }
        return SceneUtil.Descendants<NCardHolder>(screen, intoMatches: false).FirstOrDefault(h => ReferenceEquals(h.CardModel, card));
    }

    private static async Task<ActionResult> PickCard(NCardRewardSelectionScreen screen, CardModel card, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("card reward screen is gone");
        var holder = HolderFor(screen, card);
        if (holder == null) return ActionResult.Fail("card not found on the card reward screen");

        // The screen ignores card clicks for a short moment after opening.
        await RoomUtil.WaitClickable(screen, holder, ct);
        bool Done() => RoomUtil.Left(screen) || RoomUtil.CompletionDone(screen);
        var r = UiActions.ClickHolder(holder, "card reward");
        if (!r.Success) return r;
        if (!await MainThread.WaitUntil(Done, TimeSpan.FromSeconds(1.5), ct) && SceneUtil.Alive(holder) && !Done())
            UiActions.ClickHolder(holder, "card reward"); // the first click fell into the opening guard
        r = await UiActions.Expect(Done, "pick card", ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private enum AltKind { Skip, Reroll, Other }

    private static void AddAlternatives(NCardRewardSelectionScreen screen, RewardInfo info, ActionSink sink)
    {
        var pairs = AlternativeButtons(screen, out bool anyMapped);
        bool skipSeen = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (button, alt) = pairs[i];
            if (!SceneUtil.VisibleInTree(button)) continue;
            var text = AltText(button, alt);
            var kind = Classify(alt, text, first: !skipSeen && !anyMapped);
            if (kind == AltKind.Skip)
            {
                if (skipSeen) kind = AltKind.Other;
                skipSeen = true;
                info.CanSkipCards = true;
            }
            if (!button.IsEnabled) continue;
            switch (kind)
            {
                case AltKind.Skip:
                    sink.Add(ActionKind.SkipCardReward, RoomUtil.T("act.skip_cards"), "cardreward:skip", ct => Skip(screen, button, ct), unpredictable: true);
                    break;
                case AltKind.Reroll:
                    sink.Add(ActionKind.ChooseOption, RoomUtil.T("rooms.reroll", text), "cardreward:reroll", ct => Reroll(screen, button, ct),
                        new Dictionary<string, string> { [ActionArgs.Index] = i.ToString() }, unpredictable: true);
                    break;
                default:
                    var id = alt?.OptionId is { Length: > 0 } oid ? oid : text;
                    sink.Add(ActionKind.ChooseOption, RoomUtil.T("rooms.card_alt", text), "cardreward:alt:" + id, ct => Alternative(screen, button, ct),
                        new Dictionary<string, string> { [ActionArgs.Index] = i.ToString() }, unpredictable: true);
                    break;
            }
        }
    }

    /// <summary>
    /// The alternative buttons (skip / reroll / relic options) with their model option. The screen creates one button per
    /// <c>_extraOptions</c> entry in <c>_rewardAlternativesContainer</c>, so the live children in child order pair with the
    /// options by index (screen positions are unreliable while the buttons tween in). Each pair is cross-checked against the
    /// option's localized title; a contradicted pair is re-matched by title, and a button no option matches stays unpaired.
    /// </summary>
    private static List<(NCardRewardAlternativeButton button, CardRewardAlternative? alt)> AlternativeButtons(NCardRewardSelectionScreen screen,
        out bool anyMapped)
    {
        anyMapped = false;
        var extra = Reflect.GetOptional<IReadOnlyList<CardRewardAlternative>>(screen, "_extraOptions");
        var container = RoomUtil.OptionalNodeOf<Control>(screen, "_rewardAlternativesContainer");
        List<NCardRewardAlternativeButton> buttons;
        bool childOrder = SceneUtil.Alive(container);
        if (childOrder)
        {
            buttons = new List<NCardRewardAlternativeButton>();
            foreach (var child in container!.GetChildren())
                if (child is NCardRewardAlternativeButton b && SceneUtil.Alive(b)) buttons.Add(b);
        }
        else
        {
            buttons = SceneUtil.ByScreenPosition(SceneUtil.Descendants<NCardRewardAlternativeButton>(screen, intoMatches: false).Where(b => SceneUtil.Alive(b)).ToList());
        }

        var result = buttons.Select(b => (b, (CardRewardAlternative?)null)).ToList();
        if (extra == null || extra.Count == 0) return result;
        var used = new HashSet<int>();
        // Index pairing only from the container's child order (the game's creation order), never from screen positions.
        if (childOrder && extra.Count == buttons.Count)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (TitleMatches(buttons[i], extra[i]) == false) continue;
                result[i] = (buttons[i], extra[i]);
                used.Add(i);
            }
        }
        // Remaining buttons: match by title.
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].Item2 != null) continue;
            int found = -1;
            for (int k = 0; k < extra.Count; k++)
            {
                if (used.Contains(k) || TitleMatches(buttons[i], extra[k]) != true) continue;
                if (found >= 0) { found = -2; break; } // ambiguous
                found = k;
            }
            if (found < 0) continue;
            result[i] = (buttons[i], extra[found]);
            used.Add(found);
        }
        anyMapped = result.Any(r => r.Item2 != null);
        return result;
    }

    /// <summary>
    /// Whether a button shows the option's title: true / false, or null when either text is unavailable. Compares the
    /// button's label and its private option name with the option's localized title and id.
    /// </summary>
    private static bool? TitleMatches(NCardRewardAlternativeButton button, CardRewardAlternative alt)
    {
        string title = "";
        try { title = ModelText.Loc(alt.Title); } catch (Exception) { }
        var label = RoomUtil.LabelText(RoomUtil.OptionalNodeOf<Control>(button, "_label", "Label"));
        var optionName = Reflect.GetOptional<string>(button, "_optionName") ?? "";
        string id = "";
        try { id = alt.OptionId ?? ""; } catch (Exception) { }
        bool any = false;
        foreach (var shown in new[] { label, optionName })
        {
            if (Norm(shown).Length == 0) continue;
            foreach (var expected in new[] { title, id })
            {
                if (Norm(expected).Length == 0) continue;
                any = true;
                if (SameText(shown, expected)) return true;
            }
        }
        return any ? false : null;
    }

    private static string Norm(string? text) =>
        new string(ModelText.Clean(text ?? "").Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray()).ToLowerInvariant();

    private static bool SameText(string a, string b)
    {
        var x = Norm(a);
        var y = Norm(b);
        return x.Length > 0 && y.Length > 0 && (x == y || x.Contains(y) || y.Contains(x));
    }

    private static string AltText(NCardRewardAlternativeButton button, CardRewardAlternative? alt)
    {
        var text = RoomUtil.LabelText(RoomUtil.OptionalNodeOf<Control>(button, "_label", "Label"));
        if (text.Length == 0 && alt != null) text = ModelText.Loc(alt.Title);
        if (text.Length == 0) text = alt?.OptionId ?? button.Name.ToString();
        return text;
    }

    /// <summary>
    /// Skip closes the overlay but leaves the reward row (EndSelectionAndDoNotCompleteReward); reroll keeps the overlay
    /// open with new cards (DoNothing). Option ids are not in the API dump, so the id is only a secondary hint.
    /// Without the model list, the first alternative is the skip (with a reroll relic the order is [Skip, Reroll]).
    /// </summary>
    private static AltKind Classify(CardRewardAlternative? alt, string text, bool first)
    {
        if (alt != null)
        {
            var id = alt.OptionId ?? "";
            if (id.Contains("REROLL", StringComparison.OrdinalIgnoreCase)) return AltKind.Reroll;
            if (id.Contains("SKIP", StringComparison.OrdinalIgnoreCase)) return AltKind.Skip;
            try
            {
                if (alt.AfterSelected == PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward) return AltKind.Skip;
                if (alt.AfterSelected == PostAlternateCardRewardAction.DoNothing) return AltKind.Reroll;
            }
            catch (Exception) { }
            return AltKind.Other;
        }
        if (text.Contains("reroll", StringComparison.OrdinalIgnoreCase) || text.Contains("重")) return AltKind.Reroll;
        return first ? AltKind.Skip : AltKind.Other;
    }

    private static async Task<ActionResult> Skip(NCardRewardSelectionScreen screen, NCardRewardAlternativeButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("card reward screen is gone");
        var pressed = UiActions.Press(button, "skip card reward");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => RoomUtil.Left(screen), "skip card reward", ct: ct);
        if (r.Success) MarkSkipped(screen);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private static async Task<ActionResult> Reroll(NCardRewardSelectionScreen screen, NCardRewardAlternativeButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("card reward screen is gone");
        var before = RewardCards(screen);
        var pressed = UiActions.Press(button, "reroll");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() =>
        {
            if (RoomUtil.Left(screen)) return true;
            var now = RewardCards(screen);
            return now.Count > 0 && (now.Count != before.Count || now.Where((c, i) => !ReferenceEquals(c, before[i])).Any());
        }, "reroll", ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private static async Task<ActionResult> Alternative(NCardRewardSelectionScreen screen, NCardRewardAlternativeButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("card reward screen is gone");
        var before = RewardCards(screen);
        var pressed = UiActions.Press(button, "card reward option");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => RoomUtil.Left(screen) || !SceneUtil.Alive(button) || !button.IsEnabled ||
                                             !RewardCards(screen).SequenceEqual(before), "card reward option", ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    /// <summary>Remembers the skipped card reward so the rewards screen stops offering to re-open it.</summary>
    private static void MarkSkipped(NCardRewardSelectionScreen screen)
    {
        try
        {
            Reward? reward = null;
            var rewardsScreen = screen.GetParent() is { } parent ? SceneUtil.First<NRewardsScreen>(parent) : null;
            if (SceneUtil.Alive(rewardsScreen))
            {
                // CardReward keeps the screen it opened in a private field.
                reward = RewardButtons(rewardsScreen!).Select(b => b.Reward)
                    .FirstOrDefault(rw => rw is CardReward && ReferenceEquals(Reflect.GetOptional<NCardRewardSelectionScreen>(rw, "_currentlyShownScreen"), screen));
            }
            if (reward == null && _lastOpenedCardReward != null && _lastOpenedCardReward.TryGetTarget(out var last)) reward = last;
            if (reward != null) SkippedCardRewards.AddOrUpdate(reward, true);
        }
        catch (Exception) { /* best effort */ }
    }

    // ================================================================== reward text

    private static string KindName(Reward reward) => reward switch
    {
        GoldReward => "Gold",
        PotionReward => "Potion",
        RelicReward => "Relic",
        CardReward => "Card",
        SpecialCardReward => "SpecialCard",
        CardRemovalReward => "RemoveCard",
        LinkedRewardSet => "LinkedRewardSet",
        _ => reward.GetType().Name,
    };

    private static string KindKey(Reward reward) => reward switch
    {
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardReward => "card",
        SpecialCardReward => "special_card",
        CardRemovalReward => "remove_card",
        LinkedRewardSet => "linked",
        _ => reward.GetType().Name,
    };

    /// <summary>Model-based identity of a reward row (never its position).</summary>
    private static string RewardId(Reward reward)
    {
        try
        {
            switch (reward)
            {
                case GoldReward g: return SafeGold(g)?.ToString() ?? "gold";
                case PotionReward p: return p.Potion != null ? ModelText.SafeId(p.Potion) : "potion";
                case RelicReward r: return r.Relic != null ? ModelText.SafeId(r.Relic) : "relic";
                case CardReward c:
                    var ids = c.Cards.Select(RoomUtil.CardKey).ToList();
                    return ids.Count > 0 ? string.Join(",", ids) : "card";
                case SpecialCardReward s:
                    return Reflect.GetOptional<CardModel>(s, "_card") is { } card ? RoomUtil.CardKey(card) : "special";
                case CardRemovalReward: return "remove";
                case LinkedRewardSet l: return string.Join(",", l.Rewards.Select(x => KindKey(x) + "." + RewardId(x)));
            }
        }
        catch (Exception) { }
        return reward.GetType().Name;
    }

    private static string Describe(Reward reward)
    {
        string desc = "";
        try { desc = ModelText.Loc(reward.Description); }
        catch (Exception) { }
        try
        {
            switch (reward)
            {
                case GoldReward g when SafeGold(g) is { } amount:
                    return desc.Length > 0 ? desc : amount + " gold";
                case PotionReward p when p.Potion != null:
                    Glossary.NotePotion(p.Potion);
                    var pi = ModelText.Potion(p.Potion, -1, false);
                    var pname = pi?.Name ?? ModelText.SafeId(p.Potion);
                    var pdesc = ModelText.Clean(pi?.Description);
                    return pdesc.Length > 0 ? pname + ": " + RoomUtil.Trim(pdesc, 160) : (pname.Length > 0 ? pname : desc);
                case RelicReward r when r.Relic != null:
                    return RoomUtil.RelicText(RoomUtil.RelicInfo(r.Relic));
                case CardReward c:
                    var names = c.Cards.Select(card => RoomUtil.CardName(RoomUtil.CardInfo(card, "", false))).ToList();
                    return names.Count > 0 ? RoomUtil.T("rooms.card_reward", string.Join(" / ", names)) : desc;
                case SpecialCardReward s when Reflect.GetOptional<CardModel>(s, "_card") is { } card:
                    return RoomUtil.CardText(RoomUtil.CardInfo(card, "", false));
                case LinkedRewardSet l:
                    return string.Join(" | ", l.Rewards.Select(Describe));
            }
        }
        catch (Exception) { }
        return desc.Length > 0 ? desc : KindName(reward);
    }

    private static int? SafeGold(GoldReward g)
    {
        try { return g.Amount; }
        catch (Exception) { return null; }
    }

    private static bool SafeHasFreeSlot(Player? me)
    {
        try { return me?.HasOpenPotionSlots ?? false; }
        catch (Exception) { return false; }
    }
}
