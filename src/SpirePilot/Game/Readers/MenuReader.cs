using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms2;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Main menu (continue / new run), character select (characters, ascension, embark; co-op lobby picks/ready), game
/// over (continue / main menu), timeline/unlock flow. ReadMainMenu, ReadCharacterSelect, ReadGameOver.
/// Admin actions (abandon run, multiplayer, settings, compendium, profiles, quit) are never offered; menu pages the
/// AI has no business on are left to the human.
/// </summary>
internal static class MenuReader
{
    // ================================================================ main menu

    public static void ReadMainMenu(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        switch (probe.Screen)
        {
            case NMainMenu menu:
                MainMenu(menu, snap, sink);
                return;
            case NSingleplayerSubmenu sp:
                Singleplayer(sp, sink);
                return;
            case NTimelineScreen timeline:
                Timeline(timeline, snap, sink);
                return;
            case NLogoAnimation:
                snap.Notes.Add(R2Text.T("note.menu_intro"));
                return;
        }
        var visibleTimeline = TimelineInstance();
        if (visibleTimeline != null)
        {
            Timeline(visibleTimeline, snap, sink);
            return;
        }
        snap.Notes.Add(R2Text.T("note.menu_page", probe.Name));
    }

    private static void MainMenu(NMainMenu menu, GameSnapshot snap, ActionSink sink)
    {
        try
        {
            if (menu.SubmenuStack is { SubmenusOpen: true }) return; // a page is opening on top; wait for it
        }
        catch (Exception) { }

        bool hasSave = false;
        try { hasSave = SaveManager.Instance?.HasRunSave ?? false; }
        catch (Exception) { }

        var cont = MenuButton(menu, "_continueButton", "MainMenuTextButtons/ContinueButton");
        if (SceneUtil.Usable(cont))
        {
            foreach (var line in RoomUtil.Texts(Safe(() => menu.ContinueRunInfo), 4, skipButtons: true))
                snap.Notes.Add(R2Text.T("note.menu_run_info", line));
            sink.Add(ActionKind.ContinueRun, R2Text.T("act.continue_run"), "menu:continue",
                ct => RoomUtil.PressAndWait(cont, "continue run", () => RoomUtil.ModalOpen() || RoomUtil.CurrentScreen() is not NMainMenu,
                    TimeSpan.FromSeconds(20), ct),
                unpredictable: true);
        }
        else if (hasSave) snap.Notes.Add(R2Text.T("note.menu_has_save"));

        // While a run is saved the game swaps "single player" for continue/abandon; only offer it when it is shown.
        var single = MenuButton(menu, "_singleplayerButton", "MainMenuTextButtons/SingleplayerButton");
        if (SceneUtil.Usable(single))
            sink.Add(ActionKind.StartNewRun, R2Text.T("act.new_run"), "menu:new_run", ct => NewRunFromMenuAsync(menu, single!, ct), unpredictable: true);
    }

    private static void Singleplayer(NSingleplayerSubmenu sp, ActionSink sink)
    {
        var standard = StandardButton(sp);
        if (SceneUtil.Usable(standard))
            sink.Add(ActionKind.StartNewRun, R2Text.T("act.new_run"), "menu:new_run", ct => PressStandardAsync(sp, ct), unpredictable: true);
    }

    private static NMainMenuTextButton? MenuButton(NMainMenu menu, string field, string path) =>
        RoomUtil.Node<NMainMenuTextButton>(menu, field) ?? SceneUtil.Get<NMainMenuTextButton>(menu, path);

    private static NClickableControl? StandardButton(NSingleplayerSubmenu sp) =>
        RoomUtil.Node<NClickableControl>(sp, "_standardButton") ?? SceneUtil.Get<NClickableControl>(sp, "%StandardButton");

    private static async Task<ActionResult> NewRunFromMenuAsync(NMainMenu menu, NMainMenuTextButton single, CancellationToken ct)
    {
        var opened = await RoomUtil.PressAndWait(single, "single player",
            () => RoomUtil.ModalOpen() || RoomUtil.CurrentScreen() is NSingleplayerSubmenu or NCharacterSelectScreen, UiActions.DefaultTimeout, ct);
        if (!opened.Success) return opened;
        // Daily / custom may still be locked; then the game can go straight to character select.
        if (RoomUtil.CurrentScreen() is NSingleplayerSubmenu sp) return await PressStandardAsync(sp, ct);
        return ActionResult.Ok();
    }

    private static async Task<ActionResult> PressStandardAsync(NSingleplayerSubmenu sp, CancellationToken ct)
    {
        await MainThread.WaitUntil(() => SceneUtil.Usable(StandardButton(sp)), RoomUtil.ShortWait, ct);
        return await RoomUtil.PressAndWait(StandardButton(sp), "standard run",
            () => RoomUtil.ModalOpen() || RoomUtil.CurrentScreen() is NCharacterSelectScreen, UiActions.DefaultTimeout, ct);
    }

    // ---------------------------------------------------------------- timeline (epochs / unlocks after runs)

    private static NTimelineScreen? TimelineInstance()
    {
        try
        {
            var t = NTimelineScreen.Instance;
            return SceneUtil.VisibleInTree(t) ? t : null;
        }
        catch (Exception) { return null; }
    }

    private static void Timeline(NTimelineScreen timeline, GameSnapshot snap, ActionSink sink)
    {
        snap.Notes.Add(R2Text.T("note.timeline"));

        // Overlays inside the timeline come first; each blocks the timeline below it.
        NUnlockScreen? unlock = null;
        try { unlock = timeline.CurrentUnlockScreen; }
        catch (Exception) { }
        if (!SceneUtil.VisibleInTree(unlock)) unlock = SceneUtil.Descendants<NUnlockScreen>(timeline).FirstOrDefault(u => SceneUtil.VisibleInTree(u));
        if (unlock != null)
        {
            foreach (var t in RoomUtil.Texts(unlock, 6, skipButtons: true)) snap.Notes.Add(R2Text.T("note.popup", t));
            var confirm = PopupReader.UnlockConfirmButton(unlock);
            if (confirm != null)
                AddOverlayButton(sink, confirm, "popup:confirm", unlock);
            return;
        }

        var tutorial = RoomUtil.Node<NTimelineTutorial>(timeline, "_tutorial");
        if (SceneUtil.VisibleInTree(tutorial))
        {
            foreach (var t in RoomUtil.Texts(tutorial, 4, skipButtons: true)) snap.Notes.Add(R2Text.T("note.popup", t));
            var ack = RoomUtil.Node<NClickableControl>(tutorial, "_acknowledgeButton")
                      ?? RoomUtil.FirstUsable(SceneUtil.Descendants<NAcknowledgeButton>(tutorial));
            if (SceneUtil.Usable(ack)) AddOverlayButton(sink, ack!, "popup:confirm", tutorial!);
            return;
        }

        var inspect = RoomUtil.Node<NEpochInspectScreen>(timeline, "_inspectScreen")
                      ?? SceneUtil.Get<NEpochInspectScreen>(timeline, "%EpochInspectScreen");
        if (SceneUtil.VisibleInTree(inspect))
        {
            foreach (var t in RoomUtil.Texts(inspect, 4, skipButtons: true)) snap.Notes.Add(t);
            var close = RoomUtil.Node<NClickableControl>(inspect, "_closeButton")
                        ?? RoomUtil.FirstUsable(SceneUtil.Descendants<NCloseButton>(inspect));
            if (SceneUtil.Usable(close))
                sink.Add(ActionKind.DismissPopup, R2Text.T("act.close_epoch"), "popup:dismiss",
                    ct => RoomUtil.PressAndWait(close, "close epoch", () => !SceneUtil.VisibleInTree(inspect), UiActions.DefaultTimeout, ct));
            return;
        }

        // New epochs ("obtained", not yet revealed) unlock characters / cards / relics when revealed. The order does
        // not matter, so only the first one is offered (a one-action menu needs no AI decision); leaving is offered
        // once nothing is left to reveal (or revealing did not work).
        var slot = SceneUtil.Descendants<NEpochSlot>(timeline)
            .Where(s => SceneUtil.Usable(s) && s.model != null && s.State == EpochSlotState.Obtained && !FailedEpochs.Contains(s.GetInstanceId()))
            .OrderBy(s => s.GlobalPosition.X).ThenBy(s => s.GlobalPosition.Y)
            .FirstOrDefault();
        if (slot != null)
        {
            string id = "", title = "";
            try
            {
                id = slot.model.Id ?? "";
                title = ModelText.Loc(slot.model.Title);
            }
            catch (Exception) { }
            sink.Add(ActionKind.ChooseOption, R2Text.T("act.reveal_epoch", title.Length > 0 ? title : id), "timeline:reveal:" + id,
                ct => RevealEpochAsync(timeline, slot, ct), unpredictable: true);
            return;
        }

        var back = RoomUtil.Node<NClickableControl>(timeline, "_backButton") ?? SceneUtil.Get<NClickableControl>(timeline, "BackButton");
        if (SceneUtil.Usable(back))
            sink.Add(ActionKind.ReturnToMenu, R2Text.T("act.timeline_back"), "menu:back",
                ct => RoomUtil.PressAndWait(back, "leave timeline", () => RoomUtil.ModalOpen() || RoomUtil.CurrentScreen() is not NTimelineScreen,
                    UiActions.DefaultTimeout, ct));
    }

    /// <summary>Epoch slots whose click had no visible effect (skipped so the timeline can still be left).</summary>
    private static readonly HashSet<ulong> FailedEpochs = new();

    private static async Task<ActionResult> RevealEpochAsync(NTimelineScreen timeline, NEpochSlot slot, CancellationToken ct)
    {
        var r = await RoomUtil.PressAndWait(slot, "reveal epoch", () =>
                !SceneUtil.Alive(slot) || slot.State != EpochSlotState.Obtained || RoomUtil.ModalOpen() ||
                SceneUtil.VisibleInTree(RoomUtil.Node<NEpochInspectScreen>(timeline, "_inspectScreen")),
            TimeSpan.FromSeconds(15), ct);
        if (!r.Success && SceneUtil.Valid(slot))
            lock (FailedEpochs)
            {
                if (FailedEpochs.Count > 64) FailedEpochs.Clear();
                FailedEpochs.Add(slot.GetInstanceId());
            }
        return r;
    }

    private static void AddOverlayButton(ActionSink sink, NClickableControl button, string key, Control overlay)
    {
        var text = RoomUtil.ButtonText(button);
        sink.Add(ActionKind.DismissPopup, R2Text.T("act.dismiss", text.Length > 0 ? text : R2Text.T("word.ok")), key,
            ct => RoomUtil.PressAndWait(button, "timeline overlay", () => !SceneUtil.VisibleInTree(overlay), UiActions.DefaultTimeout, ct),
            unpredictable: true);
    }

    // ================================================================ character select

    public static void ReadCharacterSelect(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        if (probe.Screen is not NCharacterSelectScreen screen || !SceneUtil.Alive(screen)) return;
        var lobby = Safe(() => screen.Lobby);
        var info = new CharacterSelectInfo();
        snap.CharacterSelect = info;

        string? selectedId = LocalCharacterId(screen);
        bool multiplayer = false, client = false, ready = false;
        if (lobby != null)
        {
            try
            {
                var type = lobby.NetService?.Type ?? NetGameType.Singleplayer;
                multiplayer = type is NetGameType.Host or NetGameType.Client;
                client = type == NetGameType.Client;
            }
            catch (Exception) { }
            try { ready = lobby.LocalPlayer.isReady; }
            catch (Exception) { }
            try
            {
                info.Ascension = lobby.Ascension;
                info.MaxAscension = lobby.MaxAscension;
            }
            catch (Exception) { }
        }
        info.LocalReady = ready;

        var buttons = SceneUtil.ByScreenPosition(SceneUtil.Descendants<NCharacterSelectButton>(screen).Where(b => SceneUtil.VisibleInTree(b)));
        var selectable = new List<(NCharacterSelectButton button, CharacterOption option)>();
        foreach (var b in buttons)
        {
            try
            {
                if (b.IsRandom) continue; // the "random" tile is not a character
                var model = b.Character;
                if (model == null) continue;
                var option = new CharacterOption
                {
                    Id = ModelText.SafeId(model),
                    Name = ModelText.Loc(model.Title),
                    Locked = b.IsLocked,
                };
                option.Selected = selectedId != null ? option.Id == selectedId : SafeBool(() => b.IsSelected);
                if (option.Name.Length == 0) option.Name = option.Id;
                if (info.Characters.Any(c => c.Id == option.Id)) continue;
                info.Characters.Add(option);
                selectable.Add((b, option));
            }
            catch (Exception ex) { ThrottledLog.Warn("charselect:button", "character button unreadable: " + ex.Message); }
        }

        if (multiplayer && lobby != null) FillLobby(lobby, info, snap);

        if (ready)
        {
            snap.Notes.Add(R2Text.T("note.char_ready"));
            return;
        }

        foreach (var (button, option) in selectable)
        {
            if (option.Locked || option.Selected || !SceneUtil.Usable(button)) continue;
            var id = option.Id;
            var b = button;
            sink.Add(ActionKind.SelectCharacter, R2Text.T("act.select_character", option.Name), "char:" + id,
                ct => SelectCharacterAsync(screen, b, id, ct), new Dictionary<string, string> { [ActionArgs.Character] = id });
        }

        if (lobby != null && !client)
        {
            for (int n = 0; n <= info.MaxAscension; n++)
            {
                if (n == info.Ascension) continue;
                int level = n;
                sink.Add(ActionKind.SetAscension, R2Text.T("act.set_ascension", level), "ascension:" + level,
                    ct => SetAscensionAsync(screen, level, ct), new Dictionary<string, string> { [ActionArgs.Ascension] = level.ToString() });
            }
        }
        else if (client) snap.Notes.Add(R2Text.T("note.char_client_asc"));

        var embark = EmbarkButton(screen);
        if (selectedId != null && SceneUtil.Usable(embark))
            sink.Add(ActionKind.Embark, R2Text.T("act.embark"), "embark", ct => EmbarkAsync(screen, embark!, multiplayer, ct), unpredictable: true);
    }

    /// <summary>Character id the local player has picked in the lobby (null when none / random unresolved).</summary>
    private static string? LocalCharacterId(NCharacterSelectScreen screen)
    {
        try
        {
            var lobby = screen.Lobby;
            if (lobby == null) return null;
            CharacterModel? c = lobby.LocalPlayer.character;
            var id = c == null ? "" : ModelText.SafeId(c);
            return id.Length > 0 ? id : null;
        }
        catch (Exception) { return null; }
    }

    private static void FillLobby(MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby lobby, CharacterSelectInfo info, GameSnapshot snap)
    {
        try
        {
            ulong me = lobby.NetService?.NetId ?? 0;
            PlatformType? platform = null;
            try { platform = lobby.NetService?.Platform; }
            catch (Exception) { }
            // Lobby player structs differ by name between game versions (LobbyPlayer / StartRunLobbyPlayer) but share fields.
            foreach (var p in lobby.Players)
            {
                if (p.id == me) continue;
                string name = p.id.ToString();
                try
                {
                    if (platform is { } pt)
                    {
                        var n = PlatformUtil.GetPlayerName(pt, p.id);
                        if (!string.IsNullOrEmpty(n)) name = n;
                    }
                }
                catch (Exception) { }
                if (info.LobbyPicks.ContainsKey(name)) name += "#" + p.slotId;
                var charId = p.character == null ? "" : ModelText.SafeId(p.character);
                info.LobbyPicks[name] = charId.Length > 0 ? charId : "-";
                var charName = p.character == null ? "-" : ModelText.Loc(p.character.Title);
                snap.Notes.Add(R2Text.T("note.char_lobby", name, charName, p.isReady ? R2Text.T("note.char_ready_tag") : ""));
            }
        }
        catch (Exception ex) { ThrottledLog.Warn("charselect:lobby", "lobby unreadable: " + ex.Message); }
    }

    private static NClickableControl? EmbarkButton(NCharacterSelectScreen screen) =>
        RoomUtil.Node<NConfirmButton>(screen, "_embarkButton")
        ?? SceneUtil.Get<NConfirmButton>(screen, "ConfirmButton")
        ?? RoomUtil.FirstUsable(SceneUtil.Descendants<NConfirmButton>(screen));

    private static async Task<ActionResult> SelectCharacterAsync(NCharacterSelectScreen screen, NCharacterSelectButton button, string id, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen) || !SceneUtil.Alive(button)) return ActionResult.Fail("character select is gone");
        if (button.IsLocked) return ActionResult.Fail(id + " is locked");
        if (!SceneUtil.Usable(button)) return ActionResult.Fail(id + " cannot be selected now");
        bool Selected() => LocalCharacterId(screen) == id || (screen.Lobby == null && button.IsSelected);
        try { button.Select(); }
        catch (Exception ex) { return ActionResult.Fail("select " + id + ": " + ex.Message); }
        var r = await UiActions.Expect(Selected, "select " + id, TimeSpan.FromSeconds(2), ct);
        if (!r.Success && SceneUtil.Alive(screen) && SceneUtil.Alive(button))
        {
            // Unverified assumption: Select() goes through the screen's button delegate. If the lobby did not change,
            // call that delegate entry point (what a click on the button ends up in) directly.
            try { screen.SelectCharacter(button, button.Character); }
            catch (Exception ex) { return ActionResult.Fail("select " + id + ": " + ex.Message); }
            r = await UiActions.Expect(Selected, "select " + id, RoomUtil.ShortWait, ct);
        }
        await MainThread.Frames(2);
        return r;
    }

    private static async Task<ActionResult> SetAscensionAsync(NCharacterSelectScreen screen, int level, CancellationToken ct)
    {
        var lobby = SceneUtil.Alive(screen) ? screen.Lobby : null;
        if (lobby == null) return ActionResult.Fail("no lobby");
        if (level < 0 || level > lobby.MaxAscension) return ActionResult.Fail($"ascension {level} is not unlocked");
        if (lobby.NetService?.Type == NetGameType.Client) return ActionResult.Fail("only the host sets the ascension");
        if (lobby.LocalPlayer.isReady) return ActionResult.Fail("already ready");
        // The lobby call the ascension panel's arrows end up in (it also syncs the level to co-op peers).
        try { lobby.SyncAscensionChange(level); }
        catch (Exception ex) { return ActionResult.Fail("set ascension: " + ex.Message); }
        var r = await UiActions.Expect(() => lobby.Ascension == level, "set ascension", RoomUtil.ShortWait, ct);
        await MainThread.Frames(2);
        return r;
    }

    private static async Task<ActionResult> EmbarkAsync(NCharacterSelectScreen screen, NClickableControl embark, bool multiplayer, CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("character select is gone");
        if (LocalCharacterId(screen) == null) return ActionResult.Fail("no character selected");
        // Embark is a heavy transition; first runs may ask about tutorials (a popup) before starting.
        return await RoomUtil.PressAndWait(embark, "embark", () =>
        {
            if (RoomUtil.ModalOpen() || !ReferenceEquals(RoomUtil.CurrentScreen(), screen)) return true;
            if (RunManager.Instance is { IsInProgress: true }) return true;
            try { return multiplayer && screen.Lobby != null && screen.Lobby.LocalPlayer.isReady; }
            catch (Exception) { return false; }
        }, TimeSpan.FromSeconds(20), ct);
    }

    // ================================================================ game over

    /// <summary>Game over screens whose continue button was already pressed (pressing it again restarts the summary).</summary>
    private static readonly HashSet<ulong> Continued = new();

    public static void ReadGameOver(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        if (probe.Screen is not NGameOverScreen screen || !SceneUtil.Alive(screen)) return;

        var run = ctx.Run ?? Reflect.Get<RunState>(screen, "_runState");
        var info = new GameOverInfo();
        try
        {
            var history = RunManager.Instance?.History ?? Reflect.Get<RunHistory>(screen, "_history");
            info.Victory = history?.Win ?? run?.CurrentRoom?.IsVictoryRoom ?? false;
        }
        catch (Exception) { }
        try { info.FloorReached = run?.TotalFloor; }
        catch (Exception) { }
        int score = Reflect.Get<int>(screen, "_score");
        if (score > 0) info.Score = score;
        snap.GameOver = info;

        var waiting = SceneUtil.Get<Control>(screen, "%WaitingForOtherPlayers")
                      ?? SceneUtil.Descendants<Control>(screen, 8).FirstOrDefault(c => c.Name.ToString().Contains("WaitingForOtherPlayers"));
        if (SceneUtil.VisibleInTree(waiting)) snap.Notes.Add(R2Text.T("note.gameover_wait"));

        var cont = RoomUtil.Node<NClickableControl>(screen, "_continueButton") ?? SceneUtil.Get<NClickableControl>(screen, "%ContinueButton");
        var menu = RoomUtil.Node<NClickableControl>(screen, "_mainMenuButton") ?? SceneUtil.Get<NClickableControl>(screen, "%MainMenuButton");
        bool animating = Reflect.Get<bool>(screen, "_isAnimatingSummary");
        ulong id = screen.GetInstanceId();
        bool continued;
        lock (Continued) continued = Continued.Contains(id);

        // Continue opens the run summary; it must be pressed exactly once (a second press restarts the summary
        // animation and the main-menu button never becomes available).
        if (!continued && !animating && SceneUtil.Usable(cont) && !SceneUtil.VisibleInTree(menu))
            sink.Add(ActionKind.Proceed, R2Text.T("act.gameover_continue"), "gameover:continue", ct => ContinueAsync(screen, cont!, menu, ct), unpredictable: true);
        else if (continued && !SceneUtil.Usable(menu)) snap.Notes.Add(R2Text.T("note.gameover_continued"));

        // Only once the game itself enables it (the summary saves progress / unlocks first).
        if (SceneUtil.Usable(menu))
            sink.Add(ActionKind.ReturnToMenu, R2Text.T("act.return_menu"), "gameover:menu",
                ct => RoomUtil.PressAndWait(menu, "return to main menu", () => RoomUtil.CurrentScreen() is not NGameOverScreen,
                    TimeSpan.FromSeconds(30), ct),
                unpredictable: true);
    }

    private static async Task<ActionResult> ContinueAsync(NGameOverScreen screen, NClickableControl cont, NClickableControl? menu, CancellationToken ct)
    {
        ulong id = screen.GetInstanceId();
        lock (Continued)
        {
            if (Continued.Contains(id)) return ActionResult.Fail("continue was already pressed");
            if (Continued.Count > 32) Continued.Clear();
        }
        var pressed = UiActions.Press(cont, "game over continue");
        if (!pressed.Success) return pressed;
        lock (Continued) Continued.Add(id); // never again for this screen, whatever happens next
        var r = await UiActions.Expect(() =>
                !SceneUtil.Alive(screen) || Reflect.Get<bool>(screen, "_isAnimatingSummary") || SceneUtil.VisibleInTree(menu) ||
                !SceneUtil.VisibleInTree(cont) || RoomUtil.CurrentScreen() is not NGameOverScreen,
            "game over continue", UiActions.DefaultTimeout, ct);
        await MainThread.Frames(2);
        return r;
    }

    // ================================================================ helpers

    private static T? Safe<T>(Func<T?> get) where T : class
    {
        try { return get(); }
        catch (Exception) { return null; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch (Exception) { return false; }
    }
}
