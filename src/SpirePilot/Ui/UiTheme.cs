using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Localization.Fonts.FontPathSets;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Text;
using SpirePilot.Game;

namespace SpirePilot.Ui;

/// <summary>
/// Palette, fonts and the Godot <see cref="Theme"/> of the overlay. The theme sits on the overlay's host control, so
/// everything Godot draws by itself (scroll bars, OptionButton popups, tooltips, switch icons, carets) is styled too.
/// Main thread only.
/// </summary>
internal static class UiTheme
{
    // Dark blue-grey surfaces with a warm gold trim, like the game's own panels.
    public static readonly Color PanelBg = new(0.055f, 0.063f, 0.082f, 0.97f);
    public static readonly Color TitleBg = new(0.10f, 0.11f, 0.14f, 1f);
    public static readonly Color CardBg = new(1f, 1f, 1f, 0.035f);
    public static readonly Color CardBorder = new(1f, 1f, 1f, 0.08f);
    public static readonly Color Gold = new(0.89f, 0.73f, 0.42f);
    public static readonly Color GoldDim = new(0.60f, 0.48f, 0.27f);
    public static readonly Color Text = new(0.93f, 0.90f, 0.84f);
    public static readonly Color Muted = new(0.63f, 0.63f, 0.61f);
    public static readonly Color Faint = new(0.45f, 0.46f, 0.48f);
    public static readonly Color Good = new(0.47f, 0.83f, 0.50f);
    public static readonly Color Warn = new(0.96f, 0.76f, 0.33f);
    public static readonly Color Bad = new(0.96f, 0.43f, 0.39f);
    public static readonly Color Info = new(0.53f, 0.72f, 0.98f);
    public static readonly Color Field = new(0.03f, 0.035f, 0.05f, 1f);
    public static readonly Color ButtonBg = new(0.15f, 0.165f, 0.20f, 1f);
    public static readonly Color ButtonHover = new(0.21f, 0.23f, 0.28f, 1f);
    public static readonly Color ButtonDown = new(0.38f, 0.30f, 0.15f, 1f);
    public static readonly Color Clear = new(0, 0, 0, 0);

    public const int FontBody = 16;
    public const int FontSmall = 14;
    public const int FontHeading = 15;
    public const int FontTitle = 18;

    public static Font Regular { get; private set; } = null!;
    public static Font Bold { get; private set; } = null!;
    public static Theme Theme { get; private set; } = null!;

    private static readonly string[] SystemFaces =
    {
        "Noto Sans CJK SC", "Source Han Sans SC", "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC",
        "WenQuanYi Micro Hei", "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji", "Noto Color Emoji", "Apple Color Emoji",
        "DejaVu Sans", "sans-serif",
    };

    /// <summary>Colour of the status dot / floating button for a pilot state.</summary>
    public static Color StateColor(PilotState state) => state switch
    {
        PilotState.Off => Faint,
        PilotState.Waiting => Info,
        PilotState.HumanTurn => new Color(0.62f, 0.68f, 0.80f),
        PilotState.Thinking => new Color(0.74f, 0.58f, 0.98f),
        PilotState.Acting => Good,
        PilotState.Paused => Warn,
        PilotState.StepReady => new Color(0.40f, 0.86f, 0.90f),
        PilotState.Advising => new Color(0.40f, 0.86f, 0.70f),
        PilotState.Error => Bad,
        _ => Muted,
    };

    /// <summary>Rebuilds fonts and the theme (the base face depends on the UI language).</summary>
    public static void Rebuild()
    {
        ButtonStyles.Clear();
        Regular = BuildFont(bold: false);
        Bold = BuildFont(bold: true);
        Theme = BuildTheme();
    }

    public static StyleBoxFlat Box(Color bg, Color border, int radius = 8, int borderWidth = 1, int padX = 10, int padY = 6)
    {
        var b = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
        b.SetBorderWidthAll(borderWidth);
        b.SetCornerRadiusAll(radius);
        b.ContentMarginLeft = padX;
        b.ContentMarginRight = padX;
        b.ContentMarginTop = padY;
        b.ContentMarginBottom = padY;
        return b;
    }

    // ---------------------------------------------------------------- fonts

    /// <summary>
    /// Chinese UI: the game's own Simplified Chinese face as the base (it also has Latin glyphs, like the game in
    /// Chinese). English UI: the game's Latin face (Kreon) with the Chinese face as fallback, so Chinese chat and
    /// names still render. A system font list is the last fallback (symbols / emoji / missing resources).
    /// </summary>
    private static Font BuildFont(bool bold)
    {
        var type = bold ? FontType.Bold : FontType.Regular;
        Font? cjk = null;
        try
        {
            // Unverified at runtime: the path set returns res:// paths inside the game's pck.
            var path = new ZhsFontPathSet().GetPath(type);
            if (!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path)) cjk = ResourceLoader.Load<Font>(path);
        }
        catch (Exception ex) { ThrottledLog.Warn("ui.font.zhs", "UI: game CJK font unavailable: " + ex.Message); }
        if (cjk == null)
        {
            try { cjk = FontManager.GetSubstituteFont("zhs", type); }
            catch (Exception) { /* not available */ }
        }

        Font? latin = null;
        try
        {
            var path = bold ? "res://themes/kreon_bold_shared.tres" : "res://themes/kreon_regular_shared.tres";
            if (ResourceLoader.Exists(path)) latin = ResourceLoader.Load<Font>(path);
        }
        catch (Exception) { /* fall back below */ }

        var system = new SystemFont
        {
            FontNames = SystemFaces,
            FontWeight = bold ? 700 : 400,
            AllowSystemFallback = true,
        };

        Font baseFont = L10n.IsChinese ? cjk ?? latin ?? system : latin ?? cjk ?? system;
        var font = new FontVariation { BaseFont = baseFont };
        var fallbacks = new Godot.Collections.Array<Font>();
        if (cjk != null && !ReferenceEquals(baseFont, cjk)) fallbacks.Add(cjk);
        if (latin != null && !ReferenceEquals(baseFont, latin)) fallbacks.Add(latin);
        if (!ReferenceEquals(baseFont, system)) fallbacks.Add(system);
        font.Fallbacks = fallbacks;
        return font;
    }

    // ---------------------------------------------------------------- theme

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFont = Regular, DefaultFontSize = FontBody };
        // A theme's default font only applies where no theme in the chain names one; the game's project theme does,
        // so name the font per type as well.
        foreach (var type in new[]
                 {
                     "Label", "Button", "CheckBox", "CheckButton", "OptionButton", "LineEdit", "TextEdit", "PopupMenu",
                     "TooltipLabel", "SpinBox", "ItemList", "MenuButton",
                 })
        {
            theme.SetFont("font", type, Regular);
            theme.SetFontSize("font_size", type, FontBody);
        }
        theme.SetFont("normal_font", "RichTextLabel", Regular);
        theme.SetFont("bold_font", "RichTextLabel", Bold);
        theme.SetFont("italics_font", "RichTextLabel", Regular);
        theme.SetFont("mono_font", "RichTextLabel", Regular);
        foreach (var size in new[] { "normal_font_size", "bold_font_size", "italics_font_size", "mono_font_size" })
            theme.SetFontSize(size, "RichTextLabel", FontSmall);
        theme.SetColor("default_color", "RichTextLabel", Text);
        theme.SetColor("selection_color", "RichTextLabel", new Color(Gold, 0.30f));
        theme.SetStylebox("focus", "RichTextLabel", new StyleBoxEmpty());
        theme.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());

        theme.SetColor("font_color", "Label", Text);

        // Scroll bars: thin rounded thumb, no gutter.
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            var track = new StyleBoxEmpty();
            if (type == "VScrollBar")
            {
                track.ContentMarginLeft = 3;
                track.ContentMarginRight = 3;
            }
            else
            {
                track.ContentMarginTop = 3;
                track.ContentMarginBottom = 3;
            }
            theme.SetStylebox("scroll", type, track);
            theme.SetStylebox("scroll_focus", type, track);
            theme.SetStylebox("grabber", type, Box(new Color(1, 1, 1, 0.18f), Clear, 3, 0, 0, 0));
            theme.SetStylebox("grabber_highlight", type, Box(new Color(1, 1, 1, 0.30f), Clear, 3, 0, 0, 0));
            theme.SetStylebox("grabber_pressed", type, Box(new Color(Gold, 0.55f), Clear, 3, 0, 0, 0));
        }

        // Drop-down lists.
        var popup = Box(new Color(0.08f, 0.09f, 0.115f, 0.99f), new Color(Gold, 0.45f), 8, 1, 6, 6);
        popup.ShadowColor = new Color(0, 0, 0, 0.45f);
        popup.ShadowSize = 10;
        popup.ShadowOffset = new Vector2(0, 4);
        theme.SetStylebox("panel", "PopupMenu", popup);
        theme.SetStylebox("hover", "PopupMenu", Box(new Color(Gold, 0.18f), Clear, 5, 0, 6, 3));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Text);
        theme.SetColor("font_disabled_color", "PopupMenu", Faint);
        theme.SetConstant("v_separation", "PopupMenu", 8);
        theme.SetConstant("item_start_padding", "PopupMenu", 8);
        theme.SetConstant("item_end_padding", "PopupMenu", 12);

        // Tooltips.
        theme.SetStylebox("panel", "TooltipPanel", Box(new Color(0.08f, 0.09f, 0.115f, 0.98f), new Color(Gold, 0.40f), 6, 1, 8, 5));
        theme.SetColor("font_color", "TooltipLabel", Text);
        theme.SetFontSize("font_size", "TooltipLabel", FontSmall);

        // Buttons (default look; special kinds get overrides from ButtonStyle()).
        ApplyButtonStyle(theme, "Button", ButtonKind.Normal);
        ApplyButtonStyle(theme, "OptionButton", ButtonKind.Normal);
        theme.SetConstant("modulate_arrow", "OptionButton", 1);
        theme.SetColor("font_color", "OptionButton", Text);

        // Switches (CheckButton): drawn icons in the palette.
        theme.SetIcon("checked", "CheckButton", SwitchIcon(true, false));
        theme.SetIcon("unchecked", "CheckButton", SwitchIcon(false, false));
        theme.SetIcon("checked_disabled", "CheckButton", SwitchIcon(true, true));
        theme.SetIcon("unchecked_disabled", "CheckButton", SwitchIcon(false, true));
        theme.SetIcon("checked_mirrored", "CheckButton", SwitchIcon(true, false));
        theme.SetIcon("unchecked_mirrored", "CheckButton", SwitchIcon(false, false));
        theme.SetIcon("checked_disabled_mirrored", "CheckButton", SwitchIcon(true, true));
        theme.SetIcon("unchecked_disabled_mirrored", "CheckButton", SwitchIcon(false, true));
        var bare = new StyleBoxEmpty { ContentMarginTop = 3, ContentMarginBottom = 3, ContentMarginLeft = 0, ContentMarginRight = 2 };
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            theme.SetStylebox(state, "CheckButton", bare);
        theme.SetColor("font_color", "CheckButton", Text);
        theme.SetColor("font_hover_color", "CheckButton", Text);
        theme.SetColor("font_pressed_color", "CheckButton", Text);
        theme.SetColor("font_hover_pressed_color", "CheckButton", Text);
        theme.SetColor("font_focus_color", "CheckButton", Text);
        theme.SetColor("font_disabled_color", "CheckButton", Faint);
        theme.SetConstant("h_separation", "CheckButton", 10);

        // Text fields.
        foreach (var type in new[] { "LineEdit", "TextEdit" })
        {
            theme.SetStylebox("normal", type, Box(Field, new Color(1, 1, 1, 0.12f), 6, 1, 8, 5));
            theme.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), new Color(Gold, 0.85f), 6, 1, 8, 5));
            theme.SetStylebox("read_only", type, Box(new Color(Field, 0.6f), new Color(1, 1, 1, 0.06f), 6, 1, 8, 5));
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_placeholder_color", type, new Color(Muted, 0.75f));
            theme.SetColor("caret_color", type, Gold);
            theme.SetColor("selection_color", type, new Color(Gold, 0.32f));
            theme.SetColor("font_selected_color", type, Text);
        }
        theme.SetColor("font_uneditable_color", "LineEdit", Muted);
        theme.SetColor("font_readonly_color", "TextEdit", Muted);
        theme.SetColor("background_color", "TextEdit", Clear);

        // Sliders.
        var sliderTrack = Box(new Color(1, 1, 1, 0.10f), Clear, 3, 0, 0, 3);
        theme.SetStylebox("slider", "HSlider", sliderTrack);
        theme.SetStylebox("grabber_area", "HSlider", Box(new Color(Gold, 0.65f), Clear, 3, 0, 0, 3));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(new Color(Gold, 0.85f), Clear, 3, 0, 0, 3));
        theme.SetIcon("grabber", "HSlider", KnobIcon(false));
        theme.SetIcon("grabber_highlight", "HSlider", KnobIcon(true));
        theme.SetIcon("grabber_disabled", "HSlider", KnobIcon(false, disabled: true));

        theme.SetConstant("separation", "VBoxContainer", 6);
        theme.SetConstant("separation", "HBoxContainer", 6);
        theme.SetConstant("h_separation", "HFlowContainer", 6);
        theme.SetConstant("v_separation", "HFlowContainer", 6);
        theme.SetConstant("scrollbar_v_separation", "ScrollContainer", 4);
        return theme;
    }

    // ---------------------------------------------------------------- buttons

    public enum ButtonKind { Normal, Primary, Danger, Tab, Small, Ghost }

    private static readonly Dictionary<ButtonKind, (StyleBox normal, StyleBox hover, StyleBox pressed, StyleBox disabled)> ButtonStyles = new();

    private static (StyleBox normal, StyleBox hover, StyleBox pressed, StyleBox disabled) StylesFor(ButtonKind kind)
    {
        if (ButtonStyles.TryGetValue(kind, out var s)) return s;
        int padX = kind is ButtonKind.Small or ButtonKind.Ghost ? 8 : 12;
        int padY = kind is ButtonKind.Small or ButtonKind.Ghost ? 3 : 5;
        switch (kind)
        {
            case ButtonKind.Primary:
                s = (Box(new Color(0.55f, 0.42f, 0.19f), new Color(Gold, 0.9f), 6, 1, padX, padY),
                     Box(new Color(0.66f, 0.51f, 0.24f), Gold, 6, 1, padX, padY),
                     Box(new Color(0.44f, 0.33f, 0.15f), Gold, 6, 1, padX, padY),
                     Box(new Color(0.20f, 0.18f, 0.15f), new Color(1, 1, 1, 0.06f), 6, 1, padX, padY));
                break;
            case ButtonKind.Danger:
                s = (Box(new Color(0.30f, 0.12f, 0.12f), new Color(Bad, 0.55f), 6, 1, padX, padY),
                     Box(new Color(0.42f, 0.16f, 0.15f), Bad, 6, 1, padX, padY),
                     Box(new Color(0.52f, 0.18f, 0.16f), Bad, 6, 1, padX, padY),
                     Box(new Color(0.16f, 0.12f, 0.12f), new Color(1, 1, 1, 0.06f), 6, 1, padX, padY));
                break;
            case ButtonKind.Tab:
                s = (Box(Clear, Clear, 6, 0, 8, 6),
                     Box(new Color(1, 1, 1, 0.06f), Clear, 6, 0, 8, 6),
                     Box(new Color(Gold, 0.20f), new Color(Gold, 0.85f), 6, 1, 8, 6),
                     Box(Clear, Clear, 6, 0, 8, 6));
                break;
            case ButtonKind.Ghost:
                s = (Box(Clear, Clear, 6, 0, padX, padY),
                     Box(new Color(1, 1, 1, 0.08f), Clear, 6, 0, padX, padY),
                     Box(new Color(1, 1, 1, 0.14f), Clear, 6, 0, padX, padY),
                     Box(Clear, Clear, 6, 0, padX, padY));
                break;
            default:
                s = (Box(ButtonBg, new Color(1, 1, 1, 0.10f), 6, 1, padX, padY),
                     Box(ButtonHover, new Color(Gold, 0.55f), 6, 1, padX, padY),
                     Box(ButtonDown, new Color(Gold, 0.95f), 6, 1, padX, padY),
                     Box(new Color(0.11f, 0.12f, 0.14f), new Color(1, 1, 1, 0.05f), 6, 1, padX, padY));
                break;
        }
        ButtonStyles[kind] = s;
        return s;
    }

    private static void ApplyButtonStyle(Theme theme, string type, ButtonKind kind)
    {
        var (normal, hover, pressed, disabled) = StylesFor(kind);
        theme.SetStylebox("normal", type, normal);
        theme.SetStylebox("hover", type, hover);
        theme.SetStylebox("pressed", type, pressed);
        theme.SetStylebox("hover_pressed", type, pressed);
        theme.SetStylebox("disabled", type, disabled);
        theme.SetStylebox("focus", type, new StyleBoxEmpty());
        theme.SetColor("font_color", type, Text);
        theme.SetColor("font_hover_color", type, Text);
        theme.SetColor("font_pressed_color", type, Text);
        theme.SetColor("font_hover_pressed_color", type, Text);
        theme.SetColor("font_focus_color", type, Text);
        theme.SetColor("font_disabled_color", type, Faint);
    }

    /// <summary>Applies the look of <paramref name="kind"/> to one button (theme overrides).</summary>
    public static void Style(Button button, ButtonKind kind)
    {
        var (normal, hover, pressed, disabled) = StylesFor(kind);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        if (kind == ButtonKind.Tab)
        {
            button.AddThemeColorOverride("font_color", Muted);
            button.AddThemeColorOverride("font_hover_color", Text);
            button.AddThemeColorOverride("font_pressed_color", Gold);
            button.AddThemeColorOverride("font_hover_pressed_color", Gold);
        }
        if (kind is ButtonKind.Small or ButtonKind.Ghost) button.AddThemeFontSizeOverride("font_size", FontSmall);
    }

    // ---------------------------------------------------------------- drawn icons

    /// <summary>Switch icon (pill track + knob), drawn with simple signed distances and anti-aliasing.</summary>
    private static ImageTexture SwitchIcon(bool on, bool disabled)
    {
        const int w = 40, h = 22;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var track = on ? new Color(0.62f, 0.48f, 0.22f) : new Color(0.24f, 0.26f, 0.30f);
        var knob = on ? new Color(0.98f, 0.92f, 0.78f) : new Color(0.66f, 0.67f, 0.70f);
        float alpha = disabled ? 0.4f : 1f;
        float r = h / 2f - 1f;
        float kx = on ? w - h / 2f : h / 2f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // capsule from (h/2, h/2) to (w-h/2, h/2)
                float cx = Math.Clamp(px, h / 2f, w - h / 2f);
                float dTrack = MathF.Sqrt((px - cx) * (px - cx) + (py - h / 2f) * (py - h / 2f)) - r;
                float aTrack = Math.Clamp(0.5f - dTrack, 0f, 1f);
                float dKnob = MathF.Sqrt((px - kx) * (px - kx) + (py - h / 2f) * (py - h / 2f)) - (r - 3f);
                float aKnob = Math.Clamp(0.5f - dKnob, 0f, 1f);
                var c = new Color(track, aTrack * alpha);
                c = Over(c, new Color(knob, aKnob * alpha));
                img.SetPixel(x, y, c);
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Resize grip: three short diagonal strokes in the bottom-right corner.</summary>
    public static ImageTexture GripIcon()
    {
        const int s = 16;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        var c = new Color(Gold, 0.75f);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float a = 0;
                // Lines x + y = k for k in {s+2, s+6, s+10}, only inside the corner triangle.
                foreach (var k in new[] { s + 1f, s + 6f, s + 11f })
                {
                    float d = Math.Abs(x + 0.5f + y + 0.5f - k) / 1.4142f;
                    a = Math.Max(a, Math.Clamp(1.1f - d, 0f, 1f));
                }
                if (x + y < s) a = 0;
                img.SetPixel(x, y, new Color(c, c.A * a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    private static ImageTexture KnobIcon(bool highlight, bool disabled = false)
    {
        const int s = 16;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        var fill = disabled ? Faint : highlight ? new Color(1f, 0.93f, 0.75f) : new Color(0.95f, 0.86f, 0.64f);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - s / 2f, dy = y + 0.5f - s / 2f;
                float d = MathF.Sqrt(dx * dx + dy * dy) - (s / 2f - 1.5f);
                float a = Math.Clamp(0.5f - d, 0f, 1f);
                float ring = Math.Clamp(0.5f - Math.Abs(d + 1f), 0f, 1f);
                var c = new Color(fill, a);
                c = Over(c, new Color(GoldDim, ring * 0.8f * a));
                img.SetPixel(x, y, c);
            }
        return ImageTexture.CreateFromImage(img);
    }

    private static Color Over(Color dst, Color src)
    {
        float a = src.A + dst.A * (1 - src.A);
        if (a <= 0.0001f) return new Color(0, 0, 0, 0);
        return new Color(
            (src.R * src.A + dst.R * dst.A * (1 - src.A)) / a,
            (src.G * src.A + dst.G * dst.A * (1 - src.A)) / a,
            (src.B * src.A + dst.B * dst.A * (1 - src.A)) / a,
            a);
    }
}
