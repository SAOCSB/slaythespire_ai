using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms2;

/// <summary>Small helpers shared by the shop / event / rest site / menu / popup readers. Main thread only.</summary>
internal static class RoomUtil
{
    /// <summary>How long an executor waits for the effect of a click.</summary>
    public static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    /// <summary>The active screen context, or null while the screen tree is in flux.</summary>
    public static IScreenContext? CurrentScreen()
    {
        try { return ActiveScreenContext.Instance?.GetCurrentScreen(); }
        catch (Exception) { return null; }
    }

    /// <summary>The open modal (popup / tutorial), when it is a live node.</summary>
    public static Node? OpenModal()
    {
        try { return NModalContainer.Instance?.OpenModal is Node n && SceneUtil.Alive(n) ? n : null; }
        catch (Exception) { return null; }
    }

    public static bool ModalOpen() => OpenModal() != null;

    /// <summary>True when a selection overlay (card grid, choose a card / relic / bundle) is the current screen.</summary>
    public static bool SelectionOpen() =>
        CurrentScreen() is NCardGridSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen or
            NChooseARelicSelection or NCardRewardSelectionScreen;

    /// <summary>Reads a (possibly private) node field and returns it only when it is a live node.</summary>
    public static T? Node<T>(object? owner, string member) where T : Node
    {
        var n = Reflect.Get<T>(owner, member);
        return SceneUtil.Alive(n) ? n : null;
    }

    /// <summary>
    /// Like <see cref="Node{T}"/> for fields only some subclasses declare (probing many popup / tutorial types): a
    /// missing field is not reported to the diagnostics' missing-members list.
    /// </summary>
    public static T? OptionalNode<T>(object? owner, string member) where T : Node =>
        Reflect.TryGetQuiet<T>(owner, member, out var n) && SceneUtil.Alive(n) ? n : null;

    /// <summary>First usable (visible + enabled) control among the candidates.</summary>
    public static T? FirstUsable<T>(IEnumerable<T?> candidates) where T : NClickableControl =>
        candidates.FirstOrDefault(c => c != null && SceneUtil.Usable(c));

    /// <summary>Visible text of a button (its first non-empty label), "" when none.</summary>
    public static string ButtonText(Node? button)
    {
        if (!SceneUtil.Alive(button)) return "";
        foreach (var text in Texts(button, 4, skipButtons: false))
            if (text.Length > 0) return text;
        return "";
    }

    /// <summary>
    /// Visible label texts under <paramref name="root"/> in tree order, cleaned of markup and de-duplicated.
    /// With <paramref name="skipButtons"/> the captions of buttons are left out (they become action labels).
    /// </summary>
    public static List<string> Texts(Node? root, int max, bool skipButtons)
    {
        var result = new List<string>();
        if (!SceneUtil.Alive(root)) return result;
        var stack = new Stack<(Node node, int depth)>();
        stack.Push((root!, 0));
        while (stack.Count > 0 && result.Count < max)
        {
            var (node, depth) = stack.Pop();
            if (!ReferenceEquals(node, root))
            {
                if (node is CanvasItem ci && !ci.Visible) continue;
                if (skipButtons && node is NClickableControl) continue;
                string? raw = node switch
                {
                    Label l => l.Text,
                    RichTextLabel r => r.Text,
                    _ => null,
                };
                var text = ModelText.Clean(raw);
                if (text.Length > 0 && !result.Contains(text)) result.Add(text);
            }
            if (depth >= 24) continue;
            int count;
            try { count = node.GetChildCount(); }
            catch (ObjectDisposedException) { continue; }
            for (int i = count - 1; i >= 0; i--)
            {
                Node? child;
                try { child = node.GetChild(i); }
                catch (ObjectDisposedException) { continue; }
                if (SceneUtil.Alive(child)) stack.Push((child!, depth + 1));
            }
        }
        return result;
    }

    /// <summary>Waits (polling once per frame on the main thread) until a game task finished.</summary>
    public static Task<bool> WaitTask(Task task, TimeSpan timeout, CancellationToken ct) =>
        MainThread.WaitUntil(() => task.IsCompleted, timeout, ct);

    /// <summary>Result of a finished game task: true/false, or null while it still runs or when it faulted.</summary>
    public static bool? Outcome(Task<bool> task) =>
        // Only read Result once the task completed successfully, so this never blocks.
        task.IsCompletedSuccessfully ? task.Result : null;

    /// <summary>Logs a fault of a game task we do not await to the end (fire-and-forget flows).</summary>
    public static void Observe(Task task, string what)
    {
        task.ContinueWith(t => ModLog.Warn($"{what} failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Settles after an action: a selection overlay or popup that the action opened is waiting for input, so only a
    /// few frames are waited then (the action that opened it may stay "running" until the choice is made).
    /// </summary>
    public static async Task Settle(CancellationToken ct)
    {
        await MainThread.Frames(2);
        if (SelectionOpen() || ModalOpen())
        {
            await MainThread.Frames(3);
            return;
        }
        await UiActions.WaitSettled(ct: ct);
    }

    /// <summary>Presses a button, then waits until <paramref name="done"/> holds and the game settled.</summary>
    public static async Task<ActionResult> PressAndWait(NClickableControl? button, string what, Func<bool> done,
        TimeSpan? timeout, CancellationToken ct)
    {
        var pressed = UiActions.Press(button, what);
        if (!pressed.Success) return pressed;
        var r = await UiActions.Expect(done, what, timeout ?? UiActions.DefaultTimeout, ct);
        await Settle(ct);
        return r;
    }

    /// <summary>Display name of a player: the snapshot's player list first (filled by the player reader), then the platform.</summary>
    public static string PlayerName(GameSnapshot snap, ulong netId)
    {
        var p = snap.Players.FirstOrDefault(x => x.NetId == netId);
        if (p != null && !string.IsNullOrEmpty(p.Name)) return p.Name;
        try
        {
            var net = RunManager.Instance?.NetService;
            if (net != null)
            {
                var name = PlatformUtil.GetPlayerName(net.Platform, netId);
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception) { /* platform not ready */ }
        return netId.ToString();
    }

    /// <summary>Players of the run in slot order (empty when unreadable).</summary>
    public static List<Player> PlayersInSlotOrder(RunState? run)
    {
        if (run == null) return new List<Player>();
        try { return run.Players.OrderBy(p => run.GetPlayerSlotIndex(p)).ToList(); }
        catch (Exception) { return new List<Player>(); }
    }

    /// <summary>Snapshot key of a player ("p" + slot index).</summary>
    public static string PlayerKey(RunState? run, Player player)
    {
        try { return run == null ? "p?" : "p" + run.GetPlayerSlotIndex(player); }
        catch (Exception) { return "p?"; }
    }
}
