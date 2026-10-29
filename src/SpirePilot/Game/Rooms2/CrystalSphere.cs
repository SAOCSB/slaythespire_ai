using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms2;

/// <summary>
/// The Crystal Sphere event minigame (an overlay of the event room): pick the divination tool, divine hidden cells,
/// proceed when done. Everything is done with the screen's own buttons and cells (ForceClick), so the game runs its
/// normal click handlers. Hidden item identities are never read (the AI sees what a player sees).
/// </summary>
internal static class CrystalSphere
{
    /// <summary>Upper bound for cell actions in one menu (the grid can be large).</summary>
    private const int MaxCellActions = 80;

    public static void Read(GameContext ctx, NCrystalSphereScreen screen, GameSnapshot snap, ActionSink sink)
    {
        // The model lives in a private field; the UI cells carry the same cell objects.
        var game = Reflect.Get<CrystalSphereMinigame>(screen, "_entity");

        var cells = SceneUtil.Descendants<NCrystalSphereCell>(screen)
            .Where(c => SceneUtil.VisibleInTree(c) && c.Entity != null)
            .OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X)
            .ToList();
        var hidden = cells.Where(c => SafeHidden(c)).ToList();

        bool finished = false;
        int divinations = 0;
        string tool = "?";
        int width = 0, height = 0;
        if (game != null)
        {
            try
            {
                finished = game.IsFinished;
                divinations = game.DivinationCount;
                tool = ToolName(game.CrystalSphereTool);
                width = game.GridSize.X;
                height = game.GridSize.Y;
            }
            catch (Exception) { /* partial info */ }
        }
        snap.Notes.Add(R2Text.T("note.crystal", divinations, tool, hidden.Count, width, height));
        foreach (var text in RoomUtil.Texts(RoomUtil.Node<Godot.Node>(screen, "_instructionsContainer"), 2, skipButtons: true))
            snap.Notes.Add(text);

        if (!finished && divinations > 0)
        {
            var big = RoomUtil.Node<NDivinationButton>(screen, "_bigDivinationButton");
            var small = RoomUtil.Node<NDivinationButton>(screen, "_smallDivinationButton");
            var current = game?.CrystalSphereTool;
            if (current != CrystalSphereMinigame.CrystalSphereToolType.Big && SceneUtil.Usable(big))
                sink.Add(ActionKind.ChooseOption, R2Text.T("act.crystal_tool", R2Text.T("word.tool_big")), "crystal:tool:big",
                    ct => SetToolAsync(game, big!, CrystalSphereMinigame.CrystalSphereToolType.Big, ct));
            if (current != CrystalSphereMinigame.CrystalSphereToolType.Small && SceneUtil.Usable(small))
                sink.Add(ActionKind.ChooseOption, R2Text.T("act.crystal_tool", R2Text.T("word.tool_small")), "crystal:tool:small",
                    ct => SetToolAsync(game, small!, CrystalSphereMinigame.CrystalSphereToolType.Small, ct));

            foreach (var cell in hidden.Take(MaxCellActions))
            {
                int x = cell.Entity.X, y = cell.Entity.Y;
                var captured = cell;
                sink.Add(ActionKind.ChooseOption, R2Text.T("act.crystal_cell", x, y), $"crystal:cell:{x},{y}",
                    ct => DivineAsync(game, captured, ct),
                    new Dictionary<string, string> { ["x"] = x.ToString(), ["y"] = y.ToString() }, unpredictable: true);
            }
        }

        var proceed = RoomUtil.Node<NProceedButton>(screen, "_proceedButton")
                      ?? SceneUtil.Descendants<NProceedButton>(screen).FirstOrDefault(b => SceneUtil.VisibleInTree(b));
        if (SceneUtil.Usable(proceed))
            sink.Add(ActionKind.Proceed, R2Text.T("act.proceed"), "proceed",
                ct => RoomUtil.PressAndWait(proceed, "crystal sphere proceed",
                    () => !SceneUtil.Alive(screen) || !screen.IsVisibleInTree() || !ReferenceEquals(RoomUtil.CurrentScreen(), screen),
                    UiActions.DefaultTimeout, ct),
                unpredictable: true);
    }

    private static bool SafeHidden(NCrystalSphereCell cell)
    {
        try { return cell.Entity.IsHidden; }
        catch (Exception) { return false; }
    }

    private static string ToolName(CrystalSphereMinigame.CrystalSphereToolType tool) => tool switch
    {
        CrystalSphereMinigame.CrystalSphereToolType.Big => R2Text.T("word.tool_big"),
        CrystalSphereMinigame.CrystalSphereToolType.Small => R2Text.T("word.tool_small"),
        _ => "-",
    };

    private static async Task<ActionResult> SetToolAsync(CrystalSphereMinigame? game, NDivinationButton button,
        CrystalSphereMinigame.CrystalSphereToolType tool, CancellationToken ct)
    {
        var pressed = UiActions.Press(button, "divination tool");
        if (!pressed.Success) return pressed;
        if (game == null)
        {
            await MainThread.Frames(2);
            return ActionResult.Ok();
        }
        return await UiActions.Expect(() => game.CrystalSphereTool == tool, "divination tool", RoomUtil.ShortWait, ct);
    }

    private static async Task<ActionResult> DivineAsync(CrystalSphereMinigame? game, NCrystalSphereCell cell, CancellationToken ct)
    {
        if (!SceneUtil.Alive(cell) || !SafeHidden(cell)) return ActionResult.Fail("cell is already revealed");
        int before = game?.DivinationCount ?? -1;
        var pressed = UiActions.Press(cell, "divine cell");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => !SceneUtil.Alive(cell) || !SafeHidden(cell) || (game != null && game.DivinationCount != before),
            "divine cell", UiActions.DefaultTimeout, ct);
        await RoomUtil.Settle(ct);
        return r;
    }
}
