using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms2;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Events incl. Ancients (Neow-like) and multi-page events: title, body, options; choose option, proceed. Called for
/// ScreenKind.Event. The event is read from <c>EventSynchronizer.GetLocalEvent()</c> (the local player's instance,
/// co-op safe) and options are chosen with <c>EventSynchronizer.ChooseLocalOption</c>, which also casts the vote in
/// shared co-op events. Multi-page events mutate the same model, so each snapshot simply shows the current page.
/// The Crystal Sphere minigame overlay is delegated to <see cref="CrystalSphere"/>.
/// </summary>
internal static class EventReader
{
    /// <summary>Dialogue line on which a click on the Ancient's dialogue had no effect (then the options are offered directly).</summary>
    private static (ulong layout, int line)? _stuckDialogue;

    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        if (probe.Screen is NCrystalSphereScreen sphere)
        {
            CrystalSphere.Read(ctx, sphere, snap, sink);
            return;
        }

        var room = probe.Screen as NEventRoom;
        if (room == null)
        {
            try { room = NEventRoom.Instance; }
            catch (Exception) { room = null; }
        }
        if (!SceneUtil.Alive(room)) room = null;

        var ev = LocalEvent(room);
        if (ev == null)
        {
            snap.Notes.Add(R2Text.T("note.event_unreadable"));
            return;
        }

        var info = new EventInfo
        {
            Id = ModelText.SafeId(ev),
            Title = Format(ev, Safe(() => ev.Title)),
            Body = Format(ev, Safe(() => ev.Description)),
        };
        if (info.Body.Length == 0) info.Body = Format(ev, Safe(() => ev.InitialDescription));
        snap.Event = info;

        // Ancients talk first; the choices appear after the dialogue (advance it like a player would).
        if (room?.Layout is NAncientEventLayout ancient && DialogueHitbox(ancient) is { } hitbox && SceneUtil.Usable(hitbox))
        {
            int line = Reflect.Get<int>(ancient, "_currentDialogueLine");
            ulong layoutId = ancient.GetInstanceId();
            if (_stuckDialogue != (layoutId, line))
            {
                FillOptions(ctx, ev, info);
                snap.Notes.Add(R2Text.T("note.event_dialogue"));
                var dialogue = RoomUtil.Node<Godot.Node>(ancient, "_dialogueContainer");
                foreach (var text in RoomUtil.Texts(dialogue, 3, skipButtons: true)) snap.Notes.Add("“" + text + "”");
                sink.Add(ActionKind.Proceed, R2Text.T("act.event_advance"), "event:dialogue",
                    ct => AdvanceDialogueAsync(ancient, hitbox, ct), unpredictable: true);
                return;
            }
        }

        if (SafeBool(() => ev.IsFinished))
        {
            var proceedText = ProceedText(room);
            info.Options.Add(new EventOptionInfo { Index = 0, Text = proceedText });
            sink.Add(ActionKind.Proceed, R2Text.T("act.event_leave"), "proceed", ct => ProceedAsync(ev, ct), unpredictable: true);
            return;
        }

        var options = FillOptions(ctx, ev, info);
        if (options.Count == 0)
        {
            snap.Notes.Add(R2Text.T("note.event_no_options"));
            return;
        }

        // Shared co-op events are decided by a vote: show the votes, and wait once the local vote is cast.
        var sync = RunManager.Instance?.EventSynchronizer;
        if (ctx.IsMultiplayer && sync != null && SafeBool(() => sync.IsShared) && ctx.Run != null)
        {
            snap.Notes.Add(R2Text.T("note.event_shared"));
            uint? myVote = null;
            foreach (var p in RoomUtil.PlayersInSlotOrder(ctx.Run))
            {
                uint? vote = null;
                try { vote = sync.GetPlayerVote(p); }
                catch (Exception) { /* not voting */ }
                if (p.NetId == ctx.LocalNetId)
                {
                    myVote = vote;
                    continue;
                }
                var name = RoomUtil.PlayerName(snap, p.NetId);
                snap.Notes.Add(vote is { } v ? R2Text.T("note.event_vote", name, OptionText(info, (int)v)) : R2Text.T("note.event_novote", name));
            }
            if (myVote is { } mine)
            {
                snap.Notes.Add(R2Text.T("note.event_voted", OptionText(info, (int)mine)));
                return;
            }
        }

        // Options that would kill the player go last (and are marked), so a careless pick never ends the run.
        foreach (var (option, oi, lethal) in options.OrderBy(o => o.lethal))
        {
            if (oi.Locked) continue;
            var label = R2Text.T("act.event_option", oi.Text);
            if (lethal) label += " " + R2Text.T("word.lethal");
            var key = $"event:{oi.Index}:{(string.IsNullOrEmpty(SafeString(() => option.TextKey)) ? oi.Text : option.TextKey)}";
            var args = new Dictionary<string, string> { [ActionArgs.Index] = oi.Index.ToString() };
            if (lethal) args["lethal"] = "true";
            int index = oi.Index;
            var opt = option;
            sink.Add(ActionKind.ChooseEventOption, label, key, ct => ChooseAsync(ctx.LocalNetId, ev, opt, index, ct), args, unpredictable: true);
        }
    }

    // ---------------------------------------------------------------- reading

    private static EventModel? LocalEvent(NEventRoom? room)
    {
        try
        {
            var ev = RunManager.Instance?.EventSynchronizer?.GetLocalEvent();
            if (ev != null) return ev;
        }
        catch (Exception) { /* no event running */ }
        // Fallback: the event the room node shows (private field).
        return Reflect.Get<EventModel>(room, "_event");
    }

    private static List<(EventOption option, EventOptionInfo info, bool lethal)> FillOptions(GameContext ctx, EventModel ev, EventInfo info)
    {
        var result = new List<(EventOption, EventOptionInfo, bool)>();
        IReadOnlyList<EventOption>? options = null;
        try { options = ev.CurrentOptions; }
        catch (Exception) { /* between pages */ }
        if (options == null) return result;

        Player? owner = null;
        try { owner = ev.Owner; }
        catch (Exception) { }
        owner ??= ctx.Me;

        for (int i = 0; i < options.Count; i++)
        {
            try
            {
                var o = options[i];
                if (o == null) continue;
                var text = Format(ev, Safe(() => o.Title));
                var description = Format(ev, Safe(() => o.Description));
                var relic = Safe(() => o.Relic);
                if (relic != null)
                {
                    var ri = ModelText.Relic(relic);
                    if (text.Length == 0) text = ri.Name;
                    if (description.Length == 0) description = ri.Description;
                    else if (ri.Name.Length > 0 && !description.Contains(ri.Name)) description = ri.Name + ": " + description;
                }
                if (text.Length == 0) text = LastSegment(SafeString(() => o.TextKey));

                bool lethal = false;
                try { lethal = owner != null && o.WillKillPlayer != null && o.WillKillPlayer(owner); }
                catch (Exception) { /* predicate needs combat state etc.; treat as safe */ }

                var oi = new EventOptionInfo
                {
                    Index = i,
                    Text = text,
                    Description = lethal ? (description + " " + R2Text.T("word.lethal")).Trim() : description,
                    Locked = SafeBool(() => o.IsLocked),
                };
                info.Options.Add(oi);
                result.Add((o, oi, lethal));
            }
            catch (Exception ex) { ThrottledLog.Warn("event:option", "event option unreadable: " + ex.Message); }
        }
        return result;
    }

    /// <summary>
    /// Formats event text with the event's variables (gold amounts, HP costs ...). Adding the variables to the
    /// LocString is what the event UI does before showing it; it does not touch game state.
    /// </summary>
    private static string Format(EventModel ev, LocString? text)
    {
        if (text == null) return "";
        try { ev.DynamicVars?.AddTo(text); }
        catch (Exception) { /* show unformatted */ }
        return ModelText.Loc(text);
    }

    private static string OptionText(EventInfo info, int index) =>
        info.Options.FirstOrDefault(o => o.Index == index)?.Text ?? "#" + index;

    private static string ProceedText(NEventRoom? room)
    {
        var button = ProceedButton(room);
        var text = RoomUtil.ButtonText(button);
        return text.Length > 0 ? text : R2Text.T("act.event_leave");
    }

    private static NEventOptionButton? ProceedButton(NEventRoom? room)
    {
        try
        {
            if (!SceneUtil.Alive(room) || room!.Layout == null) return null;
            return room.Layout.OptionButtons?.FirstOrDefault(b => SceneUtil.Alive(b) && b.Option is { IsProceed: true });
        }
        catch (Exception) { return null; }
    }

    private static NAncientDialogueHitbox? DialogueHitbox(NAncientEventLayout layout) =>
        RoomUtil.Node<NAncientDialogueHitbox>(layout, "_dialogueHitbox")
        ?? SceneUtil.Descendants<NAncientDialogueHitbox>(layout).FirstOrDefault();

    /// <summary>Order-sensitive fingerprint of the current page (options change in place on multi-page events).</summary>
    private static string Signature(EventModel ev)
    {
        try
        {
            var parts = ev.CurrentOptions?.Select(o => o == null ? "-" : $"{o.TextKey}:{o.IsLocked}:{o.IsProceed}:{o.WasChosen}") ?? Enumerable.Empty<string>();
            return string.Join("|", parts) + "#" + (ev.Description?.GetHashCode() ?? 0);
        }
        catch (Exception) { return "?"; }
    }

    private static T? Safe<T>(Func<T?> get) where T : class
    {
        try { return get(); }
        catch (Exception) { return null; }
    }

    private static string SafeString(Func<string?> get)
    {
        try { return get() ?? ""; }
        catch (Exception) { return ""; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch (Exception) { return false; }
    }

    private static string LastSegment(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot >= 0 && dot < key.Length - 1 ? key[(dot + 1)..] : key;
    }

    // ---------------------------------------------------------------- executors

    private static async Task<ActionResult> ChooseAsync(ulong localNetId, EventModel ev, EventOption option, int index, CancellationToken ct)
    {
        var sync = RunManager.Instance?.EventSynchronizer;
        if (sync == null) return ActionResult.Fail("no event synchronizer");
        EventModel? current;
        try { current = sync.GetLocalEvent(); }
        catch (Exception) { current = null; }
        if (!ReferenceEquals(current, ev)) return ActionResult.Fail("the event changed");
        if (ev.IsFinished) return ActionResult.Fail("the event is already finished");
        var options = ev.CurrentOptions;
        if (options == null || index < 0 || index >= options.Count || !ReferenceEquals(options[index], option))
            return ActionResult.Fail("the event options changed");
        if (option.IsLocked) return ActionResult.Fail("this option is locked");

        bool shared = SafeBool(() => sync.IsShared);
        Player? me = null;
        try { me = RunManager.Instance?.DebugOnlyGetState()?.GetPlayer(localNetId); }
        catch (Exception) { }
        string signature = Signature(ev);

        try { sync.ChooseLocalOption(index); }
        catch (Exception ex) { return ActionResult.Fail("choose option: " + ex.Message); }

        var r = await UiActions.Expect(() =>
        {
            if (RoomUtil.CurrentScreen() is not NEventRoom || RoomUtil.ModalOpen()) return true; // selection overlay, map, combat ...
            EventModel? now = null;
            try { now = RunManager.Instance?.EventSynchronizer?.GetLocalEvent(); }
            catch (Exception) { }
            if (!ReferenceEquals(now, ev) || ev.IsFinished || Signature(ev) != signature) return true;
            if (CombatManager.Instance is { IsInProgress: true }) return true;
            // Shared event: our vote is registered; the option resolves once everyone voted.
            return shared && me != null && sync.GetPlayerVote(me) != null;
        }, "event option", UiActions.DefaultTimeout, ct);
        await RoomUtil.Settle(ct);
        return r;
    }

    private static async Task<ActionResult> ProceedAsync(EventModel ev, CancellationToken ct)
    {
        var room = NEventRoom.Instance;
        bool Left() => RoomUtil.CurrentScreen() is not NEventRoom || RoomUtil.ModalOpen();

        var button = ProceedButton(SceneUtil.Alive(room) ? room : null);
        if (button != null && SceneUtil.Usable(button))
            return await RoomUtil.PressAndWait(button, "leave event", Left, UiActions.DefaultTimeout, ct);

        // No proceed button (custom event layouts): use the room's own proceed. It opens the map, which must exist.
        if (NMapScreen.Instance == null) return ActionResult.Fail("leave event: the map is not ready yet");
        Task task;
        try { task = NEventRoom.Proceed(); }
        catch (Exception ex) { return ActionResult.Fail("leave event: " + ex.Message); }
        RoomUtil.Observe(task, "event proceed");
        var r = await UiActions.Expect(Left, "leave event", UiActions.DefaultTimeout, ct);
        await RoomUtil.Settle(ct);
        return r;
    }

    private static async Task<ActionResult> AdvanceDialogueAsync(NAncientEventLayout layout, NAncientDialogueHitbox hitbox, CancellationToken ct)
    {
        if (!SceneUtil.Alive(layout)) return ActionResult.Fail("the dialogue is gone");
        int line = Reflect.Get<int>(layout, "_currentDialogueLine");
        ulong id = layout.GetInstanceId();
        var pressed = UiActions.Press(hitbox, "continue dialogue");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => !SceneUtil.Usable(hitbox) || Reflect.Get<int>(layout, "_currentDialogueLine") != line,
            "continue dialogue", TimeSpan.FromSeconds(3), ct);
        if (!r.Success)
        {
            // Do not loop on a dialogue that ignores clicks: next snapshot offers the choices directly.
            _stuckDialogue = (id, line);
            return r;
        }
        await MainThread.Frames(3);
        return ActionResult.Ok();
    }
}
