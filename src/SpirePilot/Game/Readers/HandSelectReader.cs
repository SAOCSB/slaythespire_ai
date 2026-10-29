using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// In-combat hand selection (choose cards from hand to discard/exhaust/upgrade/retain...). Called for
/// ScreenKind.HandSelect. Drives the hand's own select mode (the game syncs the result in co-op).
/// </summary>
internal static class HandSelectReader
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What the open hand selection looks like.</summary>
    private sealed class HandSelection
    {
        public NPlayerHand Hand = null!;
        public NPlayerHand.Mode Mode;
        public CardSelectorPrefs? Prefs;
        /// <summary>Selectable cards still in the hand (visible holders), in hand order.</summary>
        public List<NHandCardHolder> Selectable = new();
        /// <summary>Already selected cards (moved to the selected-card container).</summary>
        public List<(CardModel Card, NCardHolder? Holder)> Selected = new();
        public NConfirmButton? Confirm;

        public int Min => Prefs?.MinSelect ?? 1;
        public int Max => Math.Max(Prefs?.MaxSelect ?? 1, 1);
        public bool ManualConfirm => Prefs?.RequireManualConfirmation ?? false;
    }

    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        CombatStrings.Ensure();
        var sel = Capture();
        if (sel == null) return;

        var info = new SelectionInfo
        {
            Purpose = Purpose(sel),
            Prompt = PromptText(sel),
            MinSelect = sel.Min,
            MaxSelect = sel.Max,
            CanCancel = false,
        };

        int k = 0;
        var selectable = new List<(NHandCardHolder Holder, CardModel Card, string Key)>();
        foreach (var holder in sel.Selectable)
        {
            var card = holder.CardModel;
            string key = "c" + k++;
            info.Candidates.Add(Candidate(card, key));
            selectable.Add((holder, card, key));
        }
        var selected = new List<(CardModel Card, NCardHolder? Holder, string Key)>();
        foreach (var (card, holder) in sel.Selected)
        {
            string key = "c" + k++;
            info.Candidates.Add(Candidate(card, key));
            info.SelectedKeys.Add(key);
            selected.Add((card, holder, key));
        }

        int count = sel.Selected.Count;
        bool confirmUsable = SceneUtil.Usable(sel.Confirm);
        // The game enables its confirm button only for a valid selection; the count check guards against a button
        // that is still fading out. Without readable prefs the button is the only authority.
        info.CanConfirm = confirmUsable && (sel.Prefs == null || count >= sel.Min && count <= sel.Max);
        snap.Selection = info;

        // Picks: only while more cards may be selected. Picking the last allowed card completes the selection by
        // itself unless the prompt wants a manual confirm.
        if (count < sel.Max)
        {
            bool completes = count + 1 >= sel.Max && !sel.ManualConfirm;
            foreach (var (holder, card, key) in selectable)
            {
                var name = ModelText.CardName(card);
                var h = holder;
                var c = card;
                sink.Add(ActionKind.SelectCard, L10n.T("act.select_card", name), "select:" + SemanticCard(card),
                    ct => SelectCard(h, c, ct), new Dictionary<string, string> { [ActionArgs.Card] = key },
                    unpredictable: completes);
            }
        }
        foreach (var (card, holder, key) in selected)
        {
            var name = ModelText.CardName(card);
            var c = card;
            sink.Add(ActionKind.DeselectCard, L10n.T("act.deselect_card", name), "deselect:" + SemanticCard(card),
                ct => DeselectCard(c, ct), new Dictionary<string, string> { [ActionArgs.Card] = key });
        }
        if (info.CanConfirm)
            sink.Add(ActionKind.ConfirmSelection, L10n.T("act.confirm"), "confirm", Confirm, unpredictable: true);
    }

    private static CardInfo Candidate(CardModel card, string key)
    {
        var info = ModelText.Card(card, key, inCombat: true);
        // Playability of a card is irrelevant while choosing it.
        info.Playable = null;
        info.UnplayableReason = null;
        return info;
    }

    private static string SemanticCard(CardModel card) => ModelText.SafeId(card) + "+" + ModelText.UpgradeLevel(card);

    // ------------------------------------------------------------------ reading the hand

    private static HandSelection? Capture()
    {
        if (!ScreenResolver.IsHandSelection()) return null;
        var hand = CombatFlow.Hand();
        if (hand == null) return null;
        var sel = new HandSelection { Hand = hand };
        try { sel.Mode = hand.CurrentMode; } catch (Exception) { }
        // Private prefs (min/max/manual confirm): reflection, tolerated when missing (STS2-Agent reads the same field).
        if (Reflect.Get<object>(hand, "_prefs") is CardSelectorPrefs prefs) sel.Prefs = prefs;

        var selectedCards = SelectedCards(hand);
        try
        {
            var holders = hand.ActiveHolders;
            if (holders != null)
            {
                sel.Selectable = holders
                    .Where(h => SceneUtil.Alive(h) && h.Visible && h.CardModel != null && !selectedCards.Any(s => ReferenceEquals(s.Card, h.CardModel)))
                    .OrderBy(h => h.GetIndex())
                    .ToList();
            }
        }
        catch (Exception) { }
        sel.Selected = selectedCards;
        sel.Confirm = Reflect.Get<NConfirmButton>(hand, "_selectModeConfirmButton")
                      ?? SceneUtil.Get<NConfirmButton>(hand, "%SelectModeConfirmButton");
        return sel;
    }

    /// <summary>
    /// The cards picked so far: the hand's private _selectedCards list, mapped to the holders shown in the selected-card
    /// container (needed to deselect). Falls back to the container alone.
    /// </summary>
    private static List<(CardModel Card, NCardHolder? Holder)> SelectedCards(NPlayerHand hand)
    {
        var result = new List<(CardModel, NCardHolder?)>();
        var holders = new List<NCardHolder>();
        try
        {
            var container = Reflect.Get<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
                            ?? SceneUtil.Get<NSelectedHandCardContainer>(hand, "%SelectedHandCardContainer");
            if (SceneUtil.Alive(container) && container!.Holders != null)
                holders.AddRange(container.Holders.Where(h => SceneUtil.Alive(h) && h.CardModel != null));
        }
        catch (Exception) { }

        var cards = Reflect.Get<List<CardModel>>(hand, "_selectedCards");
        if (cards != null)
        {
            foreach (var c in cards)
                if (c != null) result.Add((c, holders.FirstOrDefault(h => ReferenceEquals(h.CardModel, c))));
        }
        else
        {
            foreach (var h in holders) result.Add((h.CardModel!, h));
        }
        return result;
    }

    private static string Purpose(HandSelection sel)
    {
        if (sel.Mode == NPlayerHand.Mode.UpgradeSelect) return "Upgrade";
        string key = "";
        try { key = (sel.Prefs?.Prompt?.LocEntryKey ?? "").ToUpperInvariant(); } catch (Exception) { }
        try
        {
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.DiscardSelectionPrompt)) return "Discard";
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.ExhaustSelectionPrompt)) return "Exhaust";
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.UpgradeSelectionPrompt)) return "Upgrade";
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.TransformSelectionPrompt)) return "Transform";
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.RemoveSelectionPrompt)) return "Remove";
            if (Same(sel.Prefs?.Prompt, CardSelectorPrefs.EnchantSelectionPrompt)) return "Enchant";
        }
        catch (Exception) { }
        foreach (var word in new[] { "DISCARD", "EXHAUST", "UPGRADE", "RETAIN", "TRANSFORM", "REMOVE", "ENCHANT", "DRAW", "PLAY", "COPY", "DUPLICATE" })
            if (key.Contains(word)) return word[0] + word[1..].ToLowerInvariant();
        return "Choose";
    }

    private static bool Same(MegaCrit.Sts2.Core.Localization.LocString? a, MegaCrit.Sts2.Core.Localization.LocString? b) =>
        a != null && b != null && a.LocEntryKey == b.LocEntryKey && a.LocTable == b.LocTable;

    private static string PromptText(HandSelection sel)
    {
        string text = "";
        try
        {
            var header = Reflect.Get<MegaRichTextLabel>(sel.Hand, "_selectionHeader")
                         ?? SceneUtil.Get<MegaRichTextLabel>(sel.Hand, "%SelectionHeader");
            if (SceneUtil.Alive(header)) text = ModelText.Clean(header!.Text);
        }
        catch (Exception) { }
        if (text.Length == 0) text = ModelText.Loc(sel.Prefs?.Prompt);
        return text;
    }

    // ------------------------------------------------------------------ executors

    /// <summary>
    /// Picks a card the way the hand does on click: the hand's own select-mode handler (private, reached through
    /// Godot's method dispatch by its public StringName, as STS2-Agent does), then its completion check.
    /// </summary>
    private static async Task<ActionResult> SelectCard(NHandCardHolder holder, CardModel card, CancellationToken ct)
    {
        var sel = Capture();
        if (sel == null) return ActionResult.Fail("selection is no longer open");
        if (!SceneUtil.Alive(holder) || !ReferenceEquals(holder.CardModel, card) || !sel.Selectable.Contains(holder))
            return ActionResult.Fail("card is no longer selectable");
        if (sel.Selected.Count >= sel.Max) return ActionResult.Fail("no more cards can be selected");
        int before = sel.Selected.Count;
        try
        {
            var method = sel.Mode == NPlayerHand.Mode.UpgradeSelect
                ? NPlayerHand.MethodName.SelectCardInUpgradeMode
                : NPlayerHand.MethodName.SelectCardInSimpleMode;
            sel.Hand.Call(method, holder);
            if (SceneUtil.Alive(sel.Hand)) sel.Hand.Call(NPlayerHand.MethodName.CheckIfSelectionComplete);
        }
        catch (Exception ex) { return ActionResult.Fail("select failed: " + ex.Message); }

        // Done when the pick registered (selection still open) or the selection completed on its own.
        var outcome = await CombatFlow.WaitFor(() =>
        {
            var now = Capture();
            return now == null || now.Selected.Count != before || now.Selected.Any(s => ReferenceEquals(s.Card, card));
        }, StepTimeout, ct, stopOnChoice: false);
        if (outcome == CombatFlow.WaitOutcome.Cancelled) return ActionResult.Fail("cancelled");
        if (outcome == CombatFlow.WaitOutcome.TimedOut) return ActionResult.Fail("selecting the card had no effect");
        if (Capture() == null) return await AfterSelectionClosed(ct);
        await MainThread.Frames(2);
        return ActionResult.Ok();
    }

    /// <summary>Unpicks a card: clicks its holder in the selected-card container, falling back to NPlayerHand.DeselectCard.</summary>
    private static async Task<ActionResult> DeselectCard(CardModel card, CancellationToken ct)
    {
        var sel = Capture();
        if (sel == null) return ActionResult.Fail("selection is no longer open");
        var entry = sel.Selected.FirstOrDefault(s => ReferenceEquals(s.Card, card));
        if (entry.Card == null) return ActionResult.Fail("card is not selected");

        bool StillSelected() => Capture() is { } now && now.Selected.Any(s => ReferenceEquals(s.Card, card));

        if (entry.Holder != null && SceneUtil.Alive(entry.Holder))
        {
            var click = UiActions.ClickHolder(entry.Holder, "deselect");
            if (click.Success)
            {
                await MainThread.Frames(3);
                if (!StillSelected()) return ActionResult.Ok();
            }
        }
        // Fallback: the hand's public deselect entry point with the card node shown for the selected card.
        try
        {
            var node = entry.Holder?.CardNode ?? sel.Hand.GetCard(card);
            if (!SceneUtil.Valid(node)) return ActionResult.Fail("selected card node not found");
            sel.Hand.DeselectCard(node);
        }
        catch (Exception ex) { return ActionResult.Fail("deselect failed: " + ex.Message); }
        var outcome = await CombatFlow.WaitFor(() => !StillSelected(), StepTimeout, ct, stopOnChoice: false);
        if (outcome == CombatFlow.WaitOutcome.Cancelled) return ActionResult.Fail("cancelled");
        return outcome == CombatFlow.WaitOutcome.TimedOut ? ActionResult.Fail("deselecting had no effect") : ActionResult.Ok();
    }

    /// <summary>Presses the hand's select-mode confirm button, then waits until the selection closed and the effect resolved.</summary>
    private static async Task<ActionResult> Confirm(CancellationToken ct)
    {
        var sel = Capture();
        if (sel == null) return ActionResult.Fail("selection is no longer open");
        int count = sel.Selected.Count;
        if (sel.Prefs != null && (count < sel.Min || count > sel.Max)) return ActionResult.Fail("selection is not complete");
        var pressed = UiActions.Press(sel.Confirm, "confirm selection");
        if (!pressed.Success) return pressed;
        var outcome = await CombatFlow.WaitFor(() => Capture() == null, StepTimeout, ct, stopOnChoice: false);
        if (outcome == CombatFlow.WaitOutcome.Cancelled) return ActionResult.Fail("cancelled");
        if (outcome == CombatFlow.WaitOutcome.TimedOut) return ActionResult.Fail("confirm had no effect (timed out)");
        return await AfterSelectionClosed(ct);
    }

    /// <summary>After the prompt closed: let the parked card effect finish (or stop at the next prompt).</summary>
    private static async Task<ActionResult> AfterSelectionClosed(CancellationToken ct)
    {
        await MainThread.Frames(2);
        var outcome = await CombatFlow.WaitFor(() => !CombatFlow.PlayerDrivenBusy(), StepTimeout, ct);
        if (outcome == CombatFlow.WaitOutcome.ChoicePending) return ActionResult.Ok("choice pending");
        if (outcome == CombatFlow.WaitOutcome.Cancelled) return ActionResult.Fail("cancelled");
        // The selection itself is done; a long effect afterwards is not a failure.
        return ActionResult.Ok(outcome == CombatFlow.WaitOutcome.TimedOut ? "still resolving" : "");
    }
}
