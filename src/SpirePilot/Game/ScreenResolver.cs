using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.FeedbackScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game;

/// <summary>What is on screen right now, as found by <see cref="ScreenResolver"/>.</summary>
internal sealed class ScreenProbe
{
    public ScreenKind Kind { get; init; }
    /// <summary>The active screen context (room, overlay, menu, ...) as reported by the game.</summary>
    public IScreenContext? Screen { get; init; }
    /// <summary>The open modal (popup / tutorial) node, when <see cref="Kind"/> is <see cref="ScreenKind.Popup"/>.</summary>
    public Node? Modal { get; init; }
    /// <summary>For a modal: the screen underneath it.</summary>
    public IScreenContext? Underlying { get; init; }
    /// <summary>Type name of the screen, for logs and the snapshot.</summary>
    public string Name { get; init; } = "";
    /// <summary>Set when a human-only screen is open (pause menu, settings, card library ...): the AI must wait.</summary>
    public string? HumanOnlyReason { get; init; }
    /// <summary>
    /// <see cref="ScreenKind.Combat"/> only: the combat room is the current screen but no fight is running (the room
    /// left behind after a fight, with its own proceed button). Judged like any other room, not like a combat turn.
    /// </summary>
    public bool CombatFinished { get; init; }

    public T? As<T>() where T : class => Screen as T;
}

/// <summary>
/// Maps the game's active screen context to a <see cref="ScreenKind"/>. Order matters (see the comments): modals win,
/// game over beats the combat room it leaves behind, overlays beat the room below them.
/// </summary>
internal static class ScreenResolver
{
    public static ScreenProbe Resolve(GameContext ctx)
    {
        IScreenContext? current = null;
        try { current = ActiveScreenContext.Instance?.GetCurrentScreen(); }
        catch (Exception) { /* screen tree in flux */ }

        // 1) A modal (confirmation popup, tutorial) blocks everything below it.
        IScreenContext? modal = null;
        try { modal = NModalContainer.Instance?.OpenModal; }
        catch (Exception) { }
        if (modal is Node modalNode && SceneUtil.Alive(modalNode))
        {
            // GetCurrentScreen() returns the modal itself (only the feedback screen beats it), so the screen under the
            // popup comes from the capstone container (pause menu, deck / pile viewers, teammate view) and the inspect
            // screens. A popup over one of those was opened by the human: theirs to answer.
            bool overHumanMenu = HumanMenuUnderModal(out var under);
            bool currentIsModal = ReferenceEquals(current, modal);
            if (!currentIsModal && IsHumanOnly(current))
            {
                overHumanMenu = true;
                under ??= current;
            }
            return new ScreenProbe
            {
                Kind = ScreenKind.Popup, Screen = modal, Modal = modalNode, Name = modalNode.GetType().Name,
                Underlying = under ?? (currentIsModal ? null : current),
                HumanOnlyReason = overHumanMenu ? "menu open" : null,
            };
        }

        if (current == null)
            return new ScreenProbe { Kind = ScreenKind.Loading, Name = "none" };
        var name = current.GetType().Name;
        ScreenProbe Make(ScreenKind kind, string? humanOnly = null) => new() { Kind = kind, Screen = current, Name = name, HumanOnlyReason = humanOnly };

        // 2) Human-only menus and viewers pushed on top of a run (pause, settings, compendium, deck / pile views, the
        //    teammate view ...). Every capstone screen was opened by the human, also in combat.
        if (current is NCapstoneSubmenuStack) return Make(ScreenKind.Unknown, "menu open");
        if (current is ICapstoneScreen || IsHumanOnly(current)) return Make(ScreenKind.Unknown, "viewer open");

        // 3) Unlock screens after a run behave like popups (one confirm button).
        if (current is NUnlockScreen) return Make(ScreenKind.Popup);

        // 4) Menus and the timeline. Their pages hold card holders too (unlocked cards inside the timeline, run
        //    history, compendium), so they are routed before the generic card-grid check below.
        switch (current)
        {
            case NCharacterSelectScreen: return Make(ScreenKind.CharacterSelect);
            case NMultiplayerLoadGameScreen: return Make(ScreenKind.Unknown, "multiplayer load screen");
            case NTimelineScreen or NEpochInspectScreen or NTimelineTutorial: return Make(ScreenKind.MainMenu);
            case NEarlyAccessDisclaimer: return Make(ScreenKind.Popup);
            // A submenu during a run is part of the pause menu: the human's.
            case NSubmenu when ctx.InRun: return Make(ScreenKind.Unknown, "menu open");
            case NMainMenu or NSubmenu or NLogoAnimation: return Make(ScreenKind.MainMenu);
        }

        // 5) In-combat "choose cards from your hand".
        if (ctx.Combat != null && IsHandSelection()) return Make(ScreenKind.HandSelect);

        // 6) Overlays and selection screens (they carry card holders, so test them before the generic grid check).
        switch (current)
        {
            case NCardRewardSelectionScreen: return Make(ScreenKind.CardReward);
            case NChooseACardSelectionScreen or NChooseARelicSelection or NChooseABundleSelectionScreen: return Make(ScreenKind.ChooseOne);
            case NCardGridSelectionScreen: return Make(ScreenKind.CardGridSelect);
            // Death leaves the combat room active underneath, so game over must win over combat.
            case NGameOverScreen: return Make(ScreenKind.GameOver);
            case NRewardsScreen: return Make(ScreenKind.CombatRewards);
        }
        // 7) The combat room, before the generic card-grid walk below (it would visit every node of the room on every
        //    snapshot of a fight). A room whose fight is over is still Combat (its reader offers the room's proceed
        //    button), flagged so the readiness gate judges it like any other room.
        if (current is NCombatRoom)
        {
            bool running;
            try { running = CombatManager.Instance is { IsInProgress: true }; }
            catch (Exception) { running = false; }
            return running
                ? Make(ScreenKind.Combat)
                : new ScreenProbe { Kind = ScreenKind.Combat, Screen = current, Name = name, CombatFinished = true };
        }

        // Card selections only exist in a run.
        if (ctx.InRun && current is Node n && SceneUtil.Descendants<NGridCardHolder>(n, 12).Any(h => SceneUtil.VisibleInTree(h)))
            return Make(ScreenKind.CardGridSelect);

        // 8) Rooms and full screens.
        if (FindActiveCombatRoom(current) != null) return Make(ScreenKind.Combat);
        switch (current)
        {
            case NTreasureRoom or NTreasureRoomRelicCollection: return Make(ScreenKind.Treasure);
            case NRestSiteRoom: return Make(ScreenKind.RestSite);
            case NMerchantRoom or NMerchantInventory or NFakeMerchant: return Make(ScreenKind.Shop);
            case NCrystalSphereScreen: return Make(ScreenKind.Event);
            case NEventRoom: return Make(ScreenKind.Event);
            case NMapScreen or NMapRoom: return Make(ScreenKind.Map);
        }
        return Make(ScreenKind.Unknown);
    }

    private static bool IsHumanOnly(IScreenContext? screen) =>
        screen is NCapstoneSubmenuStack or ICapstoneScreen or NCardsViewScreen or NCardLibrary or NCardPileScreen or NInspectCardScreen or
            NInspectRelicScreen or NSendFeedbackScreen or NPatchNotesScreen;

    /// <summary>
    /// True when a human menu / viewer is open under the current modal: the capstone container is in use (pause menu,
    /// deck or pile view, teammate view) or an inspect screen is showing. <paramref name="under"/> is that screen.
    /// </summary>
    private static bool HumanMenuUnderModal(out IScreenContext? under)
    {
        under = null;
        try
        {
            var capstone = NCapstoneContainer.Instance;
            if (SceneUtil.Valid(capstone) && capstone.InUse)
            {
                under = capstone.CurrentCapstoneScreen;
                return true;
            }
        }
        catch (Exception) { /* container not ready */ }
        try
        {
            var game = NGame.Instance;
            if (!SceneUtil.Valid(game)) return false;
            var card = game.InspectCardScreen;
            if (SceneUtil.VisibleInTree(card))
            {
                under = card;
                return true;
            }
            var relic = game.InspectRelicScreen;
            if (SceneUtil.VisibleInTree(relic))
            {
                under = relic;
                return true;
            }
            // The feedback screen needs no check: GetCurrentScreen() returns it even over a modal (IsHumanOnly).
        }
        catch (Exception) { /* screens not created yet */ }
        return false;
    }

    /// <summary>True while the hand is in a select mode (discard / exhaust / upgrade from hand ...).</summary>
    public static bool IsHandSelection()
    {
        try
        {
            var hand = NPlayerHand.Instance;
            return SceneUtil.Alive(hand) && (hand.IsInCardSelection ||
                                            hand.CurrentMode is NPlayerHand.Mode.SimpleSelect or NPlayerHand.Mode.UpgradeSelect);
        }
        catch (Exception) { return false; }
    }

    /// <summary>The combat room node while a fight is actually running (not the finished room left behind).</summary>
    public static NCombatRoom? FindActiveCombatRoom(IScreenContext? current)
    {
        try
        {
            if (current is NCombatRoom r && SceneUtil.Alive(r) && r.Mode == CombatRoomMode.ActiveCombat) return r;
            if (CombatManager.Instance is { IsInProgress: true })
            {
                var room = NCombatRoom.Instance;
                if (SceneUtil.VisibleInTree(room) && room.Mode == CombatRoomMode.ActiveCombat && current is not NMapScreen) return room;
            }
        }
        catch (Exception) { }
        return null;
    }
}
