using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms;

/// <summary>
/// Full-screen card grids (<see cref="NCardGridSelectionScreen"/> and subclasses: deck select / remove, upgrade,
/// transform, enchant, simple and combat-pile selections), in and out of combat. Reads the full candidate list from the
/// screen's model list (the grid only instantiates holders near its scroll position), min/max and the current
/// selection from the screen's private fields, and drives the two-step confirm (confirm → preview → preview confirm).
/// </summary>
internal static class CardGridSelection
{
    // Private button / container fields per subclass (names from the 0.111 dump), with the reference mods' node paths.
    private static readonly string[] PreviewConfirmFields = { "_previewConfirmButton", "_singlePreviewConfirmButton", "_multiPreviewConfirmButton" };
    private static readonly string[] PreviewConfirmPaths =
    {
        "%PreviewConfirm", "%PreviewContainer/Confirm", "%UpgradeSinglePreviewContainer/Confirm", "%UpgradeMultiPreviewContainer/Confirm",
        "%EnchantSinglePreviewContainer/Confirm", "%EnchantMultiPreviewContainer/Confirm",
    };
    private static readonly string[] PreviewCancelFields = { "_previewCancelButton", "_singlePreviewCancelButton", "_multiPreviewCancelButton" };
    private static readonly string[] PreviewCancelPaths =
    {
        "%PreviewCancel", "%PreviewContainer/Cancel", "%UpgradeSinglePreviewContainer/Cancel", "%UpgradeMultiPreviewContainer/Cancel",
        "%EnchantSinglePreviewContainer/Cancel", "%EnchantMultiPreviewContainer/Cancel",
    };
    private static readonly string[] PreviewContainerFields =
    {
        "_previewContainer", "_upgradeSinglePreviewContainer", "_upgradeMultiPreviewContainer", "_enchantSinglePreviewContainer", "_enchantMultiPreviewContainer",
    };
    private static readonly string[] PreviewContainerPaths =
    {
        "%PreviewContainer", "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer", "%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer",
    };

    private sealed record Prefs(int Min, int Max, bool Cancelable, bool Known, LocString? Prompt);

    public static void Read(GameContext ctx, NCardGridSelectionScreen screen, GameSnapshot snap, ActionSink sink)
    {
        bool inCombat = ctx.Combat != null;
        var prefs = ReadPrefs(screen);
        var cards = Candidates(screen);
        var selected = SelectedSet(screen);
        int selectedCount = selected?.Count ?? 0;
        int effMin = screen is NDeckEnchantSelectScreen ? Math.Max(1, prefs.Min) : prefs.Min; // enchant ignores an empty confirm

        var sel = new SelectionInfo
        {
            Purpose = Purpose(screen, prefs),
            Prompt = Prompt(screen, prefs),
            MinSelect = effMin,
            MaxSelect = prefs.Max,
        };
        snap.Selection = sel;

        var infos = new List<CardInfo>();
        for (int i = 0; i < cards.Count; i++)
        {
            var ci = RoomUtil.CardInfo(cards[i], "c" + i, inCombat);
            infos.Add(ci);
            sel.Candidates.Add(ci);
            if (selected != null && selected.Contains(cards[i])) sel.SelectedKeys.Add(ci.Key);
        }
        if (cards.Count == 0)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
            return;
        }

        bool previewOpen = PreviewOpen(screen);
        if (previewOpen)
        {
            // The preview covers the grid: only confirm or back out. While its confirm is not up yet (animating in),
            // offer nothing: a lone "back" would look trivial and be taken automatically.
            if (PreviewConfirm(screen) == null)
            {
                RoomUtil.Note(snap, RoomUtil.T("rooms.selection_wait"));
                return;
            }
            sel.CanConfirm = true;
            sink.Add(ActionKind.ConfirmSelection, RoomUtil.T("act.confirm"), "confirm", ct => Confirm(screen, ct), unpredictable: true);
            if (RoomUtil.UsableButton<NBackButton>(screen, PreviewCancelFields, PreviewCancelPaths) is { } back)
                sink.Add(ActionKind.CancelSelection, RoomUtil.T("rooms.cancel_preview"), "cancel:preview", ct => Cancel(screen, back, ct));
            return;
        }
        int actionsBefore = sink.Count;

        // Select: identical unselected cards collapse into one action (the executor takes the first unselected copy).
        bool canSelectMore = selected == null || selectedCount < prefs.Max;
        bool autoConfirm = prefs.Known && prefs.Min == prefs.Max && selected != null && selectedCount + 1 == prefs.Max;
        var usedKeys = new HashSet<string>();
        if (canSelectMore)
        {
            foreach (var group in Groups(cards, infos, c => selected == null || !selected.Contains(c)))
            {
                var first = group[0];
                var name = RoomUtil.CardName(first.info) + " (" + string.Join(",", group.Select(g => g.info.Key).Take(4)) + (group.Count > 4 ? ",…" : "") + ")";
                if (group.Count > 1) name += " ×" + group.Count;
                var label = autoConfirm ? RoomUtil.T("rooms.select_confirm", name) : RoomUtil.T("act.select_card", name);
                var key = UniqueKey(usedKeys, "select:" + RoomUtil.CardKey(first.card));
                var models = group.Select(g => g.card).ToList();
                sink.Add(ActionKind.SelectCard, label, key, ct => Select(screen, models, autoConfirm, ct),
                    new Dictionary<string, string> { [ActionArgs.Card] = first.info.Key, [ActionArgs.Index] = cards.IndexOf(first.card).ToString() },
                    unpredictable: autoConfirm);
            }
        }

        // Deselect (multi-select grids toggle on a second click).
        if (selected != null && selectedCount > 0 && prefs.Max > 1)
        {
            foreach (var group in Groups(cards, infos, c => selected.Contains(c)))
            {
                var first = group[0];
                var key = UniqueKey(usedKeys, "deselect:" + RoomUtil.CardKey(first.card));
                var models = group.Select(g => g.card).ToList();
                sink.Add(ActionKind.DeselectCard, RoomUtil.T("act.deselect_card", RoomUtil.CardName(first.info) + " (" + first.info.Key + ")"), key,
                    ct => Deselect(screen, models, ct),
                    new Dictionary<string, string> { [ActionArgs.Card] = first.info.Key, [ActionArgs.Index] = cards.IndexOf(first.card).ToString() });
            }
        }

        // Confirm only when the selection satisfies the minimum (and the screen's own confirm button is up).
        var mainConfirm = MainConfirm(screen);
        bool countOk = selected == null || (selectedCount >= effMin && selectedCount <= prefs.Max);
        if (mainConfirm != null && countOk)
        {
            sel.CanConfirm = true;
            var label = selected != null ? RoomUtil.T("rooms.confirm_count", selectedCount, effMin == prefs.Max ? effMin.ToString() : effMin + "-" + prefs.Max)
                                         : RoomUtil.T("act.confirm");
            sink.Add(ActionKind.ConfirmSelection, label, "confirm", ct => Confirm(screen, ct), unpredictable: true);
        }

        // Never leave "cancel" as the only option while cards are picked (it would be taken as trivial and drop the picks).
        bool onlyCancel = sink.Count == actionsBefore && selectedCount > 0;
        if (!onlyCancel && (!prefs.Known || prefs.Cancelable) &&
            RoomUtil.UsableButton<NBackButton>(screen, new[] { "_closeButton" }, "%Close") is { } close)
        {
            sel.CanCancel = true;
            sink.Add(ActionKind.CancelSelection, RoomUtil.T("act.cancel"), "cancel", ct => Cancel(screen, close, ct), unpredictable: true);
        }
    }

    // ---------------------------------------------------------------- reading

    private static Prefs ReadPrefs(NCardGridSelectionScreen screen)
    {
        try
        {
            if (Reflect.Get<object>(screen, "_prefs") is CardSelectorPrefs p)
            {
                int max = Math.Max(p.MaxSelect, 0);
                return new Prefs(Math.Max(0, p.MinSelect), max == 0 ? Math.Max(1, p.MinSelect) : max, p.Cancelable, true, p.Prompt);
            }
        }
        catch (Exception) { }
        return new Prefs(1, 1, false, false, null);
    }

    /// <summary>
    /// Every card the grid offers, including rows scrolled off screen: the grid's model list (<c>NCardGrid._cards</c>,
    /// display order), else the screen's (<c>_cards</c>), each only when it contains every visible holder's card;
    /// else just the visible holders.
    /// </summary>
    public static List<CardModel> Candidates(NCardGridSelectionScreen screen)
    {
        var visible = VisibleHolders(screen).Select(h => h.CardModel).Where(c => c != null).Distinct().ToList();
        var grid = Grid(screen);
        foreach (var list in new[]
                 {
                     grid != null ? Reflect.Get<IEnumerable<CardModel>>(grid, "_cards") : null,
                     Reflect.Get<IEnumerable<CardModel>>(screen, "_cards"),
                 })
        {
            if (list == null) continue;
            var cards = list.Where(c => c != null).ToList();
            if (cards.Count > 0 && visible.All(v => cards.Contains(v))) return cards;
        }
        return visible;
    }

    private static List<NGridCardHolder> VisibleHolders(Node screen) =>
        SceneUtil.ByScreenPosition(SceneUtil.Descendants<NGridCardHolder>(screen).Where(h => SceneUtil.VisibleInTree(h) && h.CardModel != null).ToList());

    private static NCardGrid? Grid(NCardGridSelectionScreen screen) =>
        RoomUtil.NodeOf<NCardGrid>(screen, "_grid", "%CardGrid") ?? SceneUtil.First<NCardGrid>(screen);

    /// <summary>The current selection (private <c>_selectedCards</c> of the concrete screen); null when unreadable.</summary>
    private static HashSet<CardModel>? SelectedSet(NCardGridSelectionScreen screen)
    {
        try
        {
            var set = Reflect.Get<IEnumerable<CardModel>>(screen, "_selectedCards");
            return set == null ? null : new HashSet<CardModel>(set.Where(c => c != null), ReferenceEqualityComparer.Instance as IEqualityComparer<CardModel>);
        }
        catch (Exception) { return null; }
    }

    private static int SelectedCount(NCardGridSelectionScreen screen) => SelectedSet(screen)?.Count ?? -1;

    private static bool PreviewOpen(NCardGridSelectionScreen screen)
    {
        foreach (var f in PreviewContainerFields)
            if (SceneUtil.VisibleInTree(RoomUtil.NodeOf<Control>(screen, f))) return true;
        foreach (var p in PreviewContainerPaths)
            if (SceneUtil.VisibleInTree(RoomUtil.NodeOf<Control>(screen, null, p))) return true;
        return false;
    }

    private static NConfirmButton? PreviewConfirm(NCardGridSelectionScreen screen) =>
        RoomUtil.UsableButton<NConfirmButton>(screen, PreviewConfirmFields, PreviewConfirmPaths);

    private static NConfirmButton? MainConfirm(NCardGridSelectionScreen screen) =>
        RoomUtil.UsableButton<NConfirmButton>(screen, new[] { "_confirmButton" }, "%Confirm", "Confirm");

    private static string Purpose(NCardGridSelectionScreen screen, Prefs prefs)
    {
        switch (screen)
        {
            case NDeckUpgradeSelectScreen: return "Upgrade";
            case NDeckTransformSelectScreen: return "Transform";
            case NDeckEnchantSelectScreen: return "Enchant";
        }
        var key = SafeKey(prefs.Prompt);
        if (key.Length > 0)
        {
            if (key == SafeKey(Static(() => CardSelectorPrefs.RemoveSelectionPrompt))) return "Remove";
            if (key == SafeKey(Static(() => CardSelectorPrefs.TransformSelectionPrompt))) return "Transform";
            if (key == SafeKey(Static(() => CardSelectorPrefs.UpgradeSelectionPrompt))) return "Upgrade";
            if (key == SafeKey(Static(() => CardSelectorPrefs.EnchantSelectionPrompt))) return "Enchant";
            if (key == SafeKey(Static(() => CardSelectorPrefs.DiscardSelectionPrompt))) return "Discard";
            if (key == SafeKey(Static(() => CardSelectorPrefs.ExhaustSelectionPrompt))) return "Exhaust";
        }
        return screen is NDeckCardSelectScreen ? "DeckSelect" : "Select";
    }

    private static LocString? Static(Func<LocString> get)
    {
        try { return get(); }
        catch (Exception) { return null; }
    }

    private static string SafeKey(LocString? s)
    {
        try { return s == null ? "" : (s.LocTable ?? "") + "/" + (s.LocEntryKey ?? ""); }
        catch (Exception) { return ""; }
    }

    private static string Prompt(NCardGridSelectionScreen screen, Prefs prefs)
    {
        var text = ModelText.Loc(prefs.Prompt);
        if (text.Length > 0) return text;
        return RoomUtil.LabelText(RoomUtil.NodeOf<Control>(screen, "_infoLabel", "%BottomLabel"));
    }

    private static List<List<(CardModel card, CardInfo info)>> Groups(List<CardModel> cards, List<CardInfo> infos, Func<CardModel, bool> include)
    {
        var groups = new List<List<(CardModel, CardInfo)>>();
        var byKey = new Dictionary<string, List<(CardModel, CardInfo)>>();
        for (int i = 0; i < cards.Count; i++)
        {
            if (!include(cards[i])) continue;
            var ci = infos[i];
            // Identical copies only: same id, upgrade level, cost and text (enchanted / modified copies stay apart).
            var key = RoomUtil.CardKey(cards[i]) + "|" + ci.Name + "|" + ci.Cost + "|" + ci.Description;
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new List<(CardModel, CardInfo)>();
                byKey[key] = g;
                groups.Add(g);
            }
            g.Add((cards[i], ci));
        }
        return groups;
    }

    private static string UniqueKey(HashSet<string> used, string key)
    {
        var k = key;
        for (int n = 2; !used.Add(k); n++) k = key + "#" + n;
        return k;
    }

    // ---------------------------------------------------------------- executors

    private static bool Closed(NCardGridSelectionScreen screen)
    {
        if (RoomUtil.Left(screen)) return true;
        try { return screen.CardsSelected().IsCompleted; } // readonly TaskCompletionSource: only reads its task
        catch (Exception) { return false; }
    }

    /// <summary>Clicks one card: its holder when the grid shows it, else the screen's own click handler.</summary>
    private static ActionResult ClickCard(NCardGridSelectionScreen screen, CardModel card)
    {
        NGridCardHolder? holder = null;
        try { holder = Grid(screen)?.GetCardHolder(card); }
        catch (Exception) { /* not displayed */ }
        if (!SceneUtil.Alive(holder))
            holder = SceneUtil.Descendants<NGridCardHolder>(screen).FirstOrDefault(h => ReferenceEquals(h.CardModel, card));
        if (SceneUtil.Alive(holder)) return UiActions.ClickHolder(holder, "card");
        // Off-screen card of a virtualized grid: OnCardClicked(CardModel) is what a holder press ends in (protected).
        return Reflect.TryCall(screen, "OnCardClicked", out _, card)
            ? ActionResult.Ok()
            : ActionResult.Fail("card is not on screen and the grid's click handler is unavailable");
    }

    private static async Task<ActionResult> Select(NCardGridSelectionScreen screen, List<CardModel> group, bool autoConfirm, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        if (PreviewOpen(screen)) return ActionResult.Fail("a preview is open; confirm or cancel it first");
        var selected = SelectedSet(screen);
        var card = group.FirstOrDefault(c => selected == null || !selected.Contains(c));
        if (card == null) return ActionResult.Fail("card already selected");
        if (!Candidates(screen).Contains(card)) return ActionResult.Fail("card is no longer offered");

        int before = selected?.Count ?? -1;
        var clicked = ClickCard(screen, card);
        if (!clicked.Success) return clicked;
        var r = await UiActions.Expect(() => Closed(screen) || PreviewOpen(screen) || (before >= 0 && SelectedCount(screen) != before),
            "select card", TimeSpan.FromSeconds(5), ct);
        if (!r.Success) return r;
        if (autoConfirm && !Closed(screen))
        {
            await MainThread.Frames(3);
            return await Confirm(screen, ct);
        }
        if (Closed(screen)) await RoomUtil.SettleAsync(ct);
        else await MainThread.Frames(3); // the choice may sit inside a running action: do not wait for it to finish
        return r;
    }

    private static async Task<ActionResult> Deselect(NCardGridSelectionScreen screen, List<CardModel> group, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        var selected = SelectedSet(screen);
        var card = group.FirstOrDefault(c => selected != null && selected.Contains(c));
        if (card == null) return ActionResult.Fail("card is not selected");
        int before = selected!.Count;
        var clicked = ClickCard(screen, card);
        if (!clicked.Success) return clicked;
        var r = await UiActions.Expect(() => Closed(screen) || SelectedCount(screen) != before, "deselect card", TimeSpan.FromSeconds(5), ct);
        await MainThread.Frames(3);
        return r;
    }

    /// <summary>
    /// Confirm, including the preview step: deck / transform / enchant / multi-upgrade screens first open a preview
    /// whose own confirm finishes the selection. Clicks the screen confirm at most twice, a few frames apart.
    /// </summary>
    public static async Task<ActionResult> Confirm(NCardGridSelectionScreen screen, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int mainClicks = 0, cooldown = 0;
        bool pressedAnything = false;
        while (sw.Elapsed < UiActions.DefaultTimeout && !ct.IsCancellationRequested)
        {
            if (Closed(screen)) break;
            if (PreviewConfirm(screen) is { } preview)
            {
                var p = UiActions.Press(preview, "confirm preview");
                if (!p.Success) return p;
                var r = await UiActions.Expect(() => Closed(screen), "confirm selection", ct: ct);
                await RoomUtil.SettleAsync(ct);
                return r;
            }
            if (cooldown > 0) cooldown--;
            else if (mainClicks < 2 && MainConfirm(screen) is { } main)
            {
                var p = UiActions.Press(main, "confirm");
                if (!p.Success) return p;
                pressedAnything = true;
                mainClicks++;
                cooldown = 5;
            }
            else if (!pressedAnything && sw.Elapsed > TimeSpan.FromSeconds(2))
            {
                // Nothing became pressable (a preview may need a moment to animate in, hence the grace period).
                return ActionResult.Fail("no confirm button is enabled (select more cards first?)");
            }
            await MainThread.NextFrame();
        }
        if (!Closed(screen)) return ActionResult.Fail("confirm selection: no effect (timed out)");
        await RoomUtil.SettleAsync(ct);
        return ActionResult.Ok();
    }

    private static async Task<ActionResult> Cancel(NCardGridSelectionScreen screen, NBackButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("selection screen is gone");
        bool wasPreview = PreviewOpen(screen);
        var pressed = UiActions.Press(button, "cancel");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => Closed(screen) || (wasPreview && !PreviewOpen(screen)), "cancel selection", ct: ct);
        if (Closed(screen)) await RoomUtil.SettleAsync(ct);
        else await MainThread.Frames(3);
        return r;
    }

    // ---------------------------------------------------------------- generic grid (unknown screen with grid holders)

    /// <summary>Best effort for a screen type we do not know that shows selectable grid card holders.</summary>
    public static void ReadGeneric(GameContext ctx, Node screen, GameSnapshot snap, ActionSink sink)
    {
        bool inCombat = ctx.Combat != null;
        var holders = VisibleHolders(screen);
        var sel = new SelectionInfo { Purpose = "Select", Prompt = RoomUtil.BannerText(screen), MinSelect = 0, MaxSelect = 1 };
        snap.Selection = sel;
        var usedKeys = new HashSet<string>();
        for (int i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            var card = holder.CardModel;
            if (card == null) continue;
            var ci = RoomUtil.CardInfo(card, "c" + i, inCombat);
            sel.Candidates.Add(ci);
            sink.Add(ActionKind.SelectCard, RoomUtil.T("act.select_card", RoomUtil.CardName(ci) + " (" + ci.Key + ")"),
                UniqueKey(usedKeys, "select:" + RoomUtil.CardKey(card)),
                async ct =>
                {
                    if (!SceneUtil.Alive(holder)) return ActionResult.Fail("card is gone");
                    var before = Signature(screen);
                    var r = UiActions.ClickHolder(holder, "card");
                    if (!r.Success) return r;
                    r = await UiActions.Expect(() => RoomUtil.Left(screen) || Signature(screen) != before, "select card", TimeSpan.FromSeconds(3), ct);
                    await RoomUtil.SettleAsync(ct);
                    return r;
                },
                new Dictionary<string, string> { [ActionArgs.Card] = ci.Key, [ActionArgs.Index] = i.ToString() }, unpredictable: true);
        }
        var confirm = SceneUtil.Descendants<NConfirmButton>(screen).FirstOrDefault(SceneUtil.Usable);
        if (confirm != null)
        {
            sel.CanConfirm = true;
            sink.Add(ActionKind.ConfirmSelection, RoomUtil.T("act.confirm"), "confirm", ct => PressUntilLeft(screen, confirm, "confirm", ct), unpredictable: true);
        }
        var back = SceneUtil.Descendants<NBackButton>(screen).FirstOrDefault(SceneUtil.Usable);
        if (back != null)
        {
            sel.CanCancel = true;
            sink.Add(ActionKind.CancelSelection, RoomUtil.T("act.cancel"), "cancel", ct => PressUntilLeft(screen, back, "cancel", ct), unpredictable: true);
        }
    }

    /// <summary>Cheap fingerprint of a generic selection screen's visible state.</summary>
    private static string Signature(Node screen)
    {
        try
        {
            var holders = SceneUtil.Descendants<NGridCardHolder>(screen).Count(h => SceneUtil.VisibleInTree(h));
            var buttons = SceneUtil.Descendants<NConfirmButton>(screen).Count(SceneUtil.Usable);
            return holders + "/" + buttons;
        }
        catch (Exception) { return ""; }
    }

    private static async Task<ActionResult> PressUntilLeft(Node screen, MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl button, string what, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("screen is gone");
        var before = Signature(screen);
        var pressed = UiActions.Press(button, what);
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => RoomUtil.Left(screen) || Signature(screen) != before, what, ct: ct);
        await RoomUtil.SettleAsync(ct);
        return r;
    }
}
