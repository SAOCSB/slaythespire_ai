using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>
/// One tab of the control panel: a scrolling page built from code. Pages are plain C# objects that own their
/// controls (no Node subclasses: Godot would never call their overrides without the source generators).
/// </summary>
internal abstract class Page
{
    /// <summary>L10n key of the tab title.</summary>
    public abstract string TitleKey { get; }

    public ScrollContainer Root { get; private set; } = null!;

    protected VBoxContainer Body { get; private set; } = null!;

    /// <summary>
    /// How often (ms) the page wants a fresh snapshot while it is visible; 0 = it shows no game state. The overlay
    /// stretches this while the pilot is off or paused, and never captures while the panel is hidden.
    /// </summary>
    public virtual ulong SnapshotIntervalMs => 500;

    public void Build()
    {
        Root = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
            FollowFocus = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_left", 2);
        margin.AddThemeConstantOverride("margin_top", 2);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        Body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        Body.AddThemeConstantOverride("separation", 8);
        margin.AddChild(Body);
        Root.AddChild(margin);
        try
        {
            BuildContent();
        }
        catch (Exception ex)
        {
            // Keep the rest of the panel working; show what broke instead of this page.
            Failed = true;
            ModLog.Error($"overlay page {TitleKey} failed to build: {ex}");
            Body.AddChild(UiKit.Label($"({ex.GetType().Name}: {ex.Message})", UiTheme.FontSmall, UiTheme.Bad));
        }
    }

    /// <summary>True when building the page threw; the overlay then leaves it alone.</summary>
    public bool Failed { get; private set; }

    protected abstract void BuildContent();

    /// <summary>Pushes settings into the controls (called with <see cref="UiKit.Syncing"/> set).</summary>
    public virtual void Sync(PilotSettings settings) { }

    /// <summary>Live refresh while the page is visible: fast ≈ 5×/s, slow ≈ 2×/s.</summary>
    public virtual void Refresh(bool slow) { }

    /// <summary>Called every frame (cheap work only), visible or not.</summary>
    public virtual void Pump() { }

    protected static PilotSettings Draft => SettingsDraft.Value;

    /// <summary>Edits the settings draft; false when the edit or (with <paramref name="now"/>) its save failed.</summary>
    protected static bool Edit(Action<PilotSettings> change, bool now = false) => SettingsDraft.Edit(change, now);

    protected static GameSnapshot? Snap => PilotRuntime.Bridge?.Latest;
}
