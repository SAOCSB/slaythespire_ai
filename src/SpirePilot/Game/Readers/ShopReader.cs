using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms2;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Merchant: open inventory, buy cards/relics/potions, card removal service, leave; fake merchant event. Called for
/// ScreenKind.Shop (<see cref="NMerchantRoom"/>, <see cref="NMerchantInventory"/>, <see cref="NFakeMerchant"/>).
/// The inventory model is read without opening anything; buying is offered only while the inventory is open (the
/// way a player shops). Purchases go through <see cref="MerchantEntry.OnTryPurchaseWrapper"/>, the same per-player
/// entry point the shop UI uses, so co-op stays in sync.
/// </summary>
internal static class ShopReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var view = ShopView.Find(probe, ctx);
        if (view == null)
        {
            snap.Notes.Add(R2Text.T("note.shop_unreadable"));
            return;
        }

        var inv = view.Inventory;
        var shop = new ShopInfo();
        var entries = new List<(MerchantEntry entry, ShopItemInfo item, string key)>();
        if (inv != null) FillItems(ctx, inv, shop, entries);
        else snap.Notes.Add(R2Text.T("note.shop_unreadable"));
        snap.Shop = shop;

        if (view.State == ShopState.Transition)
        {
            snap.Notes.Add(R2Text.T("note.shop_transition"));
            return;
        }

        if (view.State == ShopState.Open && inv != null)
        {
            foreach (var (entry, item, key) in entries)
            {
                if (!item.Affordable) continue;
                var label = item.OnSale ? R2Text.T("act.buy_sale", item.Name, item.Price) : R2Text.T("act.buy", item.Name, item.Price);
                var captured = entry;
                sink.Add(ActionKind.BuyItem, label, key, ct => BuyAsync(view, inv, captured, item.Name, ct),
                    new Dictionary<string, string> { [ActionArgs.Index] = item.Index.ToString(), ["kind"] = item.Kind, ["price"] = item.Price.ToString() });
            }
            var removal = SafeRemoval(inv);
            if (removal != null && Stocked(removal) && Affordable(removal))
                sink.Add(ActionKind.BuyCardRemoval, R2Text.T("act.buy_removal", removal.Cost), "shop:remove",
                    ct => BuyRemovalAsync(view, inv, ct), new Dictionary<string, string> { ["price"] = removal.Cost.ToString() },
                    unpredictable: true);
        }
        else if (view.State == ShopState.Closed)
        {
            if (view.LocalPlayerDead) snap.Notes.Add(R2Text.T("note.shop_dead"));
            else if (view.CanOpen)
            {
                snap.Notes.Add(R2Text.T("note.shop_closed"));
                sink.Add(ActionKind.ChooseOption, R2Text.T("act.shop_open"), "shop:open", ct => OpenAsync(view, ct), unpredictable: true);
            }
        }

        if (view.CanLeave)
            sink.Add(ActionKind.Proceed, R2Text.T("act.shop_leave"), "shop:leave", ct => LeaveAsync(view, ct), unpredictable: true);
    }

    // ---------------------------------------------------------------- reading

    private static void FillItems(GameContext ctx, MerchantInventory inv, ShopInfo shop,
        List<(MerchantEntry, ShopItemInfo, string)> entries)
    {
        bool freeSlot = false;
        try { freeSlot = (inv.Player ?? ctx.Me)?.HasOpenPotionSlots ?? false; }
        catch (Exception) { /* keep false: never offer a potion we may not be able to hold */ }

        int index = 0;
        void Add(MerchantEntry entry, string kind, string id, string name, string description, CardInfo? card, bool onSale)
        {
            try
            {
                bool potionBlocked = kind == "Potion" && !freeSlot;
                var item = new ShopItemInfo
                {
                    Index = index,
                    Kind = kind,
                    Name = name,
                    Description = potionBlocked ? (description + " " + R2Text.T("word.no_slot")).Trim() : description,
                    Price = entry.Cost,
                    OnSale = onSale,
                    Affordable = Affordable(entry) && !potionBlocked,
                    Card = card,
                };
                shop.Items.Add(item);
                entries.Add((entry, item, $"shop:{kind.ToLowerInvariant()}:{id}"));
                index++;
            }
            catch (Exception ex) { ThrottledLog.Warn("shop:item", "shop item unreadable: " + ex.Message); }
        }

        foreach (var e in Safe(() => inv.CharacterCardEntries).Concat(Safe(() => inv.ColorlessCardEntries)))
        {
            try
            {
                if (e == null || !Stocked(e)) continue;
                var card = e.CreationResult?.Card;
                if (card == null) continue;
                var info = ModelText.Card(card, "s" + index, false);
                int upgrades = SafeUpgrades(card);
                var id = ModelText.SafeId(card) + (upgrades > 0 ? "+" + upgrades : "");
                Add(e, "Card", id, info.Name, info.Description, info, SafeBool(() => e.IsOnSale));
            }
            catch (Exception ex) { ThrottledLog.Warn("shop:card", "shop card unreadable: " + ex.Message); }
        }
        foreach (var e in Safe(() => inv.RelicEntries))
        {
            try
            {
                if (e == null || !Stocked(e) || e.Model == null) continue;
                var relic = ModelText.Relic(e.Model);
                Add(e, "Relic", ModelText.SafeId(e.Model), relic.Name, relic.Description, null, false);
            }
            catch (Exception ex) { ThrottledLog.Warn("shop:relic", "shop relic unreadable: " + ex.Message); }
        }
        foreach (var e in Safe(() => inv.PotionEntries))
        {
            try
            {
                if (e == null || !Stocked(e) || e.Model == null) continue;
                var potion = ModelText.Potion(e.Model, -1, false);
                Add(e, "Potion", ModelText.SafeId(e.Model), potion?.Name ?? ModelText.SafeId(e.Model), potion?.Description ?? "", null, false);
            }
            catch (Exception ex) { ThrottledLog.Warn("shop:potion", "shop potion unreadable: " + ex.Message); }
        }

        var removal = SafeRemoval(inv);
        if (removal != null)
        {
            try
            {
                shop.RemovalPrice = removal.Cost;
                shop.RemovalAvailable = Stocked(removal);
            }
            catch (Exception) { /* leave unknown */ }
        }
    }

    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>?> get)
    {
        try { return get()?.ToList() ?? new List<T>(); }
        catch (Exception) { return new List<T>(); }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch (Exception) { return false; }
    }

    private static int SafeUpgrades(CardModel card)
    {
        try { return card.CurrentUpgradeLevel; }
        catch (Exception) { return 0; }
    }

    private static MerchantCardRemovalEntry? SafeRemoval(MerchantInventory inv)
    {
        try { return inv.CardRemovalEntry; }
        catch (Exception) { return null; }
    }

    private static bool Stocked(MerchantEntry e) => SafeBool(() => e.IsStocked);

    private static bool Affordable(MerchantEntry e) => SafeBool(() => e.EnoughGold);

    private static AbstractModel? ModelOf(MerchantEntry e) => e switch
    {
        MerchantCardEntry c => c.CreationResult?.Card,
        MerchantRelicEntry r => r.Model,
        MerchantPotionEntry p => p.Model,
        _ => null,
    };

    // ---------------------------------------------------------------- executors

    private static async Task<ActionResult> OpenAsync(ShopView view, CancellationToken ct)
    {
        if (!view.HostAlive) return ActionResult.Fail("shop is gone");
        if (view.LocalPlayerDead) return ActionResult.Fail("the local player is dead");
        var button = view.MerchantButton;
        if (button != null && SceneUtil.Usable(button))
        {
            var pressed = UiActions.Press(button, "open shop");
            if (!pressed.Success) return pressed;
        }
        else if (!view.IsFake && view.Room != null)
        {
            // The merchant room exposes the same entry point the merchant button ends up calling.
            try { view.Room.OpenInventory(); }
            catch (Exception ex) { return ActionResult.Fail("open shop: " + ex.Message); }
        }
        else return ActionResult.Fail("open shop: merchant button not usable");

        // The first visit may show the merchant tutorial instead; that is handled as a popup.
        var r = await UiActions.Expect(() => view.IsOpenNow() || RoomUtil.ModalOpen(), "open shop", UiActions.DefaultTimeout, ct);
        await RoomUtil.Settle(ct);
        return r;
    }

    private static async Task<ActionResult> BuyAsync(ShopView view, MerchantInventory inv, MerchantEntry entry, string name, CancellationToken ct)
    {
        if (!view.IsOpenNow()) return ActionResult.Fail("shop inventory is not open");
        var player = inv.Player;
        if (player == null) return ActionResult.Fail("shop has no owner");
        if (!inv.AllEntries.Contains(entry)) return ActionResult.Fail(name + " is no longer in the shop");
        if (!Stocked(entry)) return ActionResult.Fail(name + " is sold out");
        if (!Affordable(entry)) return ActionResult.Fail("not enough gold for " + name);
        if (entry is MerchantPotionEntry && !player.HasOpenPotionSlots) return ActionResult.Fail("no free potion slot");

        int gold = player.Gold;
        var model = ModelOf(entry);
        Task<bool> task;
        try { task = entry.OnTryPurchaseWrapper(inv); } // never ignoreCost
        catch (Exception ex) { return ActionResult.Fail("buy " + name + ": " + ex.Message); }

        if (!await RoomUtil.WaitTask(task, UiActions.DefaultTimeout, ct))
        {
            RoomUtil.Observe(task, "purchase of " + name);
            return ActionResult.Fail("buy " + name + ": no answer from the shop (timed out)");
        }
        if (task.IsFaulted) return ActionResult.Fail("buy " + name + ": " + task.Exception?.GetBaseException().Message);
        if (RoomUtil.Outcome(task) != true) return ActionResult.Fail("the shop refused to sell " + name);

        // Gold drops (or the slot empties / restocks for free purchases).
        var r = await UiActions.Expect(() => player.Gold != gold || !Stocked(entry) || !ReferenceEquals(ModelOf(entry), model),
            "buy " + name, RoomUtil.ShortWait, ct);
        await RoomUtil.Settle(ct);
        return r.Success ? ActionResult.Ok("bought " + name) : r;
    }

    private static async Task<ActionResult> BuyRemovalAsync(ShopView view, MerchantInventory inv, CancellationToken ct)
    {
        if (!view.IsOpenNow()) return ActionResult.Fail("shop inventory is not open");
        var removal = SafeRemoval(inv);
        if (removal == null || !Stocked(removal)) return ActionResult.Fail("card removal is not available");
        if (!Affordable(removal)) return ActionResult.Fail("not enough gold for card removal");

        Task<bool> task;
        // Completes only after a card was picked in the grid that opens next, so it is not awaited here.
        try { task = removal.OnTryPurchaseWrapper(inv); }
        catch (Exception ex) { return ActionResult.Fail("card removal: " + ex.Message); }
        RoomUtil.Observe(task, "card removal");

        var r = await UiActions.Expect(() => RoomUtil.CurrentScreen() is NCardGridSelectionScreen || RoomUtil.ModalOpen() || task.IsCompleted,
            "card removal", UiActions.DefaultTimeout, ct);
        if (!r.Success) return r;
        if (RoomUtil.CurrentScreen() is not NCardGridSelectionScreen && task.IsCompleted && RoomUtil.Outcome(task) != true)
            return ActionResult.Fail("the shop refused the card removal");
        await RoomUtil.Settle(ct);
        return ActionResult.Ok();
    }

    private static async Task<ActionResult> LeaveAsync(ShopView view, CancellationToken ct)
    {
        if (!view.HostAlive) return ActionResult.Fail("shop is gone");
        var host = view.Host;

        // Close the inventory first (its back button), like a player does.
        if (view.LiveInventoryNode() is { } invNode && invNode.IsOpen)
        {
            var back = RoomUtil.Node<NBackButton>(invNode, "_backButton")
                       ?? RoomUtil.FirstUsable(SceneUtil.Descendants<NBackButton>(invNode));
            var closed = await RoomUtil.PressAndWait(back, "close shop", () => !SceneUtil.Alive(invNode) || !invNode.IsOpen, RoomUtil.ShortWait, ct);
            if (!closed.Success) return closed;
        }

        // The proceed button animates back in after the inventory closes.
        await MainThread.WaitUntil(() => SceneUtil.Usable(view.ProceedButton()), RoomUtil.ShortWait, ct);
        var proceed = view.ProceedButton();
        return await RoomUtil.PressAndWait(proceed, "leave shop", () =>
        {
            var cur = RoomUtil.CurrentScreen();
            return RoomUtil.ModalOpen() || cur != null && !ReferenceEquals(cur, host) && cur is not NMerchantInventory;
        }, UiActions.DefaultTimeout, ct);
    }

    // ---------------------------------------------------------------- view

    private enum ShopState { Closed, Open, Transition }

    /// <summary>The shop nodes of the current screen (merchant room or fake merchant event).</summary>
    private sealed class ShopView
    {
        public Control Host = null!;
        public NMerchantRoom? Room;
        public NFakeMerchant? Fake;
        public NMerchantInventory? InventoryNode;
        public MerchantInventory? Inventory;
        public ShopState State;

        public bool IsFake => Fake != null;
        public bool HostAlive => SceneUtil.Alive(Host);

        public NMerchantButton? MerchantButton
        {
            get
            {
                try { return IsFake ? Fake!.MerchantButton : Room?.MerchantButton; }
                catch (Exception) { return null; }
            }
        }

        public bool LocalPlayerDead
        {
            get
            {
                var b = MerchantButton;
                try { return SceneUtil.Valid(b) && b!.IsLocalPlayerDead; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The merchant room can always be opened through its own method; the fake merchant only by its button.</summary>
        public bool CanOpen => !LocalPlayerDead && (SceneUtil.Usable(MerchantButton) || (!IsFake && SceneUtil.Alive(Room)));

        public bool CanLeave => SceneUtil.Usable(ProceedButton()) || State == ShopState.Open;

        public NProceedButton? ProceedButton()
        {
            try
            {
                if (!IsFake) return SceneUtil.Alive(Room) ? Room!.ProceedButton : null;
                return RoomUtil.Node<NProceedButton>(Fake, "_proceedButton")
                       ?? SceneUtil.Descendants<NProceedButton>(Fake).FirstOrDefault(b => SceneUtil.VisibleInTree(b));
            }
            catch (Exception) { return null; }
        }

        /// <summary>The inventory node now (it may not have existed yet when the shop was read).</summary>
        public NMerchantInventory? LiveInventoryNode()
        {
            if (SceneUtil.Alive(InventoryNode)) return InventoryNode;
            try
            {
                var n = IsFake ? Fake!.Inventory : Room?.Inventory;
                return SceneUtil.Alive(n) ? n : null;
            }
            catch (Exception) { return null; }
        }

        public bool IsOpenNow() =>
            LiveInventoryNode() is { } inv && inv.IsOpen && RoomUtil.CurrentScreen() is NMerchantInventory;

        public static ShopView? Find(ScreenProbe probe, GameContext ctx)
        {
            var cur = probe.Screen;
            var view = new ShopView();
            switch (cur)
            {
                case NMerchantRoom room:
                    view.Room = room;
                    break;
                case NFakeMerchant fake:
                    view.Fake = fake;
                    break;
                case NMerchantInventory inv:
                    view.InventoryNode = inv;
                    view.Fake = Ancestor<NFakeMerchant>(inv);
                    if (view.Fake == null)
                    {
                        var room = Ancestor<NMerchantRoom>(inv);
                        if (room == null)
                        {
                            try { room = NMerchantRoom.Instance; }
                            catch (Exception) { room = null; }
                        }
                        view.Room = SceneUtil.Alive(room) ? room : null;
                    }
                    break;
                default:
                    return null;
            }
            Control? host = view.IsFake ? view.Fake : view.Room;
            if (!SceneUtil.Alive(host)) return null;
            view.Host = host!;

            try { view.InventoryNode ??= view.IsFake ? view.Fake!.Inventory : view.Room!.Inventory; }
            catch (Exception) { /* not ready yet */ }
            if (!SceneUtil.Alive(view.InventoryNode)) view.InventoryNode = null;

            view.Inventory = ReadInventory(view, ctx);

            bool isOpen = view.InventoryNode != null && SafeBool(() => view.InventoryNode.IsOpen);
            view.State = cur is NMerchantInventory
                ? (isOpen ? ShopState.Open : ShopState.Transition)
                : (isOpen ? ShopState.Transition : ShopState.Closed);
            return view;
        }

        private static MerchantInventory? ReadInventory(ShopView view, GameContext ctx)
        {
            try
            {
                var fromNode = view.InventoryNode?.Inventory;
                if (fromNode != null) return fromNode;
            }
            catch (Exception) { /* fall through */ }
            try
            {
                if (!view.IsFake) return view.Room?.Room?.GetLocalInventory();
                // The fake merchant keeps its (relic-only) stock on its event model.
                var ev = RunManager.Instance?.EventSynchronizer?.GetLocalEvent();
                return (ev as FakeMerchant)?.Inventory;
            }
            catch (Exception) { return null; }
        }

        private static T? Ancestor<T>(Node? node) where T : Node
        {
            for (var p = node?.GetParent(); p != null; p = p.GetParent())
                if (p is T t) return t;
            return null;
        }
    }
}
