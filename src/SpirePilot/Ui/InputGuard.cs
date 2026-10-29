using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using SpirePilot.Infra;

namespace SpirePilot.Ui;

/// <summary>
/// Keeps the game from reacting to input meant for the overlay.
/// <list type="bullet">
/// <item>While one of our text fields has focus, the game's keyboard hotkey pipeline is skipped:
/// <c>NInputManager._UnhandledKeyInput</c> translates keys into game actions, <c>NHotkeyManager._UnhandledInput</c>
/// and <c>NPlayerHand._UnhandledInput</c> dispatch them. A focused LineEdit/TextEdit accepts typed characters during
/// the GUI phase, but not key releases or keys without a character (Esc, F-keys), which would otherwise leak.</item>
/// <item>About 45 game nodes override <c>_Input</c>, which runs before GUI processing (dev console backtick, card
/// targeting, map drawing, scrollable containers ...). Their key events are skipped while typing, and mouse presses /
/// wheel / gestures over the visible panel or floating button are skipped so clicking the panel never clicks the game
/// underneath. Releases and motion always pass, so drags started in the game finish normally.</item>
/// </list>
/// Every patch is optional (resolved by reflection, applied with try/catch) and every prefix fails open.
/// The prefixes run for every input event on every patched node (NButton has many instances), so they only do
/// type checks and cached-rectangle tests.
/// </summary>
internal static class InputGuard
{
    private static bool _dispatchPatched;
    private static bool _inputPatched;

    public static int PatchedCount { get; private set; }

    /// <summary>Patches the hotkey dispatch (cheap: a handful of methods). Called from <see cref="Overlay.Initialize"/>.</summary>
    public static void ApplyDispatch(Harmony harmony)
    {
        if (_dispatchPatched) return;
        _dispatchPatched = true;
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(InputGuard), nameof(HotkeyPrefix)));
        TryPatch(harmony, AccessTools.Method(typeof(NInputManager), "_UnhandledKeyInput", new[] { typeof(InputEvent) }), prefix);
        TryPatch(harmony, AccessTools.Method(typeof(NInputManager), "_UnhandledInput", new[] { typeof(InputEvent) }), prefix);
        TryPatch(harmony, AccessTools.Method(typeof(NHotkeyManager), "_UnhandledInput", new[] { typeof(InputEvent) }), prefix);
        var hand = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
        if (hand != null) TryPatch(harmony, AccessTools.Method(hand, "_UnhandledInput", new[] { typeof(InputEvent) }), prefix);
    }

    /// <summary>
    /// Patches every game node's own <c>_Input</c> override (found by reflection, so both game versions are covered).
    /// Done once when the overlay is first built (the game is still loading then).
    /// </summary>
    public static void ApplyInputOverrides(Harmony harmony)
    {
        if (_inputPatched) return;
        _inputPatched = true;
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(InputGuard), nameof(InputPrefix)));
        int before = PatchedCount;
        foreach (var method in FindInputOverrides(typeof(NGame).Assembly)) TryPatch(harmony, method, prefix);
        ModLog.Info($"UI input guard: {PatchedCount - before} _Input overrides guarded, {PatchedCount} patches total");
    }

    private static IEnumerable<MethodInfo> FindInputOverrides(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        foreach (var t in types)
        {
            MethodInfo? m = null;
            try
            {
                if (t == null || t.ContainsGenericParameters || !typeof(Node).IsAssignableFrom(t)) continue;
                m = t.GetMethod("_Input", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(InputEvent) }, null);
            }
            catch (Exception) { /* ambiguous / unloadable type */ }
            if (m != null && !m.IsAbstract) yield return m;
        }
    }

    private static void TryPatch(Harmony harmony, MethodInfo? method, HarmonyMethod prefix)
    {
        if (method == null) return;
        try
        {
            harmony.Patch(method, prefix: prefix);
            PatchedCount++;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"UI input guard: could not patch {method.DeclaringType?.Name}.{method.Name}: {ex.Message}");
        }
    }

    // Prefixes take the event as "__0" (first argument by position): the parameter is called "inputEvent" in most
    // game classes but "input" in a few, and positional binding avoids the per-call allocation of "__args".

    /// <summary>Hotkey dispatch: skip keyboard / action events while the user types in the overlay.</summary>
    private static bool HotkeyPrefix(InputEvent __0)
    {
        try
        {
            if (__0 is not (InputEventKey or InputEventAction)) return true;
            return !UiKit.IsTyping;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Game <c>_Input</c> overrides: skip typing keys and presses over the overlay.</summary>
    private static bool InputPrefix(InputEvent __0)
    {
        try
        {
            switch (__0)
            {
                case InputEventKey:
                    return !UiKit.IsTyping;
                case InputEventMouseButton { Pressed: true } button:
                    return !Overlay.IsOverUi(button.Position);
                case InputEventGesture gesture:
                    return !Overlay.IsOverUi(gesture.Position);
                default:
                    return true;
            }
        }
        catch (Exception) { return true; }
    }
}
