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
            return new ScreenProbe { Kind = ScreenKind.Popup, Screen = modal, Modal = modalNode, Underlying = current, Name = modalNode.GetType().Name };

        if (current == null)
            return new ScreenProbe { Kind = ScreenKind.Loading, Name = "none" };
        var name = current.GetType().Name;
        ScreenProbe Make(ScreenKind kind, string? humanOnly = null) => new() { Kind = kind, Screen = current, Name = name, HumanOnlyReason = humanOnly };

        // 2) Human-only menus pushed on top of a run (pause, settings, compendium...).
        if (current is NCapstoneSubmenuStack) return Make(ScreenKind.Unknown, "menu open");
        if (current is NCardsViewScreen or NCardLibrary or NCardPileScreen or NInspectCardScreen or NInspectRelicScreen or
            NSendFeedbackScreen or NPatchNotesScreen)
            return Make(ScreenKind.Unknown, "viewer open");

        // 3) Unlock screens after a run behave like popups (one confirm button).
        if (current is NUnlockScreen) return Make(ScreenKind.Popup);

        // 4) In-combat "choose cards from your hand".
        if (ctx.Combat != null && IsHandSelection()) return Make(ScreenKind.HandSelect);

        // 5) Overlays and selection screens (they carry card holders, so test them before the generic grid check).
        switch (current)
        {
            case NCardRewardSelectionScreen: return Make(ScreenKind.CardReward);
            case NChooseACardSelectionScreen or NChooseARelicSelection or NChooseABundleSelectionScreen: return Make(ScreenKind.ChooseOne);
            case NCardGridSelectionScreen: return Make(ScreenKind.CardGridSelect);
            // Death leaves the combat room active underneath, so game over must win over combat.
            case NGameOverScreen: return Make(ScreenKind.GameOver);
            case NRewardsScreen: return Make(ScreenKind.CombatRewards);
        }
        if (current is Node n && SceneUtil.Descendants<NGridCardHolder>(n, 12).Any(h => SceneUtil.VisibleInTree(h)))
            return Make(ScreenKind.CardGridSelect);

        // 6) Rooms and full screens.
        if (FindActiveCombatRoom(current) != null) return Make(ScreenKind.Combat);
        switch (current)
        {
            case NTreasureRoom or NTreasureRoomRelicCollection: return Make(ScreenKind.Treasure);
            case NRestSiteRoom: return Make(ScreenKind.RestSite);
            case NMerchantRoom or NMerchantInventory or NFakeMerchant: return Make(ScreenKind.Shop);
            case NCrystalSphereScreen: return Make(ScreenKind.Event);
            case NEventRoom: return Make(ScreenKind.Event);
            case NMapScreen or NMapRoom: return Make(ScreenKind.Map);
            case NCombatRoom: return Make(ScreenKind.Combat);
            case NCharacterSelectScreen: return Make(ScreenKind.CharacterSelect);
            case NMultiplayerLoadGameScreen: return Make(ScreenKind.Unknown, "multiplayer load screen");
            case NTimelineScreen or NEpochInspectScreen or NTimelineTutorial: return Make(ScreenKind.MainMenu);
            case NEarlyAccessDisclaimer: return Make(ScreenKind.Popup);
            case NMainMenu or NSubmenu or NLogoAnimation: return Make(ScreenKind.MainMenu);
        }
        return Make(ScreenKind.Unknown);
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
