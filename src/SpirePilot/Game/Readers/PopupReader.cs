using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms2;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// Modals, confirmation popups, FTUE tutorials, unlock screens: describe and offer confirm/dismiss actions. Called for
/// ScreenKind.Popup. The popup's text goes into the snapshot notes. Rules: tutorials and info popups are simply
/// confirmed (multi-page tutorials page by page); confirmations of destructive actions (abandon run, disconnect,
/// quit, delete) only expose their cancel button; popups that are the human's decision (mod loading, settings,
/// multiplayer invites / warnings) expose nothing.
/// </summary>
internal static class PopupReader
{
    private static readonly string[] ConfirmNames = { "Yes", "Confirm", "Acknowledge", "Ok", "Okay", "Continue", "Accept", "Next", "RightArrow", "Proceed" };
    private static readonly string[] DismissNames = { "No", "Cancel", "Back", "Dismiss", "Close", "Ignore", "Skip" };
    private static readonly string[] NeverPress = { "Report", "Bug", "Feedback", "Discord", "Link", "Url", "Website" };
    private static readonly string[] ConfirmFields =
        { "_confirmButton", "_nextButton", "_acknowledgeButton", "_okButton", "_continueButton", "_advanceButton", "_proceedButton", "_closeButton" };

    /// <summary>Words that mark a confirmation of something that cannot be undone (checked on unknown popups).</summary>
    private static readonly string[] DestructiveWords =
        { "abandon", "give up", "quit", "delete", "disconnect", "erase", "reset", "放弃", "退出", "删除", "断开", "清除", "重置" };

    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var node = probe.Modal ?? probe.Screen as Node;
        if (!SceneUtil.Alive(node)) return;
        bool isModal = probe.Modal != null;

        var texts = RoomUtil.Texts(node, 10, skipButtons: true);
        if (texts.Count > 0) snap.Notes.Add(R2Text.T("note.popup", string.Join(" — ", texts)));
        if (isModal && probe.Underlying != null) snap.Notes.Add(R2Text.T("note.popup_over", probe.Underlying.GetType().Name));

        switch (node)
        {
            case NUnlockScreen unlock:
            {
                var confirm = UnlockConfirmButton(unlock);
                if (confirm != null) AddConfirm(sink, confirm, node!, isModal, ftue: false);
                else snap.Notes.Add(R2Text.T("note.popup_no_button"));
                return;
            }
            case NConfirmModLoadingPopup or NSettingsScreenPopup or NGenericPopup or NMultiplayerWarningPopup:
                // Mod loading / settings / co-op invites and warnings are the human's call.
                snap.Notes.Add(R2Text.T("note.popup_human"));
                return;
            case NAbandonRunConfirmPopup or NDisconnectConfirmPopup:
                AddCancelOnly(snap, sink, node!, isModal);
                return;
            case NErrorPopup:
            {
                // Informational (e.g. a connection error); both buttons just close it. Never the bug-report button.
                var popup = VerticalPopup(node!);
                if (Usable(popup?.YesButton)) AddConfirm(sink, popup!.YesButton, node!, isModal, ftue: false);
                if (Usable(popup?.NoButton)) AddDismiss(sink, popup!.NoButton, node!, isModal);
                if (sink.Count == 0)
                {
                    var (ok, cancel) = ButtonsByName(node!);
                    if (ok != null) AddConfirm(sink, ok, node!, isModal, ftue: false);
                    if (cancel != null) AddDismiss(sink, cancel, node!, isModal);
                }
                if (sink.Count == 0) snap.Notes.Add(R2Text.T("note.popup_no_button"));
                return;
            }
            case NAcceptTutorialsFtue:
            {
                // "Enable tutorials?": answering No is listed first (tutorials only interrupt an AI-played run).
                var popup = VerticalPopup(node!);
                if (Usable(popup?.NoButton)) AddDismiss(sink, popup!.NoButton, node!, isModal);
                if (Usable(popup?.YesButton)) AddConfirm(sink, popup!.YesButton, node!, isModal, ftue: false);
                if (sink.Count == 0) snap.Notes.Add(R2Text.T("note.popup_no_button"));
                return;
            }
            case NCombatRulesFtue rules:
            {
                // Multi-page: "next" turns the page and closes the tutorial on the last page.
                var next = RoomUtil.Node<NClickableControl>(rules, "_nextButton");
                if (Usable(next))
                {
                    var page = ModelText.Clean(RoomUtil.Node<Label>(rules, "_pageCount")?.Text);
                    AddConfirm(sink, next!, node!, isModal, ftue: true,
                        label: page.Length > 0 ? R2Text.T("act.popup_next", page) : null);
                }
                else if (FtueConfirmButton(rules) is { } other) AddConfirm(sink, other, node!, isModal, ftue: true);
                else snap.Notes.Add(R2Text.T("note.popup_no_button"));
                return;
            }
            case NFtue ftue:
            {
                var confirm = FtueConfirmButton(ftue);
                if (confirm != null) AddConfirm(sink, confirm, node!, isModal, ftue: true);
                else snap.Notes.Add(R2Text.T("note.popup_no_button"));
                return;
            }
        }

        // Generic yes/no popup (NVerticalPopup based: error popups, confirmations ...), else buttons by name.
        var vp = VerticalPopup(node!);
        NClickableControl? yes = Usable(vp?.YesButton) ? vp!.YesButton : null;
        NClickableControl? no = Usable(vp?.NoButton) ? vp!.NoButton : null;
        if (yes == null && no == null) (yes, no) = ButtonsByName(node!);

        bool destructive = texts.Any(t => DestructiveWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (destructive)
        {
            if (no != null)
            {
                snap.Notes.Add(R2Text.T("note.popup_destructive"));
                AddDismiss(sink, no, node!, isModal);
            }
            else snap.Notes.Add(R2Text.T("note.popup_human"));
            return;
        }
        if (yes != null) AddConfirm(sink, yes, node!, isModal, ftue: false);
        if (no != null) AddDismiss(sink, no, node!, isModal);

        if (sink.Count == 0 && node is NEarlyAccessDisclaimer disclaimer)
        {
            // Shown once at startup; closing it is its own public method.
            sink.Add(ActionKind.DismissPopup, R2Text.T("act.dismiss", R2Text.T("word.ok")), "popup:confirm", async ct =>
            {
                Task task;
                try { task = disclaimer.CloseScreen(); }
                catch (Exception ex) { return ActionResult.Fail("close disclaimer: " + ex.Message); }
                RoomUtil.Observe(task, "close disclaimer");
                var r = await UiActions.Expect(() => Closed(disclaimer, isModal), "close disclaimer", RoomUtil.ShortWait, ct);
                await MainThread.Frames(2);
                return r;
            });
        }
        if (sink.Count == 0) snap.Notes.Add(R2Text.T("note.popup_no_button"));
    }

    /// <summary>Confirm button of an unlock showcase (also used for the unlock screens shown inside the timeline).</summary>
    public static NClickableControl? UnlockConfirmButton(NUnlockScreen unlock)
    {
        var field = RoomUtil.Node<NClickableControl>(unlock, "_unlockConfirmButton");
        if (Usable(field)) return field;
        return RoomUtil.FirstUsable(SceneUtil.Descendants<NUnlockConfirmButton>(unlock))
               ?? (Usable(SceneUtil.Get<NClickableControl>(unlock, "ConfirmButton")) ? SceneUtil.Get<NClickableControl>(unlock, "ConfirmButton") : null);
    }

    // ---------------------------------------------------------------- lookups

    private static bool Usable(NClickableControl? b) => SceneUtil.Usable(b);

    private static NVerticalPopup? VerticalPopup(Node node)
    {
        // Wrappers keep the popup in a private field; error popups both are and contain one.
        var inner = RoomUtil.Node<NVerticalPopup>(node, "_verticalPopup");
        if (inner != null && SceneUtil.VisibleInTree(inner)) return inner;
        if (node is NVerticalPopup self) return self;
        return SceneUtil.Descendants<NVerticalPopup>(node, 6).FirstOrDefault(p => SceneUtil.VisibleInTree(p));
    }

    private static NClickableControl? FtueConfirmButton(Node ftue)
    {
        foreach (var field in ConfirmFields)
        {
            var b = RoomUtil.Node<NClickableControl>(ftue, field);
            if (Usable(b)) return b;
        }
        var typed = RoomUtil.FirstUsable(SceneUtil.Descendants<NFtueConfirmButton>(ftue));
        if (typed != null) return typed;
        var vp = VerticalPopup(ftue);
        if (Usable(vp?.YesButton)) return vp!.YesButton;
        var (yes, _) = ButtonsByName(ftue);
        return yes;
    }

    /// <summary>Usable buttons of an unknown popup classified by node name; a lone unnamed button counts as confirm.</summary>
    private static (NClickableControl? yes, NClickableControl? no) ButtonsByName(Node root)
    {
        var buttons = SceneUtil.Descendants<NClickableControl>(root, 12)
            .Where(b => SceneUtil.Usable(b) && !NameHas(b, NeverPress))
            .ToList();
        var yes = buttons.FirstOrDefault(b => NameHas(b, ConfirmNames) && !NameHas(b, DismissNames));
        var no = buttons.FirstOrDefault(b => NameHas(b, DismissNames));
        if (yes == null && no == null && buttons.Count == 1) yes = buttons[0];
        return (yes, no);
    }

    private static bool NameHas(Node n, string[] words)
    {
        string name;
        try { name = n.Name.ToString(); }
        catch (Exception) { return false; }
        return words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Closed(Node popup, bool isModal)
    {
        if (!SceneUtil.Alive(popup) || popup is CanvasItem ci && !ci.IsVisibleInTree()) return true;
        return isModal ? !ReferenceEquals(RoomUtil.OpenModal(), popup) : !ReferenceEquals(RoomUtil.CurrentScreen(), popup);
    }

    // ---------------------------------------------------------------- actions

    private static void AddCancelOnly(GameSnapshot snap, ActionSink sink, Node popup, bool isModal)
    {
        var vp = VerticalPopup(popup);
        NClickableControl? no = Usable(vp?.NoButton) ? vp!.NoButton : null;
        no ??= RoomUtil.Node<NClickableControl>(popup, "_noButton");
        if (!Usable(no)) no = ButtonsByName(popup).no;
        if (no == null)
        {
            snap.Notes.Add(R2Text.T("note.popup_human"));
            return;
        }
        snap.Notes.Add(R2Text.T("note.popup_destructive"));
        AddDismiss(sink, no, popup, isModal);
    }

    /// <summary>Captions of buttons that leave the game flow (bug reports, feedback, links).</summary>
    private static readonly string[] NeverPressText = { "report", "bug", "feedback", "discord", "报告", "反馈", "问题" };

    private static bool Forbidden(NClickableControl button, string text) =>
        NameHas(button, NeverPress) || NeverPressText.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static void AddConfirm(ActionSink sink, NClickableControl button, Node popup, bool isModal, bool ftue, string? label = null)
    {
        var text = RoomUtil.ButtonText(button);
        if (Forbidden(button, text)) return;
        label ??= R2Text.T("act.dismiss", text.Length > 0 ? text : R2Text.T("word.ok"));
        sink.Add(ActionKind.DismissPopup, label, "popup:confirm", ct => PressAsync(button, popup, isModal, ftue, ct),
            new Dictionary<string, string> { ["button"] = "confirm" }, unpredictable: true);
    }

    private static void AddDismiss(ActionSink sink, NClickableControl button, Node popup, bool isModal)
    {
        var text = RoomUtil.ButtonText(button);
        if (Forbidden(button, text)) return;
        sink.Add(ActionKind.DismissPopup, R2Text.T("act.dismiss", text.Length > 0 ? text : R2Text.T("word.no")), "popup:dismiss",
            ct => PressAsync(button, popup, isModal, ftue: false, ct),
            new Dictionary<string, string> { ["button"] = "dismiss" }, unpredictable: true);
    }

    private static async Task<ActionResult> PressAsync(NClickableControl button, Node popup, bool isModal, bool ftue, CancellationToken ct)
    {
        if (!SceneUtil.Alive(popup)) return ActionResult.Fail("the popup is gone");
        // Multi-page tutorials keep the same node and only turn the page.
        bool multiPage = popup is NCombatRulesFtue;
        int page = multiPage ? Reflect.Get<int>(popup, "_currentPage") : 0;
        var pressed = UiActions.Press(button, "popup button");
        if (!pressed.Success) return pressed;

        bool Done() => Closed(popup, isModal) || (multiPage && Reflect.Get<int>(popup, "_currentPage") != page);
        var r = await UiActions.Expect(Done, "popup", RoomUtil.ShortWait, ct);
        if (!r.Success && ftue && !multiPage && popup is NFtue and not NAcceptTutorialsFtue && SceneUtil.Alive(popup))
        {
            // A tutorial that ignored its button: close it the way the tutorial base class closes itself.
            ModLog.Warn($"tutorial {popup.GetType().Name} ignored its button; closing it");
            if (Reflect.TryCall(popup, "CloseFtue", out _))
                r = await UiActions.Expect(Done, "close tutorial", RoomUtil.ShortWait, ct);
        }
        await MainThread.Frames(3);
        return r;
    }
}
