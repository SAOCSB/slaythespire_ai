using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>Treasure room: open chest, pick relic (co-op relic vote), proceed. Called for ScreenKind.Treasure.</summary>
internal static class TreasureReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var room = FindRoom(probe.Screen as Node);
        if (room == null) return;
        var me = ctx.Me;

        // A lone "leave / skip" would count as a trivial action and be taken automatically, so leaving is only ever
        // offered next to the real choice (open / pick), or once there is nothing left to take.

        // 1) Closed chest.
        var chest = ChestButton(room);
        bool opened = ChestOpened(room, chest);
        if (!opened)
        {
            if (!SceneUtil.Usable(chest))
            {
                RoomUtil.Note(snap, RoomUtil.T("rooms.chest_wait"));
                return;
            }
            sink.Add(ActionKind.OpenChest, RoomUtil.T("act.open_chest"), "chest:open", ct => OpenChest(room, chest!, ct), unpredictable: true);
            AddProceed(sink, room, RoomUtil.T("rooms.leave_chest"));
            return;
        }

        // 2) Relic picking (one relic per player in co-op; everyone votes, collisions are fought over).
        var collection = Collection(room);
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        IReadOnlyList<RelicModel>? relics = null;
        try { relics = sync?.CurrentRelics; } catch (Exception) { }
        bool collectionShown = SceneUtil.VisibleInTree(collection);
        if (collectionShown && relics is { Count: > 0 })
        {
            try { ReadPicking(ctx, snap, sink, room, collection!, sync!, relics, me); }
            catch (Exception ex) { ThrottledLog.Warn("treasure-pick", "chest relics unreadable: " + ex.Message); }
            return;
        }

        // 3) Relic taken (or an empty chest): leave. While the button still reads "skip" a relic may be on its way
        //    (chest animating open), so wait unless there is clearly nothing to pick.
        bool nothingToPick = !collectionShown && relics is not { Count: > 0 };
        var proceed = Proceed(room);
        bool isSkip = false;
        try { isSkip = SceneUtil.Valid(proceed) && proceed!.IsSkip; } catch (Exception) { }
        if (isSkip && !nothingToPick)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.chest_wait"));
            return;
        }
        AddProceed(sink, room, RoomUtil.T("act.proceed"));
    }

    private static void ReadPicking(GameContext ctx, GameSnapshot snap, ActionSink sink, NTreasureRoom room,
        NTreasureRoomRelicCollection collection, TreasureRoomRelicSynchronizer sync, IReadOnlyList<RelicModel> relics, Player? me)
    {
        var infos = relics.Select(r => r == null ? null : RoomUtil.RelicInfo(r)).ToList();
        string NameAt(int? i) => i is { } k && k >= 0 && k < infos.Count && infos[k] is { } ri ? (ri.Name.Length > 0 ? ri.Name : ri.Id) : RoomUtil.T("rooms.nothing");

        // Teammates' picks (readable locally: co-op simulates everyone).
        var pickedBy = new Dictionary<int, List<string>>();
        foreach (var mate in RoomUtil.Teammates(ctx.Run, me))
        {
            var vote = SafeVote(sync, mate);
            if (vote is not { voteReceived: true }) continue;
            var name = RoomUtil.PlayerName(mate);
            snap.Notes.Add(RoomUtil.T("rooms.teammate_pick", name, NameAt(vote.index)));
            if (vote.index is { } idx)
            {
                if (!pickedBy.TryGetValue(idx, out var list)) pickedBy[idx] = list = new List<string>();
                list.Add(name);
            }
        }

        var sel = new SelectionInfo { Purpose = "ChestRelic", MinSelect = 1, MaxSelect = 1 };
        for (int i = 0; i < infos.Count; i++)
            if (infos[i] is { } ri) sel.OtherOptions.Add(i + ": " + RoomUtil.RelicText(ri) + (pickedBy.TryGetValue(i, out var by) ? " " + RoomUtil.T("rooms.picked_by", string.Join(", ", by)) : ""));
        snap.Selection = sel;

        var myVote = me != null ? SafeVote(sync, me) : null;
        if (myVote is { voteReceived: true })
        {
            sel.SelectedKeys.Add(myVote.index?.ToString() ?? "-");
            RoomUtil.Note(snap, RoomUtil.T("rooms.relic_voted", NameAt(myVote.index)));
            return;
        }

        var holders = SceneUtil.Descendants<NTreasureRoomRelicHolder>(collection).Where(h => SceneUtil.VisibleInTree(h)).ToList();
        int picks = 0;
        for (int i = 0; i < infos.Count; i++)
        {
            if (infos[i] is not { } ri) continue;
            int index = i;
            var holder = holders.FirstOrDefault(h => SafeIndex(h) == index && SceneUtil.Usable(h))
                         ?? (relics.Count == 1 ? holders.FirstOrDefault(SceneUtil.Usable) : null);
            // Holders are disabled while the relics animate in; only fall back to the synchronizer when none exist at all.
            if (holder == null && holders.Count > 0) continue;
            var label = RoomUtil.T("rooms.choose_relic", RoomUtil.RelicText(ri));
            if (pickedBy.TryGetValue(i, out var by)) label += " " + RoomUtil.T("rooms.picked_by", string.Join(", ", by));
            sink.Add(ActionKind.ClaimReward, label, "chest:relic:" + ri.Id, ct => PickRelic(room, collection, holder, index, ct),
                new Dictionary<string, string> { [ActionArgs.Index] = i.ToString(), ["relic"] = ri.Id }, unpredictable: true);
            picks++;
        }
        if (picks == 0)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.chest_wait"));
            return;
        }

        // Before a pick the room's proceed button is a "skip" (a skip vote in co-op).
        AddProceed(sink, room, null, RoomUtil.T("rooms.skip_relic"));
    }

    // ---------------------------------------------------------------- nodes

    private static NTreasureRoom? FindRoom(Node? screen)
    {
        if (screen is NTreasureRoom r && SceneUtil.Alive(r)) return r;
        for (var n = screen; n != null && SceneUtil.Valid(n); n = n.GetParent())
            if (n is NTreasureRoom room && SceneUtil.Alive(room)) return room;
        return SceneUtil.Descendants<NTreasureRoom>(SceneUtil.SceneRoot, 12).FirstOrDefault(SceneUtil.VisibleInTree);
    }

    private static NButton? ChestButton(NTreasureRoom room) =>
        RoomUtil.NodeOf<NButton>(room, "_chestButton", "%Chest", "Chest") ?? SceneUtil.First<NTreasureButton>(room);

    private static bool ChestOpened(NTreasureRoom room, NButton? chest)
    {
        if (Reflect.Get<object>(room, "_hasChestBeenOpened") is bool b) return b;
        return !SceneUtil.Usable(chest); // fallback: the chest button disables once opened
    }

    private static NTreasureRoomRelicCollection? Collection(NTreasureRoom room) =>
        RoomUtil.NodeOf<NTreasureRoomRelicCollection>(room, "_relicCollection", "%RelicCollection")
        ?? SceneUtil.First<NTreasureRoomRelicCollection>(room);

    private static NProceedButton? Proceed(NTreasureRoom room)
    {
        try
        {
            var b = room.ProceedButton;
            if (SceneUtil.Valid(b)) return b;
        }
        catch (Exception) { }
        return RoomUtil.NodeOf<NProceedButton>(room, "_proceedButton", "%ProceedButton");
    }

    private static TreasureRoomRelicSynchronizer.PlayerVote? SafeVote(TreasureRoomRelicSynchronizer sync, Player p)
    {
        try { return sync.GetPlayerVote(p); }
        catch (Exception) { return null; }
    }

    private static int SafeIndex(NTreasureRoomRelicHolder h)
    {
        try { return h.Index; }
        catch (Exception) { return -1; }
    }

    // ---------------------------------------------------------------- actions

    /// <param name="fixedLabel">Label to use regardless of the button state.</param>
    /// <param name="skipLabel">Label while the button is the "skip" variant (before a relic pick).</param>
    private static void AddProceed(ActionSink sink, NTreasureRoom room, string? fixedLabel, string? skipLabel = null)
    {
        var button = Proceed(room);
        if (!SceneUtil.Usable(button)) return;
        bool isSkip = false;
        try { isSkip = button!.IsSkip; } catch (Exception) { }
        var text = fixedLabel ?? (isSkip && skipLabel != null ? skipLabel : RoomUtil.T("act.proceed"));
        sink.Add(ActionKind.Proceed, text, "proceed", ct => DoProceed(room, button!, ct), unpredictable: true);
    }

    private static async Task<ActionResult> OpenChest(NTreasureRoom room, NButton chest, CancellationToken ct)
    {
        if (!SceneUtil.Alive(room)) return ActionResult.Fail("treasure room is gone");
        var pressed = UiActions.Press(chest, "chest");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => !SceneUtil.Alive(room) || ChestOpened(room, chest) || SceneUtil.VisibleInTree(Collection(room)),
            "open chest", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }

    private static async Task<ActionResult> PickRelic(NTreasureRoom room, NTreasureRoomRelicCollection collection, NTreasureRoomRelicHolder? holder,
        int index, CancellationToken ct)
    {
        if (!SceneUtil.Alive(room) || !SceneUtil.Alive(collection)) return ActionResult.Fail("chest relics are gone");
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        var me = RoomUtil.Me();
        if (sync == null || me == null) return ActionResult.Fail("no relic synchronizer");
        List<RelicModel>? before = null;
        try { before = sync.CurrentRelics?.ToList(); } catch (Exception) { }
        if (before == null || index >= before.Count) return ActionResult.Fail("relic is no longer offered");
        bool ProceedReady()
        {
            var proceed = Proceed(room);
            try { return SceneUtil.Usable(proceed) && !proceed!.IsSkip; }
            catch (Exception) { return false; }
        }
        bool readyBefore = ProceedReady();

        if (holder != null)
        {
            // The holder's own click handler (keeps the collection's selection guards and animations).
            var pressed = UiActions.Press(holder, "relic");
            if (!pressed.Success) return pressed;
        }
        else
        {
            try { sync.PickRelicLocally(index); } // synchronized local pick / co-op vote
            catch (Exception ex) { return ActionResult.Fail("pick relic: " + ex.Message); }
        }

        bool Done()
        {
            if (!SceneUtil.Alive(room) || !SceneUtil.VisibleInTree(collection)) return true;
            if (SafeVote(sync, me) is { voteReceived: true }) return true;
            try
            {
                // Compare contents: the getter may hand out a new wrapper each call.
                var now = sync.CurrentRelics;
                if (now == null || now.Count != before.Count || now.Where((r, i) => !ReferenceEquals(r, before[i])).Any()) return true;
            }
            catch (Exception) { return true; }
            return !readyBefore && ProceedReady();
        }
        var r = await UiActions.Expect(Done, "pick relic", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }

    private static async Task<ActionResult> DoProceed(NTreasureRoom room, NProceedButton button, CancellationToken ct)
    {
        if (!SceneUtil.Alive(room)) return ActionResult.Fail("treasure room is gone");
        var me = RoomUtil.Me();
        var pressed = UiActions.Press(button, "proceed");
        if (!pressed.Success) return pressed;
        bool Done()
        {
            if (RoomUtil.Left(room) && RoomUtil.CurrentScreen() is not NTreasureRoomRelicCollection) return true;
            if (!SceneUtil.Usable(button)) return true;
            // Co-op: before the pick the button is a skip vote, shown by the room's skip vote container.
            try
            {
                var votes = Reflect.Get<NMultiplayerVoteContainer>(room, "_skipVoteContainer");
                return me != null && votes != null && votes.Players.Any(p => p.NetId == me.NetId);
            }
            catch (Exception) { return false; }
        }
        var r = await UiActions.Expect(Done, "proceed", ct: ct);
        await UiActions.WaitSettled(ct: ct);
        return r;
    }
}
