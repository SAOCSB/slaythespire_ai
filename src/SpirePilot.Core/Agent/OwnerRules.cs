using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;

namespace SpirePilot.Core.Agent;

/// <summary>
/// The owner's hard rules as menu filters, applied before any model — the built-in one or an external agent through
/// the HTTP API / MCP — sees the menu: only allowed characters, the owner's ascension, no continuing a saved run of a
/// disallowed character, no potions for teammates when giving them is off. Never throws; works on the snapshot's lists.
/// </summary>
public static class OwnerRules
{
    /// <summary>A copy of <paramref name="s"/> with the owner's rules applied (for the HTTP API / MCP); the original is untouched.</summary>
    public static GameSnapshot Filtered(GameSnapshot s, PilotSettings settings)
    {
        var copy = s.WithOwnMenu();
        Filter(copy, settings);
        return copy;
    }

    public static void Filter(GameSnapshot s, PilotSettings settings)
    {
        if (s.Screen == ScreenKind.CharacterSelect) FilterCharacterSelect(s, settings);

        if (s.Screen == ScreenKind.MainMenu && settings.Characters.Allowed.Count > 0 && s.Actions.Any(a => a.Kind == ActionKind.ContinueRun) &&
            (s.SavedRun is not { CharacterId.Length: > 0 } saved || !settings.Characters.Allowed.Contains(saved.CharacterId, StringComparer.OrdinalIgnoreCase)))
        {
            // Continuing would (or might: the save could not be read) play a character the owner did not allow;
            // abandoning the save is the human's call. The restriction is never silently widened.
            s.Actions.RemoveAll(a => a.Kind == ActionKind.ContinueRun);
            s.Notes.Add(L10n.T("status.saved_run_not_allowed", s.SavedRun?.CharacterName ?? s.SavedRun?.CharacterId ?? "?"));
        }

        if (!settings.Team.AllowGivingPotions)
            s.Actions.RemoveAll(a => a.Kind == ActionKind.UsePotion && DecisionEngine.TargetsTeammate(a, s));
    }

    /// <summary>
    /// Only allowed characters can be selected; embarking needs an allowed character at the owner's ascension; the
    /// only ascension action left is the one that sets the owner's level.
    /// </summary>
    private static void FilterCharacterSelect(GameSnapshot s, PilotSettings settings)
    {
        if (s.CharacterSelect is not { Characters.Count: > 0 } cs)
        {
            // Without the character list the restriction cannot be checked: never select or embark blindly.
            s.Actions.RemoveAll(a => a.Kind is ActionKind.SelectCharacter or ActionKind.Embark or ActionKind.SetAscension);
            return;
        }
        var allowed = CharacterPicker.AllowedIds(cs, settings);
        s.Actions.RemoveAll(a => a.Kind == ActionKind.SelectCharacter && !allowed.Contains(a.Arg(ActionArgs.Character) ?? "", StringComparer.OrdinalIgnoreCase));
        var current = cs.Characters.FirstOrDefault(c => c.Selected);
        if (current == null || !allowed.Contains(current.Id, StringComparer.OrdinalIgnoreCase))
        {
            s.Actions.RemoveAll(a => a.Kind is ActionKind.Embark or ActionKind.SetAscension);
            return;
        }
        int wanted = CharacterPicker.DesiredAscension(cs, settings);
        bool canSet = s.Actions.Any(a => a.Kind == ActionKind.SetAscension && a.IntArg(ActionArgs.Ascension) == wanted);
        // The owner sets the ascension: only the step to the owner's level stays, and embarking waits for it
        // (when the game lets this player set it at all — a co-op client cannot).
        s.Actions.RemoveAll(a => a.Kind == ActionKind.SetAscension && (cs.Ascension == wanted || a.IntArg(ActionArgs.Ascension) != wanted));
        if (cs.Ascension != wanted && canSet) s.Actions.RemoveAll(a => a.Kind == ActionKind.Embark);
    }
}
