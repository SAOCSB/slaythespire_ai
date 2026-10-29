using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms2;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Rest site: options (rest/smith/... incl. co-op heal-teammate), proceed. Called for ScreenKind.RestSite. Options
/// come from <c>RestSiteSynchronizer.GetLocalOptions()</c> (the local player's own list) and are chosen with
/// <c>RestSiteSynchronizer.ChooseLocalOption</c>. Follow-up selections (SMITH's upgrade grid, relic options) are
/// separate screens handled by the selection reader. MEND (heal a teammate) asks for a target through the game's
/// target manager, which the executor answers with the chosen teammate's rest-site character.
/// </summary>
internal static class RestSiteReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var room = probe.Screen as NRestSiteRoom;
        if (room == null)
        {
            try { room = NRestSiteRoom.Instance; }
            catch (Exception) { room = null; }
        }
        if (!SceneUtil.Alive(room)) room = null;

        var sync = RunManager.Instance?.RestSiteSynchronizer;
        IReadOnlyList<RestSiteOption>? options = null;
        try { options = sync?.GetLocalOptions(); }
        catch (Exception) { /* not started */ }
        if (options == null)
        {
            try { options = room?.Options; }
            catch (Exception) { options = null; }
        }

        var info = new RestSiteInfo();
        snap.RestSite = info;
        if (options == null)
        {
            snap.Notes.Add(R2Text.T("note.rest_unreadable"));
            AddProceed(room, sink);
            return;
        }

        for (int i = 0; i < options.Count; i++)
        {
            try
            {
                var o = options[i];
                if (o == null) continue;
                info.Options.Add(new RestOptionInfo
                {
                    Index = i,
                    Id = SafeString(() => o.OptionId),
                    Name = ModelText.Loc(Safe(() => o.Title)),
                    Description = ModelText.Loc(Safe(() => o.Description)),
                    Enabled = SafeBool(() => o.IsEnabled),
                });
            }
            catch (Exception ex) { ThrottledLog.Warn("rest:option", "rest option unreadable: " + ex.Message); }
        }

        // Teammates' choices (each player uses the rest site on their own).
        if (ctx.IsMultiplayer && sync != null)
            foreach (var p in RoomUtil.PlayersInSlotOrder(ctx.Run).Where(p => p.NetId != ctx.LocalNetId))
                NoteTeammate(snap, sync, p);

        int? chosen = null;
        try { chosen = sync?.GetChosenOptionIndex(ctx.LocalNetId); }
        catch (Exception) { /* unknown */ }

        // After a pick the room disables its option buttons; if the game re-enables them (e.g. the SMITH grid was
        // cancelled) the options are offered again. Without a visible button a recorded pick blocks them.
        if (chosen != null) snap.Notes.Add(R2Text.T("note.rest_chosen"));
        if (chosen == null || room != null)
        {
            foreach (var oi in info.Options)
            {
                if (!oi.Enabled) continue;
                var option = options[oi.Index];
                if (!ButtonUsable(room, option, strict: chosen != null)) continue;
                if (IsMend(option))
                {
                    AddMendActions(ctx, snap, sink, option, oi);
                    continue;
                }
                int index = oi.Index;
                sink.Add(ActionKind.ChooseRestOption, R2Text.T("act.rest_option", oi.Name), "rest:" + oi.Id,
                    ct => ChooseAsync(option, index, null, ct),
                    new Dictionary<string, string> { ["id"] = oi.Id, [ActionArgs.Index] = index.ToString() }, unpredictable: true);
            }
        }

        AddProceed(room, sink);
    }

    private static void AddProceed(NRestSiteRoom? room, ActionSink sink)
    {
        NClickableControl? button = null;
        try { button = room?.ProceedButton; }
        catch (Exception) { /* not ready */ }
        if (!SceneUtil.Usable(button)) return;
        sink.Add(ActionKind.Proceed, R2Text.T("act.rest_leave"), "proceed",
            ct => RoomUtil.PressAndWait(button, "leave rest site", () =>
            {
                var cur = RoomUtil.CurrentScreen();
                return RoomUtil.ModalOpen() || cur != null && !ReferenceEquals(cur, room);
            }, UiActions.DefaultTimeout, ct),
            unpredictable: true);
    }

    private static void NoteTeammate(GameSnapshot snap, RestSiteSynchronizer sync, Player p)
    {
        try
        {
            var name = RoomUtil.PlayerName(snap, p.NetId);
            var theirs = sync.GetOptionsForPlayer(p.NetId);
            string Title(int? i) => i is { } k && theirs != null && k >= 0 && k < theirs.Count ? ModelText.Loc(theirs[k].Title) : "?";
            var chosen = sync.GetChosenOptionIndex(p.NetId);
            if (chosen != null) snap.Notes.Add(R2Text.T("note.rest_mate_chosen", name, Title(chosen)));
            else if (sync.GetHoveredOptionIndex(p.NetId) is { } hovered) snap.Notes.Add(R2Text.T("note.rest_mate_hover", name, Title(hovered)));
        }
        catch (Exception) { /* teammate info is optional */ }
    }

    /// <summary>MEND: one action per living teammate (the game asks for a target after the option is picked).</summary>
    private static void AddMendActions(GameContext ctx, GameSnapshot snap, ActionSink sink, RestSiteOption option, RestOptionInfo oi)
    {
        foreach (var p in RoomUtil.PlayersInSlotOrder(ctx.Run))
        {
            if (p.NetId == ctx.LocalNetId) continue;
            bool alive;
            try { alive = p.Creature?.IsAlive ?? false; }
            catch (Exception) { alive = false; }
            if (!alive) continue;
            var name = RoomUtil.PlayerName(snap, p.NetId);
            ulong target = p.NetId;
            int index = oi.Index;
            sink.Add(ActionKind.ChooseRestOption, R2Text.T("act.rest_target", oi.Name, name), $"rest:{oi.Id}@P:{target}",
                ct => ChooseAsync(option, index, target, ct),
                new Dictionary<string, string>
                {
                    ["id"] = oi.Id,
                    [ActionArgs.Index] = index.ToString(),
                    [ActionArgs.Target] = RoomUtil.PlayerKey(ctx.Run, p),
                }, unpredictable: true);
        }
    }

    private static bool IsMend(RestSiteOption option) =>
        option is MendRestSiteOption || string.Equals(SafeString(() => option.OptionId), "MEND", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The option's button, when the room shows it, must be clickable (options are disabled while animating / after a
    /// pick). <paramref name="strict"/>: a missing button counts as not clickable.
    /// </summary>
    private static bool ButtonUsable(NRestSiteRoom? room, RestSiteOption option, bool strict)
    {
        if (room == null) return !strict;
        try
        {
            var button = room.GetButtonForOption(option);
            return button == null ? !strict : SceneUtil.Usable(button);
        }
        catch (Exception) { return !strict; }
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

    // ---------------------------------------------------------------- executors

    private static bool ProceedUsable()
    {
        try { return NRestSiteRoom.Instance is { } r && SceneUtil.Alive(r) && SceneUtil.Usable(r.ProceedButton); }
        catch (Exception) { return false; }
    }

    private static async Task<ActionResult> ChooseAsync(RestSiteOption option, int index, ulong? targetNetId, CancellationToken ct)
    {
        var sync = RunManager.Instance?.RestSiteSynchronizer;
        if (sync == null) return ActionResult.Fail("no rest site synchronizer");
        var options = sync.GetLocalOptions();
        if (options == null || index < 0 || index >= options.Count || !ReferenceEquals(options[index], option))
            return ActionResult.Fail("the rest site options changed");
        if (!option.IsEnabled) return ActionResult.Fail("this rest option is disabled");
        string name = ModelText.Loc(option.Title);

        Task<bool> task;
        try { task = sync.ChooseLocalOption(index); }
        catch (Exception ex) { return ActionResult.Fail("rest option: " + ex.Message); }

        if (targetNetId is { } target) return await FinishMendAsync(task, target, name, ct);

        // SMITH and similar options keep the task open until their follow-up selection is done, so the wait also
        // ends when another screen / popup takes over or the proceed button appears.
        var r = await UiActions.Expect(() => task.IsCompleted || RoomUtil.CurrentScreen() is not NRestSiteRoom || RoomUtil.ModalOpen() || ProceedUsable(),
            "rest option", UiActions.DefaultTimeout, ct);
        if (!task.IsCompleted) RoomUtil.Observe(task, "rest option " + name);
        else if (task.IsFaulted) return ActionResult.Fail("rest option failed: " + task.Exception?.GetBaseException().Message);
        else if (RoomUtil.Outcome(task) == false && RoomUtil.CurrentScreen() is NRestSiteRoom)
            return ActionResult.Fail("the game refused " + name);
        await RoomUtil.Settle(ct);
        return r.Success ? ActionResult.Ok(name) : r;
    }

    private static async Task<ActionResult> FinishMendAsync(Task<bool> task, ulong targetNetId, string name, CancellationToken ct)
    {
        // The option starts targeting (like aiming a card at an ally); answer it with the teammate's character.
        bool targeting = await MainThread.WaitUntil(() => task.IsCompleted || NTargetManager.Instance is { IsInSelection: true } tm && SceneUtil.Alive(tm),
            RoomUtil.ShortWait, ct);
        var manager = NTargetManager.Instance;
        if (!targeting || task.IsCompleted || manager == null || !manager.IsInSelection)
        {
            RoomUtil.Observe(task, name);
            return ActionResult.Fail(name + ": target selection did not start");
        }

        var room = NRestSiteRoom.Instance;
        Player? player = null;
        try { player = RunManager.Instance?.DebugOnlyGetState()?.GetPlayer(targetNetId); }
        catch (Exception) { }
        NRestSiteCharacter? node = null;
        try
        {
            if (SceneUtil.Alive(room) && player != null)
                node = room!.GetCharacterForPlayer(player)
                       ?? room.Characters?.FirstOrDefault(c => SceneUtil.Alive(c) && c.Player?.NetId == targetNetId);
        }
        catch (Exception) { /* handled below */ }
        if (!SceneUtil.Alive(node))
        {
            // Back out of the targeting like a right-click would; the option is not used.
            try { manager.CancelTargeting(); }
            catch (Exception) { }
            return ActionResult.Fail(name + ": teammate not found at the rest site");
        }

        // Prefer the node the target manager accepts (the character, or its hitbox).
        Node target = node!;
        try
        {
            if (!manager.AllowedToTargetNode(node!) && node!.Hitbox is { } hb && SceneUtil.Alive(hb) && manager.AllowedToTargetNode(hb))
                target = hb;
        }
        catch (Exception) { /* keep the character */ }

        try
        {
            manager.OnNodeHovered(target);
            // FinishTargeting(bool cancel) is private but registered with Godot, so it can be called by name.
            manager.Call(NTargetManager.MethodName.FinishTargeting, false);
        }
        catch (Exception ex) { return ActionResult.Fail(name + ": " + ex.Message); }

        if (!await RoomUtil.WaitTask(task, UiActions.DefaultTimeout, ct))
        {
            RoomUtil.Observe(task, name);
            return ActionResult.Fail(name + ": no result (timed out)");
        }
        if (RoomUtil.Outcome(task) != true) return ActionResult.Fail("the game refused " + name);
        await RoomUtil.Settle(ct);
        return ActionResult.Ok(name);
    }
}
