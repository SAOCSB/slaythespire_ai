using Godot;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game;

/// <summary>
/// Helpers shared by the executors: pressing the game's own buttons, clicking card holders, and waiting for the
/// game to settle afterwards. All methods run on the main thread.
/// </summary>
internal static class UiActions
{
    /// <summary>Default time an executor waits for its effect to show up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Presses a game button the way the game does on mouse release (<see cref="NClickableControl.ForceClick"/>).
    /// ForceClick skips the button's own enabled check, so it is checked here first.
    /// </summary>
    public static ActionResult Press(NClickableControl? button, string what)
    {
        if (!SceneUtil.Valid(button)) return ActionResult.Fail(what + ": button not found");
        if (!SceneUtil.VisibleInTree(button)) return ActionResult.Fail(what + ": button not visible");
        if (!button!.IsEnabled) return ActionResult.Fail(what + ": button disabled");
        try
        {
            button.ForceClick();
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(what + ": " + ex.Message);
        }
    }

    /// <summary>Card holders are not buttons: they are clicked by emitting their Pressed signal.</summary>
    public static ActionResult ClickHolder(NCardHolder? holder, string what)
    {
        if (!SceneUtil.Valid(holder) || !SceneUtil.Alive(holder)) return ActionResult.Fail(what + ": card not found");
        try
        {
            holder!.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(what + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Waits until <paramref name="done"/> holds (the action had its effect). Returns a failure naming
    /// <paramref name="what"/> on timeout. Use after pressing something whose result is observable.
    /// </summary>
    public static async Task<ActionResult> Expect(Func<bool> done, string what, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        bool ok = await MainThread.WaitUntil(done, timeout ?? DefaultTimeout, ct);
        return ok ? ActionResult.Ok() : ActionResult.Fail(what + ": no effect (timed out)");
    }

    /// <summary>True while the game is still resolving actions or transitioning between screens.</summary>
    public static bool GameBusy()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm?.ActionExecutor is { IsRunning: true }) return true;
            if (NGame.Instance?.Transition is { InTransition: true }) return true;
        }
        catch (Exception) { return true; }
        return false;
    }

    /// <summary>Waits for the action queue to drain and transitions to finish (then a couple of frames for UI to update).</summary>
    public static async Task WaitSettled(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await MainThread.Frames(2);
        await MainThread.WaitUntil(() => !GameBusy(), timeout ?? DefaultTimeout, ct);
        await MainThread.Frames(2);
    }

    /// <summary>Common pattern: press, then wait for an observable effect, then settle.</summary>
    public static async Task<ActionResult> PressAndExpect(NClickableControl? button, string what, Func<bool> done, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var pressed = Press(button, what);
        if (!pressed.Success) return pressed;
        var r = await Expect(done, what, timeout, ct);
        await WaitSettled(ct: ct);
        return r;
    }
}
