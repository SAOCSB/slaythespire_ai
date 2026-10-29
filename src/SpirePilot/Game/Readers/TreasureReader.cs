using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    /// <summary>After this long in the "opening" state with no relic collection, the chest's state is not trusted any more.</summary>
    private static readonly TimeSpan OpeningGiveUp = TimeSpan.FromSeconds(20);

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

        var collection = Collection(room);
        var state = ChestState.Of(room, collection);
        TrackShown(collection, state.Shown);

        // 2) Nothing (more) to take: an empty chest, or the collection says picking is over.
        if (state.Empty == true || state.Finished == true)
        {
            AddProceed(sink, room, RoomUtil.T("act.proceed"));
            return;
        }

        // 3) Relic picking (one relic per player in co-op; everyone votes, collisions are fought over).
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        IReadOnlyList<RelicModel>? relics = null;
        try { relics = sync?.CurrentRelics; } catch (Exception) { }
        if (state.Shown && relics is { Count: > 0 })
        {
            try { ReadPicking(ctx, snap, sink, room, collection!, sync!, relics, me); }
            catch (Exception ex) { ThrottledLog.Warn("treasure-pick", "chest relics unreadable: " + ex.Message); }
            return;
        }

        // 4) The chest is opening (the click registered, the relics are not up yet): a proceed now would forfeit the relic.
        bool stateKnown = state.Empty != null && state.Finished != null;
        if (stateKnown && OpenedFor(room) < OpeningGiveUp)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.chest_opening"));
            return;
        }

        // Fallback (collection state unreadable, or stuck): the old heuristics. While the button still reads "skip" a relic
        // may be on its way, so wait unless there is clearly nothing to pick.
        bool nothingToPick = !state.Shown && relics is not { Count: > 0 };
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

    /// <summary>
    /// The relic collection's own state: <c>_isEmptyChest</c>, <c>RelicPickingBegan()</c> / <c>RelicPickingFinished()</c>
    /// (completed tasks), and whether it is shown. Null = unreadable.
    /// </summary>
    private readonly record struct ChestState(bool? Empty, bool? Began, bool? Finished, bool Shown)
    {
        public static ChestState Of(NTreasureRoom room, NTreasureRoomRelicCollection? collection)
        {
            if (!SceneUtil.Alive(collection)) return new ChestState(null, null, null, false);
            bool? empty = Reflect.GetOptional<object>(collection, "_isEmptyChest") is bool e ? e : null;
            bool? began = TaskDone(() => collection!.RelicPickingBegan());
            bool? finished = TaskDone(() => collection!.RelicPickingFinished());
            bool shown = SceneUtil.VisibleInTree(collection) &&
                         (Reflect.GetOptional<object>(room, "_isRelicCollectionOpen") is not false || ReferenceEquals(RoomUtil.CurrentScreen(), collection));
            return new ChestState(empty, began, finished, shown);
        }

        private static bool? TaskDone(Func<Task?> get)
        {
            try { return get() is { } t ? t.IsCompleted : null; }
            catch (Exception) { return null; }
        }
    }

    private static readonly ConditionalWeakTable<NTreasureRoom, StrongBox<long>> OpenedAt = new();
    private static readonly ConditionalWeakTable<NTreasureRoomRelicCollection, StrongBox<long>> ShownAt = new();

    /// <summary>How long the reader has seen this room's chest opened.</summary>
    private static TimeSpan OpenedFor(NTreasureRoom room) =>
        Stopwatch.GetElapsedTime(OpenedAt.GetValue(room, _ => new StrongBox<long>(Stopwatch.GetTimestamp())).Value);

    private static void TrackShown(NTreasureRoomRelicCollection? collection, bool shown)
    {
        if (collection == null) return;
        if (!shown) ShownAt.Remove(collection);
        else ShownAt.GetValue(collection, _ => new StrongBox<long>(Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Waits out the collection's opening guard (it ignores picks for <c>_noSelectionTimeMsec</c> after <c>_openedTicks</c>),
    /// at least ~600 ms after it appeared, at most 2 s.
    /// </summary>
    private static async Task WaitOpenGuard(NTreasureRoomRelicCollection collection, CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(600) - (ShownAt.TryGetValue(collection, out var box) ? Stopwatch.GetElapsedTime(box.Value) : TimeSpan.Zero);
        try
        {
            ulong? opened = Reflect.GetOptional<object>(collection, "_openedTicks") is ulong o ? o : null;
            ulong? guard = Reflect.GetOptional<object>(collection, "_noSelectionTimeMsec") is ulong g ? g : null;
            ulong now = Time.GetTicksMsec();
            if (opened is > 0 && guard is { } ms && now >= opened && now - opened.Value < 60_000)
            {
                var remaining = TimeSpan.FromMilliseconds((double)ms + 100 - (now - opened.Value));
                if (remaining > wait) wait = remaining;
            }
        }
        catch (Exception) { /* fall back to the time since it appeared */ }
        if (wait > TimeSpan.FromSeconds(2)) wait = TimeSpan.FromSeconds(2);
        if (wait > TimeSpan.Zero) await MainThread.Delay(wait, ct);
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
        // The click flips _hasChestBeenOpened at once; the relics come after the opening animation. Done once the
        // collection shows relics, the chest turns out empty, or picking is already over.
        bool Ready()
        {
            if (!SceneUtil.Alive(room) || RoomUtil.Left(room) && RoomUtil.CurrentScreen() is not NTreasureRoomRelicCollection) return true;
            var state = ChestState.Of(room, Collection(room));
            if (state.Empty == true || state.Finished == true) return true;
            if (!state.Shown) return false;
            if (state.Began == true) return true;
            try { return RunManager.Instance?.TreasureRoomRelicSynchronizer?.CurrentRelics is { Count: > 0 }; }
            catch (Exception) { return true; }
        }
        var r = await UiActions.Expect(Ready, "open chest", ct: ct);
        if (!r.Success && SceneUtil.Alive(room) && ChestOpened(room, chest))
            r = ActionResult.Ok("chest opened; relics not shown yet");
        await RoomUtil.SettleAsync(ct);
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
        if (SafeVote(sync, me) is { voteReceived: true }) return ActionResult.Fail("a relic was already picked");

        bool Done()
        {
            if (!SceneUtil.Alive(room) || !SceneUtil.VisibleInTree(collection)) return true;
            if (SafeVote(sync, me) is { voteReceived: true }) return true;
            if (ChestState.Of(room, collection).Finished == true) return true;
            if (RoomUtil.AwaitingPlayerInput()) return true; // the relic's pickup opened a choice
            try
            {
                // Compare contents: the getter may hand out a new wrapper each call.
                var now = sync.CurrentRelics;
                if (now == null || now.Count != before.Count || now.Where((r, i) => !ReferenceEquals(r, before[i])).Any()) return true;
            }
            catch (Exception) { return true; }
            return false;
        }

        if (holder != null)
        {
            // The holder's own click handler (keeps the collection's selection guards and animations); it ignores clicks
            // right after the collection opened, so wait that out first and re-press once if the first click was eaten.
            await WaitOpenGuard(collection, ct);
            if (Done()) return ActionResult.Ok();
            var pressed = UiActions.Press(holder, "relic");
            if (!pressed.Success) return pressed;
            if (!await MainThread.WaitUntil(Done, TimeSpan.FromSeconds(1.5), ct) && SceneUtil.Usable(holder) && !Done())
                UiActions.Press(holder, "relic");
        }
        else
        {
            try { sync.PickRelicLocally(index); } // synchronized local pick / co-op vote
            catch (Exception ex) { return ActionResult.Fail("pick relic: " + ex.Message); }
        }

        var r = await UiActions.Expect(Done, "pick relic", ct: ct);
        // A relic whose pickup opens a selection keeps its action running until that choice is made: SettleAsync stops there.
        await RoomUtil.SettleAsync(ct);
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
