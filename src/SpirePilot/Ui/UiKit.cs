using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Platform;
#if !STS2_V0107
using MegaCrit.Sts2.Core.ControllerInput;
#endif
using SpirePilot.Game;
using SpirePilot.Infra;

namespace SpirePilot.Ui;

/// <summary>
/// Factory for the overlay's controls. Every callback is wrapped so a UI exception never reaches the game, and
/// callbacks are ignored while <see cref="Syncing"/> (settings being pushed into the controls). Main thread only.
/// </summary>
internal static class UiKit
{
    /// <summary>True while settings are written into controls; change handlers must not react then.</summary>
    public static bool Syncing;

    /// <summary>The overlay text field that currently has keyboard focus (tracked through focus signals).</summary>
    public static Control? TypingControl { get; private set; }

    // ---------------------------------------------------------------- callback safety

    public static Action Safe(string where, Action action) => () =>
    {
        if (Syncing) return;
        try { action(); }
        catch (Exception ex) { ThrottledLog.Warn("ui:" + where, $"UI handler '{where}' failed: {ex}"); }
    };

    public static Action<T> Safe<T>(string where, Action<T> action) => value =>
    {
        if (Syncing) return;
        try { action(value); }
        catch (Exception ex) { ThrottledLog.Warn("ui:" + where, $"UI handler '{where}' failed: {ex}"); }
    };

    /// <summary>Runs <paramref name="action"/> now, logging (throttled) instead of throwing.</summary>
    public static void Guard(string where, Action action)
    {
        try { action(); }
        catch (Exception ex) { ThrottledLog.Warn("ui:" + where, $"UI '{where}' failed: {ex}"); }
    }

    // ---------------------------------------------------------------- basic controls

    public static Label Label(string text, int size = UiTheme.FontBody, Color? color = null, bool wrap = true)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Pass };
        if (wrap) l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (size != UiTheme.FontBody) l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    public static Label Muted(string text, int size = UiTheme.FontSmall) => Label(text, size, UiTheme.Muted);

    public static Label Heading(string text)
    {
        var l = Label(text, UiTheme.FontHeading, UiTheme.Gold, wrap: false);
        l.AddThemeFontOverride("font", UiTheme.Bold);
        l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return l;
    }

    /// <summary>
    /// A button. Buttons never take focus, so pressing one first ends typing in a text field (<see cref="EndTyping"/>):
    /// a half-typed SpinBox value is applied before the handler reads it, and game hotkeys work again afterwards.
    /// </summary>
    public static Button Button(string text, Action onPressed, UiTheme.ButtonKind kind = UiTheme.ButtonKind.Normal, string? tooltip = null)
    {
        var b = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = tooltip ?? "",
        };
        UiTheme.Style(b, kind);
        b.Pressed += Safe("button:" + text, () =>
        {
            EndTyping();
            onPressed();
        });
        return b;
    }

    /// <summary>A labelled switch (CheckButton).</summary>
    public static CheckButton Switch(string text, Action<bool> onToggled, string? tooltip = null)
    {
        var c = new CheckButton
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = tooltip ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        var handler = Safe<bool>("switch:" + text, on =>
        {
            EndTyping();
            onToggled(on);
        });
        c.Toggled += on => handler(on);
        return c;
    }

    /// <summary>A drop-down; <paramref name="onSelected"/> receives the selected item's id.</summary>
    public static OptionButton Options(Action<int> onSelected, string where)
    {
        var o = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FitToLongestItem = false,
            ClipText = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var handler = Safe<long>("options:" + where, index =>
        {
            EndTyping();
            onSelected(o.GetItemId((int)index));
        });
        o.ItemSelected += index => handler(index);
        // An OptionButton emits Pressed when its list opens (action mode "press"): stop typing before the list shows.
        o.Pressed += () => Guard("options.open:" + where, EndTyping);
        return o;
    }

    /// <summary>Replaces the items (no-op when they are already the same, so an open list is not disturbed).</summary>
    public static void SetItems(OptionButton o, IEnumerable<(string label, int id)> items)
    {
        var list = items.ToList();
        if (o.ItemCount == list.Count)
        {
            bool same = true;
            for (int i = 0; i < list.Count && same; i++)
                same = o.GetItemText(i) == list[i].label && o.GetItemId(i) == list[i].id;
            if (same) return;
        }
        int selectedId = o.Selected >= 0 ? o.GetItemId(o.Selected) : int.MinValue;
        o.Clear();
        foreach (var (label, id) in list) o.AddItem(label, id);
        if (selectedId != int.MinValue && o.GetItemIndex(selectedId) is var index and >= 0) o.Select(index);
    }

    /// <summary>Selects the item with <paramref name="id"/> (no signal).</summary>
    public static void SelectId(OptionButton o, int id)
    {
        int index = o.GetItemIndex(id);
        if (index >= 0 && o.Selected != index) o.Select(index);
    }

    public static LineEdit Line(string placeholder, Action<string>? onChanged = null, Action<string>? onSubmit = null, string where = "line")
    {
        var e = new LineEdit { PlaceholderText = placeholder, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CaretBlink = true };
        if (onChanged != null)
        {
            var changed = Safe(where + ":changed", onChanged);
            e.TextChanged += text => changed(text);
        }
        if (onSubmit != null)
        {
            var submit = Safe(where + ":submit", onSubmit);
            e.TextSubmitted += text => submit(text);
        }
        TrackTyping(e);
        return e;
    }

    public static TextEdit Area(string placeholder, Action<string> onChanged, float minHeight = 64, string where = "area")
    {
        var t = new TextEdit
        {
            PlaceholderText = placeholder,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, minHeight),
            ScrollFitContentHeight = true,
            CaretBlink = true,
        };
        t.TextChanged += Safe(where, () => onChanged(t.Text));
        TrackTyping(t);
        return t;
    }

    public static HSlider Slider(double min, double max, double step, Action<double> onChanged, string where)
    {
        var s = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(120, 18),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var handler = Safe("slider:" + where, onChanged);
        s.ValueChanged += v => handler(v);
        return s;
    }

    /// <summary>
    /// A number field. Typed values apply on Enter or focus loss only, so a value typed and then followed by a click on
    /// a (non-focusing) button would be lost: buttons call <see cref="EndTyping"/> and readers call <see cref="ApplySpin"/>.
    /// </summary>
    public static SpinBox Spin(double min, double max, double step, Action<double> onChanged, string where, double arrowStep = 0)
    {
        var s = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Rounded = step >= 1,
            // Typed values apply on Enter / focus loss, so clamping never rewrites a half-typed number.
            UpdateOnTextChanged = false,
            CustomMinimumSize = new Vector2(110, 0),
            FocusMode = Control.FocusModeEnum.None,
        };
        if (arrowStep > 0) s.CustomArrowStep = arrowStep;
        var handler = Safe("spin:" + where, onChanged);
        s.ValueChanged += v => handler(v);
        TrackTyping(s.GetLineEdit());
        return s;
    }

    public static RichTextLabel Rich(float minHeight, bool follow = false)
    {
        var r = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            ScrollFollowing = follow,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            FitContent = false,
            CustomMinimumSize = new Vector2(0, minHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // Selection enabling switches focus mode on; keep keyboard focus with the game.
        r.FocusMode = Control.FocusModeEnum.None;
        return r;
    }

    // ---------------------------------------------------------------- layout

    public static HBoxContainer Row(params Control[] children)
    {
        var h = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        foreach (var c in children) h.AddChild(c);
        return h;
    }

    public static HFlowContainer Flow(params Control[] children)
    {
        var f = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        foreach (var c in children) f.AddChild(c);
        return f;
    }

    /// <summary>A titled section card added to <paramref name="parent"/>; returns its content box.</summary>
    public static VBoxContainer Card(Container parent, string? title, string? subtitle = null)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.CardBg, UiTheme.CardBorder, 8, 1, 10, 8));
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        if (title != null) box.AddChild(Heading(title));
        if (subtitle != null) box.AddChild(Muted(subtitle));
        parent.AddChild(panel);
        return box;
    }

    /// <summary>"Label  [field]" row; the label keeps a fixed share of the width.</summary>
    public static HBoxContainer Field(Container parent, string label, Control field, string? tooltip = null)
    {
        var l = Label(label, UiTheme.FontSmall, UiTheme.Muted, wrap: true);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.SizeFlagsStretchRatio = 0.75f;
        l.TooltipText = tooltip ?? "";
        l.VerticalAlignment = VerticalAlignment.Center;
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        field.SizeFlagsStretchRatio = 1.25f;
        if (tooltip != null) field.TooltipText = tooltip;
        var row = Row(l, field);
        parent.AddChild(row);
        return row;
    }

    /// <summary>A round status dot whose colour is changed through <paramref name="style"/>.</summary>
    public static PanelContainer Dot(Color color, out StyleBoxFlat style, int size = 12)
    {
        style = UiTheme.Box(color, new Color(0, 0, 0, 0.35f), size / 2, 1, 0, 0);
        var p = new PanelContainer
        {
            CustomMinimumSize = new Vector2(size, size),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        p.AddThemeStyleboxOverride("panel", style);
        return p;
    }

    public static Control Spacer(float height = 4) => new Control { CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };

    // ---------------------------------------------------------------- value helpers (no signals, no clobbering)

    public static void SetText(Label? l, string text)
    {
        if (l != null && SceneUtil.Valid(l) && l.Text != text) l.Text = text;
    }

    public static void SetText(Button? b, string text)
    {
        if (b != null && SceneUtil.Valid(b) && b.Text != text) b.Text = text;
    }

    /// <summary>Sets a text field unless the user is typing in it.</summary>
    public static void SetText(LineEdit? e, string text)
    {
        if (e == null || !SceneUtil.Valid(e) || e.HasFocus() || e.Text == text) return;
        e.Text = text;
    }

    public static void SetText(TextEdit? t, string text)
    {
        if (t == null || !SceneUtil.Valid(t) || t.HasFocus() || t.Text == text) return;
        t.Text = text;
    }

    public static void SetPressed(BaseButton? b, bool on)
    {
        if (b != null && SceneUtil.Valid(b) && b.ButtonPressed != on) b.SetPressedNoSignal(on);
    }

    public static void SetValue(Godot.Range? r, double v)
    {
        if (r == null || !SceneUtil.Valid(r)) return;
        if (r is SpinBox sb && sb.GetLineEdit().HasFocus()) return;
        if (Math.Abs(r.Value - v) > 1e-9) r.SetValueNoSignal(v);
    }

    /// <summary>Changes a label's colour (only when it differs: every override change re-themes the control).</summary>
    public static void SetColor(Label? l, Color c)
    {
        if (l == null || !SceneUtil.Valid(l)) return;
        if (l.HasThemeColorOverride("font_color") && l.GetThemeColor("font_color") == c) return;
        l.AddThemeColorOverride("font_color", c);
    }

    /// <summary>Escapes text for a BBCode RichTextLabel.</summary>
    public static string Esc(string? s) => (s ?? "").Replace("[", "[lb]");

    public static string Hex(Color c) => "#" + c.ToHtml(false);

    public static string Colored(string text, Color c) => $"[color={Hex(c)}]{Esc(text)}[/color]";

    // ---------------------------------------------------------------- typing focus

    /// <summary>Tracks focus of a text control so the input guard can keep game hotkeys from firing while typing.</summary>
    public static void TrackTyping(Control c)
    {
        c.FocusEntered += () =>
        {
            TypingControl = c;
            Guard("vkbd.open", () => { if (WantsVirtualKeyboard()) PlatformUtil.OpenVirtualKeyboard(); });
        };
        c.FocusExited += () =>
        {
            if (ReferenceEquals(TypingControl, c)) TypingControl = null;
            Guard("vkbd.close", () => { if (WantsVirtualKeyboard()) PlatformUtil.CloseVirtualKeyboard(); });
        };
        if (c is LineEdit line)
        {
            // Enter / Esc end editing but keep focus; give the keyboard back to the game then.
            line.EditingToggled += editing =>
            {
                if (editing) return;
                MainThread.Post(() => Guard("unedit", () =>
                {
                    if (GodotObject.IsInstanceValid(line) && line.HasFocus() && !line.IsEditing()) line.ReleaseFocus();
                }));
            };
        }
    }

    private static bool _applying;

    /// <summary>
    /// Applies the text typed into <paramref name="spin"/> (Godot only applies it on Enter / focus loss) when its field
    /// has focus. Emits ValueChanged when the value changes; never re-entered from that handler.
    /// </summary>
    public static void ApplySpin(SpinBox? spin)
    {
        if (_applying || spin == null || !SceneUtil.Valid(spin)) return;
        LineEdit line;
        try { line = spin.GetLineEdit(); }
        catch (Exception) { return; }
        if (line == null || !GodotObject.IsInstanceValid(line) || !line.HasFocus()) return;
        _applying = true;
        try { spin.Apply(); }
        catch (Exception ex) { ThrottledLog.Warn("ui.spin.apply", "UI: applying a typed number failed: " + ex.Message); }
        finally { _applying = false; }
    }

    /// <summary>
    /// Ends typing in the overlay: applies a half-typed SpinBox value, then releases the focused text field so game
    /// hotkeys reach the game again. Called before every button / switch / option handler, on clicks anywhere but the
    /// focused field, and when the panel hides.
    /// </summary>
    public static void EndTyping()
    {
        var c = TypingControl;
        if (c == null) return;
        if (!GodotObject.IsInstanceValid(c))
        {
            TypingControl = null;
            return;
        }
        if (!c.HasFocus()) return;
        if (c.GetParent() is SpinBox spin) ApplySpin(spin);
        if (GodotObject.IsInstanceValid(c) && c.HasFocus()) c.ReleaseFocus();
    }

    /// <summary>Whether <paramref name="point"/> (canvas coordinates) is over the focused text field (a SpinBox's arrows included).</summary>
    public static bool IsOverTypingControl(Vector2 point)
    {
        var c = TypingControl;
        if (c == null || !GodotObject.IsInstanceValid(c)) return false;
        Control area = c.GetParent() is SpinBox spin ? spin : c;
        return area.GetGlobalRect().HasPoint(point);
    }

    /// <summary>True while the focused text field shows its right-click menu (a click on the menu must not end typing).</summary>
    public static bool TypingMenuOpen
    {
        get
        {
            try
            {
                return TypingControl switch
                {
                    LineEdit l when GodotObject.IsInstanceValid(l) => l.IsMenuVisible(),
                    TextEdit t when GodotObject.IsInstanceValid(t) => t.IsMenuVisible(),
                    _ => false,
                };
            }
            catch (Exception) { return false; }
        }
    }

    public static bool IsTyping
    {
        get
        {
            var c = TypingControl;
            if (c == null) return false;
            if (!GodotObject.IsInstanceValid(c))
            {
                TypingControl = null;
                return false;
            }
            return c.HasFocus();
        }
    }

    /// <summary>
    /// Steam Deck / controller: open the platform's on-screen keyboard for our text fields. Unverified at runtime:
    /// the "SteamDeck=1" environment variable is set by SteamOS game mode.
    /// </summary>
    private static bool WantsVirtualKeyboard()
    {
        if (System.Environment.GetEnvironmentVariable("SteamDeck") == "1") return true;
        try
        {
            var cm = NControllerManager.Instance;
            if (cm == null || !GodotObject.IsInstanceValid(cm)) return false;
#if STS2_V0107
            return cm.IsUsingController;
#else
            return cm.InputType == InputType.Controller;
#endif
        }
        catch (Exception) { return false; }
    }
}
