using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms;

/// <summary>
/// "Choose one" overlays, in and out of combat: choose a card (<see cref="NChooseACardSelectionScreen"/>, no confirm,
/// maybe skippable), choose a card pack (<see cref="NChooseABundleSelectionScreen"/>, click → preview → confirm) and
/// choose a relic (<see cref="NChooseARelicSelection"/>, maybe skippable).
/// </summary>
internal static class ChooseScreens
{
    // ================================================================== choose a card

    public static void ReadChooseCard(GameContext ctx, NChooseACardSelectionScreen screen, GameSnapshot snap, ActionSink sink)
    {
        bool inCombat = ctx.Combat != null;
        RoomUtil.Age(screen); // start the opening-guard clock
        var skip = SkipButton(screen);
        bool canSkip = skip != null && Reflect.Get<object>(screen, "_canSkip") is not false;
        var sel = new SelectionInfo
        {
            Purpose = "Choose",
            Prompt = RoomUtil.BannerText(screen),
            MinSelect = canSkip ? 0 : 1,
            MaxSelect = 1,
            CanCancel = canSkip,
        };
        snap.Selection = sel;

        var cards = ChooseCards(screen);
        if (cards.Count == 0)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }
        var used = new HashSet<string>();
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var ci = RoomUtil.CardInfo(card, "c" + i, inCombat);
            sel.Candidates.Add(ci);
            var key = "select:" + RoomUtil.CardKey(card);
            for (int n = 2; !used.Add(key); n++) key = "select:" + RoomUtil.CardKey(card) + "#" + n;
            sink.Add(ActionKind.SelectCard, RoomUtil.T("rooms.choose_card", RoomUtil.CardText(ci)), key,
                ct => PickCard(screen, card, ct),
                new Dictionary<string, string> { [ActionArgs.Card] = ci.Key, [ActionArgs.Index] = i.ToString() },
                unpredictable: true);
        }
        if (canSkip)
        {
            sink.Add(ActionKind.CancelSelection, RoomUtil.T("rooms.skip"), "cancel", ct => PressAndWaitClosed(screen, skip!, "skip", ct), unpredictable: true);
        }
    }

    private static List<CardModel> ChooseCards(NChooseACardSelectionScreen screen)
    {
        var holders = SceneUtil.ByScreenPosition(SceneUtil.Descendants<NCardHolder>(screen, intoMatches: false)
            .Where(h => SceneUtil.VisibleInTree(h) && h.CardModel != null).ToList());
        var shown = holders.Select(h => h.CardModel!).Distinct().ToList();
        if (shown.Count > 0) return shown;
        var model = Reflect.Get<IEnumerable<CardModel>>(screen, "_cards");
        return model?.Where(c => c != null).ToList() ?? new List<CardModel>();
    }

    private static bool ChooseDone(NChooseACardSelectionScreen screen) =>
        RoomUtil.Left(screen) || RoomUtil.CompletionDone(screen) || Reflect.Get<object>(screen, "_cardSelected") is true;

    private static async Task<ActionResult> PickCard(NChooseACardSelectionScreen screen, CardModel card, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        var holder = SceneUtil.Descendants<NCardHolder>(screen, intoMatches: false).FirstOrDefault(h => ReferenceEquals(h.CardModel, card));
        if (!SceneUtil.Alive(holder)) return ActionResult.Fail("card not found on screen");
        // Like the card reward screen this one ignores clicks for a moment after opening (_noSelectionTimeMsec).
        await RoomUtil.WaitClickable(screen, holder!, ct);
        var r = UiActions.ClickHolder(holder, "choose card");
        if (!r.Success) return r;
        if (!await MainThread.WaitUntil(() => ChooseDone(screen), TimeSpan.FromSeconds(1.5), ct) && SceneUtil.Alive(holder) && !ChooseDone(screen))
            UiActions.ClickHolder(holder, "choose card"); // first click fell into the opening guard
        r = await UiActions.Expect(() => ChooseDone(screen), "choose card", ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    private static NClickableControl? SkipButton(Node screen)
    {
        var b = RoomUtil.NodeOf<NClickableControl>(screen, "_skipButton", "SkipButton", "%SkipButton");
        return SceneUtil.Usable(b) ? b : null;
    }

    // ================================================================== choose a card pack (bundle)

    public static void ReadBundles(GameContext ctx, NChooseABundleSelectionScreen screen, GameSnapshot snap, ActionSink sink)
    {
        bool inCombat = ctx.Combat != null;
        var sel = new SelectionInfo { Purpose = "ChooseBundle", Prompt = RoomUtil.BannerText(screen), MinSelect = 1, MaxSelect = 1 };
        snap.Selection = sel;

        var bundles = Bundles(screen);
        int cardIndex = 0;
        var bundleInfos = new List<(NCardBundle node, List<CardModel> cards, List<CardInfo> infos)>();
        foreach (var b in bundles)
        {
            var models = SafeBundle(b);
            var infos = new List<CardInfo>();
            foreach (var card in models)
            {
                var ci = RoomUtil.CardInfo(card, "c" + cardIndex++, inCombat);
                infos.Add(ci);
                sel.Candidates.Add(ci);
            }
            bundleInfos.Add((b, models, infos));
            sel.OtherOptions.Add(RoomUtil.T("rooms.bundle", bundleInfos.Count, string.Join(", ", infos.Select(i => i.Key + " " + RoomUtil.CardName(i)))));
        }

        if (BundlePreviewOpen(screen))
        {
            // A pack is already shown in the preview: confirm it or back out (nothing while its confirm animates in,
            // so a lone "back" is never taken as a trivial action).
            if (BundleConfirm(screen) is not { } confirm)
            {
                RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
                return;
            }
            sel.CanConfirm = true;
            sink.Add(ActionKind.ConfirmSelection, RoomUtil.T("act.confirm"), "confirm", ct => PressAndWaitClosed(screen, confirm, "confirm pack", ct), unpredictable: true);
            if (RoomUtil.UsableButton<NBackButton>(screen, new[] { "_previewCancelButton" }, "%Cancel") is { } cancel)
            {
                sink.Add(ActionKind.CancelSelection, RoomUtil.T("rooms.cancel_preview"), "cancel:preview", async ct =>
                {
                    var pressed = UiActions.Press(cancel, "cancel pack preview");
                    if (!pressed.Success) return pressed;
                    var r = await UiActions.Expect(() => RoomUtil.Left(screen) || !BundlePreviewOpen(screen), "cancel pack preview", ct: ct);
                    await MainThread.Frames(3);
                    return r;
                });
            }
            return;
        }

        if (bundleInfos.Count == 0)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }
        for (int i = 0; i < bundleInfos.Count; i++)
        {
            var (node, models, infos) = bundleInfos[i];
            var text = string.Join(", ", infos.Select(c => RoomUtil.CardText(c, 90)));
            sink.Add(ActionKind.ChooseOption, RoomUtil.T("rooms.choose_bundle", text), "choose:bundle:" + string.Join(",", models.Select(RoomUtil.CardKey)),
                ct => PickBundle(screen, node, ct),
                new Dictionary<string, string> { [ActionArgs.Index] = i.ToString() },
                unpredictable: true);
        }
    }

    private static List<NCardBundle> Bundles(NChooseABundleSelectionScreen screen)
    {
        // Bundles moved into the preview are not "visible" in the row; read the row's bundles only.
        var row = RoomUtil.NodeOf<Control>(screen, "_bundleRow", "%BundleRow") ?? (Node)screen;
        return SceneUtil.ByScreenPosition(SceneUtil.Descendants<NCardBundle>(row, intoMatches: false).Where(b => SceneUtil.VisibleInTree(b)).ToList());
    }

    private static List<CardModel> SafeBundle(NCardBundle bundle)
    {
        try { return bundle.Bundle?.Where(c => c != null).ToList() ?? new List<CardModel>(); }
        catch (Exception) { return new List<CardModel>(); }
    }

    private static bool BundlePreviewOpen(NChooseABundleSelectionScreen screen) =>
        SceneUtil.VisibleInTree(RoomUtil.NodeOf<Control>(screen, "_bundlePreviewContainer", "%BundlePreviewContainer"));

    private static NConfirmButton? BundleConfirm(NChooseABundleSelectionScreen screen) =>
        RoomUtil.UsableButton<NConfirmButton>(screen, new[] { "_previewConfirmButton" }, "%Confirm");

    private static bool BundleDone(NChooseABundleSelectionScreen screen) => RoomUtil.Left(screen) || RoomUtil.CompletionDone(screen);

    /// <summary>One decision = click the pack (opens its preview), then the preview's confirm.</summary>
    private static async Task<ActionResult> PickBundle(NChooseABundleSelectionScreen screen, NCardBundle bundle, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen) || !SceneUtil.Alive(bundle)) return ActionResult.Fail("pack is gone");
        if (BundlePreviewOpen(screen)) return ActionResult.Fail("a pack preview is already open");
        NClickableControl? hitbox = null;
        try { hitbox = bundle.Hitbox; } catch (Exception) { }
        var pressed = UiActions.Press(hitbox, "pack");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => BundleDone(screen) || BundlePreviewOpen(screen), "open pack preview", TimeSpan.FromSeconds(5), ct);
        if (!r.Success) return r;
        if (!BundleDone(screen))
        {
            await MainThread.WaitUntil(() => BundleDone(screen) || BundleConfirm(screen) != null, TimeSpan.FromSeconds(3), ct);
            if (!BundleDone(screen))
            {
                var confirm = BundleConfirm(screen);
                if (confirm == null) return ActionResult.Fail("pack preview has no enabled confirm button");
                pressed = UiActions.Press(confirm, "confirm pack");
                if (!pressed.Success) return pressed;
                r = await UiActions.Expect(() => BundleDone(screen), "confirm pack", ct: ct);
            }
        }
        await RoomUtil.SettleAsync(ct);
        return r;
    }

    // ================================================================== choose a relic

    public static void ReadRelics(GameContext ctx, NChooseARelicSelection screen, GameSnapshot snap, ActionSink sink)
    {
        var skip = SkipButton(screen);
        var sel = new SelectionInfo
        {
            Purpose = "ChooseRelic",
            Prompt = RoomUtil.BannerText(screen),
            MinSelect = skip != null ? 0 : 1,
            MaxSelect = 1,
            CanCancel = skip != null,
        };
        snap.Selection = sel;

        var row = RoomUtil.NodeOf<Control>(screen, "_relicRow", "%RelicRow") ?? (Node)screen;
        var holders = SceneUtil.ByScreenPosition(SceneUtil.Descendants<NRelicBasicHolder>(row, intoMatches: false).Where(h => SceneUtil.VisibleInTree(h)).ToList());
        var used = new HashSet<string>();
        for (int i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            RelicModel? relic = null;
            try { relic = holder.Relic?.Model; } catch (Exception) { }
            if (relic == null) continue;
            var ri = RoomUtil.RelicInfo(relic);
            var text = RoomUtil.RelicText(ri);
            sel.OtherOptions.Add(text);
            if (!holder.IsEnabled) continue;
            var key = "choose:" + ModelText.SafeId(relic);
            for (int n = 2; !used.Add(key); n++) key = "choose:" + ModelText.SafeId(relic) + "#" + n;
            sink.Add(ActionKind.ChooseOption, RoomUtil.T("rooms.choose_relic", text), key,
                ct => PressAndWaitClosed(screen, holder, "choose relic", ct),
                new Dictionary<string, string> { [ActionArgs.Index] = i.ToString(), ["relic"] = ri.Id },
                unpredictable: true);
        }
        if (sink.Count == 0)
        {
            // Never offer a lone "skip" while the relics are not clickable yet (it would be taken as trivial).
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }
        if (skip != null)
            sink.Add(ActionKind.CancelSelection, RoomUtil.T("rooms.skip"), "cancel", ct => PressAndWaitClosed(screen, skip, "skip", ct), unpredictable: true);
    }

    // ================================================================== shared

    /// <summary>Presses a button of a choose-one overlay and waits until the overlay is done (closed / choice accepted).</summary>
    private static async Task<ActionResult> PressAndWaitClosed(Node screen, NClickableControl button, string what, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        var pressed = UiActions.Press(button, what);
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => RoomUtil.Left(screen) || RoomUtil.CompletionDone(screen), what, ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }
}
