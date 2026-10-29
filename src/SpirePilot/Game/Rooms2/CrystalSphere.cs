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
        // The model lives in a private plain-C# field (not verifiable on every game version); everything below also
        // works from the UI alone: the cells carry their public cell entities, the buttons and labels are nodes.
        var game = Reflect.Get<CrystalSphereMinigame>(screen, "_entity");

        var cells = SceneUtil.Descendants<NCrystalSphereCell>(screen)
            .Where(c => SceneUtil.VisibleInTree(c) && SafeEntity(c) != null)
            .OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X)
            .ToList();
        var hidden = cells.Where(c => SafeHidden(c)).ToList();

        var proceed = RoomUtil.Node<NProceedButton>(screen, "_proceedButton")
                      ?? SceneUtil.Descendants<NProceedButton>(screen).FirstOrDefault(b => SceneUtil.VisibleInTree(b));
        bool proceedUsable = SceneUtil.Usable(proceed);

        bool finished = false;
        int? divinations = null;
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
        else
        {
            divinations = LabelCount(RoomUtil.Node<Godot.Node>(screen, "_divinationsLeftLabel"));
            // Without the model the minigame counts as running until the game offers to proceed.
            finished = proceedUsable && hidden.Count == 0;
            foreach (var c in cells)
            {
                try
                {
                    width = Math.Max(width, c.Entity.X + 1);
                    height = Math.Max(height, c.Entity.Y + 1);
                }
                catch (Exception) { /* keep what we have */ }
            }
        }
        snap.Notes.Add(R2Text.T("note.crystal", divinations?.ToString() ?? "?", tool, hidden.Count, width, height));
        foreach (var text in RoomUtil.Texts(RoomUtil.Node<Godot.Node>(screen, "_instructionsContainer"), 2, skipButtons: true))
            snap.Notes.Add(text);

        // Unknown count (no model, unreadable label): divining is offered while the game does not offer to proceed.
        bool canDivine = !finished && (divinations is { } left ? left > 0 : !proceedUsable);
        if (canDivine)
        {
            var big = RoomUtil.Node<NDivinationButton>(screen, "_bigDivinationButton");
            var small = RoomUtil.Node<NDivinationButton>(screen, "_smallDivinationButton");
            CrystalSphereMinigame.CrystalSphereToolType? current = null;
            if (game != null)
            {
                try { current = game.CrystalSphereTool; }
                catch (Exception) { /* unknown */ }
            }
            else current = ActiveToolFromUi(big, small);
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
                    ct => DivineAsync(game, screen, captured, ct),
                    new Dictionary<string, string> { ["x"] = x.ToString(), ["y"] = y.ToString() }, unpredictable: true);
            }
        }

        if (proceedUsable)
            sink.Add(ActionKind.Proceed, R2Text.T("act.proceed"), "proceed",
                ct => RoomUtil.PressAndWait(proceed, "crystal sphere proceed",
                    () => !SceneUtil.Alive(screen) || !screen.IsVisibleInTree() || !ReferenceEquals(RoomUtil.CurrentScreen(), screen),
                    UiActions.DefaultTimeout, ct),
                unpredictable: true);
    }

    private static CrystalSphereCell? SafeEntity(NCrystalSphereCell cell)
    {
        try { return cell.Entity; }
        catch (Exception) { return null; }
    }

    /// <summary>First number in the "divinations left" label (null when the label is missing or has no number).</summary>
    private static int? LabelCount(Godot.Node? label)
    {
        string text = label switch
        {
            Godot.RichTextLabel r => r.Text,
            Godot.Label l => l.Text,
            _ => "",
        };
        var m = System.Text.RegularExpressions.Regex.Match(ModelText.Clean(text), @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    /// <summary>
    /// The active tool as the UI shows it (unverified: the active button's outline is lit), null when undecidable.
    /// Only used without the model; a wrong guess only costs a harmless extra tool switch.
    /// </summary>
    private static CrystalSphereMinigame.CrystalSphereToolType? ActiveToolFromUi(NDivinationButton? big, NDivinationButton? small)
    {
        bool? Lit(NDivinationButton? b)
        {
            if (!SceneUtil.Alive(b)) return null;
            var outline = RoomUtil.Node<Godot.CanvasItem>(b, "_outline");
            if (outline == null) return null;
            return outline.IsVisibleInTree() && outline.Modulate.A > 0.5f;
        }
        bool? bigLit = Lit(big), smallLit = Lit(small);
        if (bigLit == true && smallLit == false) return CrystalSphereMinigame.CrystalSphereToolType.Big;
        if (smallLit == true && bigLit == false) return CrystalSphereMinigame.CrystalSphereToolType.Small;
        return null;
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

    private static async Task<ActionResult> DivineAsync(CrystalSphereMinigame? game, NCrystalSphereScreen screen, NCrystalSphereCell cell,
        CancellationToken ct)
    {
        if (!SceneUtil.Alive(screen)) return ActionResult.Fail("the crystal sphere is gone");
        if (!SceneUtil.Alive(cell) || !SafeHidden(cell)) return ActionResult.Fail("cell is already revealed");
        int before = game?.DivinationCount ?? -1;
        var label = game == null ? RoomUtil.Node<Godot.Node>(screen, "_divinationsLeftLabel") : null;
        int? labelBefore = LabelCount(label);
        var pressed = UiActions.Press(cell, "divine cell");
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(() => !SceneUtil.Alive(cell) || !SafeHidden(cell) ||
                                             (game != null && game.DivinationCount != before) ||
                                             (labelBefore != null && LabelCount(label) != labelBefore),
            "divine cell", UiActions.DefaultTimeout, ct);
        await RoomUtil.Settle(ct);
        return r;
    }
}
