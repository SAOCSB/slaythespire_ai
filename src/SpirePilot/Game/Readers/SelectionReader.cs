using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>Card grid selections (upgrade/remove/transform/enchant/choose from pile) and choose-one screens (choose a card / relic / bundle), in and out of combat. Called for CardGridSelect and ChooseOne.</summary>
internal static class SelectionReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        switch (probe.Screen)
        {
            case NCardGridSelectionScreen grid when SceneUtil.Alive(grid):
                CardGridSelection.Read(ctx, grid, snap, sink);
                break;
            case NChooseACardSelectionScreen choose when SceneUtil.Alive(choose):
                ChooseScreens.ReadChooseCard(ctx, choose, snap, sink);
                break;
            case NChooseABundleSelectionScreen bundles when SceneUtil.Alive(bundles):
                ChooseScreens.ReadBundles(ctx, bundles, snap, sink);
                break;
            case NChooseARelicSelection relics when SceneUtil.Alive(relics):
                ChooseScreens.ReadRelics(ctx, relics, snap, sink);
                break;
            case Node other when SceneUtil.Alive(other) && probe.Kind == ScreenKind.CardGridSelect:
                // A screen type we do not know that shows grid card holders (the resolver's generic fallback).
                CardGridSelection.ReadGeneric(ctx, other, snap, sink);
                snap.Notes.Add("Generic card grid: " + probe.Name);
                break;
        }
    }
}
