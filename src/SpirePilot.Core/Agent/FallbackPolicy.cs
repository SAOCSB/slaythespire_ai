using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;

namespace SpirePilot.Core.Agent;

/// <summary>
/// Simple rule-based player used when the LLM is unavailable, and for decisions that are obvious.
/// It never needs to be clever; it only keeps the run moving safely.
/// </summary>
public static class FallbackPolicy
{
    public static ActionOption? Choose(GameSnapshot s, PilotSettings settings)
    {
        // Taking back an ended turn is only ever done for a teammate, which needs the LLM.
        var actions = s.Actions.Where(a => a.Kind != ActionKind.UndoEndTurn).ToList();
        if (actions.Count == 0) return null;
        ActionOption? Of(ActionKind k) => actions.FirstOrDefault(a => a.Kind == k);

        switch (s.Screen)
        {
            case ScreenKind.Popup:
                return Of(ActionKind.DismissPopup) ?? Of(ActionKind.Proceed) ?? actions[0];

            case ScreenKind.Combat:
                return ChooseCombat(s) ?? Of(ActionKind.EndTurn) ?? actions[0];

            case ScreenKind.HandSelect:
            case ScreenKind.CardGridSelect:
            case ScreenKind.ChooseOne:
            {
                var sel = s.Selection;
                int selected = sel?.SelectedKeys.Count ?? 0;
                int min = sel?.MinSelect ?? 0;
                if (selected >= Math.Max(1, min) || (min == 0 && Of(ActionKind.ConfirmSelection) != null && selected > 0))
                    return Of(ActionKind.ConfirmSelection) ?? Of(ActionKind.Proceed) ?? actions[0];
                // Permanent deck changes the rules cannot judge: back out whenever the screen allows it.
                if ((min == 0 || sel?.CanCancel == true) && Of(ActionKind.CancelSelection) is { } cancel &&
                    sel?.Purpose is "Remove" or "Transform" or "DeckSelect" or "Enchant")
                    return cancel;
                // Nothing required: confirm right away rather than giving up a card.
                if (min == 0 && Of(ActionKind.ConfirmSelection) is { } done) return done;
                // Otherwise give up the least useful cards first (statuses and curses).
                var junk = actions.FirstOrDefault(a => a.Kind == ActionKind.SelectCard &&
                    sel?.Candidates.FirstOrDefault(c => c.Key == a.Arg(ActionArgs.Card)) is { Type: "Status" or "Curse" });
                return junk ?? Of(ActionKind.SelectCard) ?? Of(ActionKind.ChooseOption) ?? Of(ActionKind.ConfirmSelection) ?? actions[0];
            }

            case ScreenKind.Map:
            {
                if (s.Map is { } map)
                {
                    var me = s.LocalPlayer;
                    double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
                    var target = RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0, s.Run?.Seed)
                                 ?? RoutePlanner.BestNext(map, settings.Route, hp, me?.Gold ?? 0);
                    var pick = actions.FirstOrDefault(a => a.Kind == ActionKind.ChooseMapNode && a.Arg(ActionArgs.Node) == target);
                    if (pick != null) return pick;
                }
                return Of(ActionKind.ChooseMapNode) ?? actions[0];
            }

            case ScreenKind.CombatRewards:
            {
                // Take everything except cards (cards need judgement) and potions that cannot fit (a full belt swallows the click).
                bool beltFull = s.LocalPlayer is { } p && p.PotionSlots > 0 && p.Potions.Count >= p.PotionSlots;
                return actions.FirstOrDefault(a => a.Kind == ActionKind.ClaimReward && !IsCardReward(a, s) && !(beltFull && IsKind(a, s, "Potion")))
                       ?? Of(ActionKind.Proceed) ?? actions[0];
            }

            case ScreenKind.CardReward:
                return Of(ActionKind.SkipCardReward) ?? Of(ActionKind.Proceed) ?? actions[0];

            case ScreenKind.Shop:
                return Of(ActionKind.Proceed) ?? actions[0];

            case ScreenKind.Event:
                return SafeEventOption(s) ?? Of(ActionKind.Proceed) ?? actions[0];

            case ScreenKind.RestSite:
            {
                var me = s.LocalPlayer;
                bool low = me is { MaxHp: > 0 } && me.Hp < me.MaxHp * 0.5;
                var rest = actions.Where(a => a.Kind == ActionKind.ChooseRestOption).ToList();
                ActionOption? Named(string id) => rest.FirstOrDefault(a => (a.Arg("id") ?? a.Label).Contains(id, StringComparison.OrdinalIgnoreCase));
                return (low ? Named("HEAL") ?? Named("REST") : Named("SMITH") ?? Named("UPGRADE"))
                       ?? rest.FirstOrDefault() ?? Of(ActionKind.Proceed) ?? actions[0];
            }

            case ScreenKind.Treasure:
                return Of(ActionKind.OpenChest) ?? Of(ActionKind.ClaimReward) ?? Of(ActionKind.Proceed) ?? actions[0];

            case ScreenKind.GameOver:
                return Of(ActionKind.Proceed) ?? Of(ActionKind.ReturnToMenu) ?? actions[0];

            case ScreenKind.MainMenu:
                return Of(ActionKind.ContinueRun) ?? Of(ActionKind.StartNewRun) ?? actions[0];

            case ScreenKind.CharacterSelect:
                return CharacterPicker.Choose(s, settings) ?? Of(ActionKind.Embark) ?? actions[0];

            default:
                return Of(ActionKind.Proceed) ?? Of(ActionKind.DismissPopup) ?? actions[0];
        }
    }

    private static readonly string[] CostWords =
        { "lose", "hp", "damage", "curse", "max hp", "失去", "生命", "伤害", "诅咒", "受到" };
    private static readonly string[] LeaveWords = { "leave", "continue", "ignore", "离开", "继续", "无视", "忽略" };

    /// <summary>
    /// Events without the LLM: never a lethal option; when hurt, prefer leaving or an option without an HP / curse
    /// cost; otherwise the first option.
    /// </summary>
    private static ActionOption? SafeEventOption(GameSnapshot s)
    {
        EventOptionInfo? Info(ActionOption a) => s.Event?.Options.FirstOrDefault(o => a.IntArg(ActionArgs.Index) == o.Index);
        var options = s.Actions.Where(a => a.Kind == ActionKind.ChooseEventOption && a.Arg("lethal") != "true" &&
                                           Info(a) is not { WillKill: true } &&
                                           !a.Label.Contains("LETHAL", StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Count == 0) return null;
        string Text(ActionOption a) => (a.Label + " " + Info(a)?.Description).ToLowerInvariant();
        var me = s.LocalPlayer;
        bool hurt = me is { MaxHp: > 0 } && me.Hp < me.MaxHp * 0.5;
        if (!hurt) return options[0];
        return options.FirstOrDefault(a => Info(a) is { IsProceed: true })
               ?? options.FirstOrDefault(a => LeaveWords.Any(w => Text(a).Contains(w)))
               ?? options.FirstOrDefault(a => !CostWords.Any(w => Text(a).Contains(w)))
               ?? options[^1];
    }

    private static bool IsCardReward(ActionOption a, GameSnapshot s) => IsKind(a, s, "Card");

    private static bool IsKind(ActionOption a, GameSnapshot s, string kind)
    {
        var idx = a.IntArg(ActionArgs.Index);
        var item = s.Rewards?.Items.FirstOrDefault(i => i.Index == idx);
        return item != null && item.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cards a blind policy must not play: they give you Doom, kill your Osty or end your turn (StS2 0.111 ids).</summary>
    private static readonly HashSet<string> SelfHarm = new(StringComparer.OrdinalIgnoreCase)
        { "BORROWED_TIME", "NEUROSURGE", "SACRIFICE", "BONE_SHARDS", "VOID_FORM" };

    /// <summary>Very small combat heuristic: lethal/cheap attacks on the weakest enemy, then block if attacked, then powers.</summary>
    private static ActionOption? ChooseCombat(GameSnapshot s)
    {
        var c = s.Combat;
        if (c == null || !c.IsPlayerTurn) return null;
        CardInfo? CardOf(ActionOption a) =>
            a.IntArg(ActionArgs.Index) is { } i && i >= 0 && i < c.Hand.Count ? c.Hand[i] : null;
        var plays = s.Actions.Where(a => a.Kind == ActionKind.PlayCard && !(CardOf(a) is { } card && SelfHarm.Contains(card.Id))).ToList();
        if (plays.Count == 0) return null;
        CreatureInfo? TargetOf(ActionOption a) => c.Enemies.FirstOrDefault(e => e.Key == a.Arg(ActionArgs.Target));

        int incoming = c.Enemies.Where(e => e.IsAlive).SelectMany(e => e.Intents).Sum(i => (i.Damage ?? 0) * Math.Max(1, i.Hits ?? 1));
        int block = s.LocalPlayer?.Block ?? 0;

        var powers = plays.Where(a => CardOf(a)?.Type == "Power").ToList();
        if (powers.Count > 0 && c.Round <= 2 && incoming <= block) return powers[0];

        if (incoming > block)
        {
            static bool GivesBlock(CardInfo card) => card.GainsBlock ||
                card.Type == "Skill" &&
                (card.Description.Contains("Block", StringComparison.OrdinalIgnoreCase) || card.Description.Contains("格挡"));
            var blockCard = plays.FirstOrDefault(a => CardOf(a) is { } card && GivesBlock(card));
            if (blockCard != null) return blockCard;
        }

        var attacks = plays.Where(a => CardOf(a)?.Type == "Attack")
            .OrderBy(a => TargetOf(a)?.Hp ?? int.MaxValue)
            .ThenBy(a => CardOf(a)?.Cost ?? 0)
            .ToList();
        if (attacks.Count > 0) return attacks[0];
        if (powers.Count > 0) return powers[0];
        // Avoid playing statuses/curses or anything unknown that might hurt; end turn instead.
        return plays.FirstOrDefault(a => CardOf(a)?.Type == "Skill");
    }
}

/// <summary>Applies the user's character restriction and pick policy on the character select screen.</summary>
public static class CharacterPicker
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Returns the SelectCharacter action that the policy wants (or Embark / SetAscension once the right character
    /// is selected). Returns null when no allowed character can be selected.
    /// </summary>
    public static ActionOption? Choose(GameSnapshot s, PilotSettings settings)
    {
        var cs = s.CharacterSelect;
        var selects = s.Actions.Where(a => a.Kind == ActionKind.SelectCharacter).ToList();
        if (cs == null) return selects.FirstOrDefault();

        var allowed = AllowedIds(cs, settings);
        var current = cs.Characters.FirstOrDefault(ch => ch.Selected);
        // The lobby usually starts with some character selected: the pick policy still decides which one plays.
        var wantedId = PickId(allowed, settings);
        if (wantedId != null && current != null && !string.Equals(current.Id, wantedId, StringComparison.OrdinalIgnoreCase) &&
            selects.FirstOrDefault(a => string.Equals(a.Arg(ActionArgs.Character), wantedId, StringComparison.OrdinalIgnoreCase)) is { } switchTo)
            return switchTo;
        if (current != null && allowed.Contains(current.Id, StringComparer.OrdinalIgnoreCase))
        {
            int wanted = DesiredAscension(cs, settings);
            if (wanted != cs.Ascension)
            {
                var asc = s.Actions.FirstOrDefault(a => a.Kind == ActionKind.SetAscension && a.IntArg(ActionArgs.Ascension) == wanted);
                if (asc != null) return asc;
            }
            return s.Actions.FirstOrDefault(a => a.Kind == ActionKind.Embark);
        }

        return selects.FirstOrDefault(a => string.Equals(a.Arg(ActionArgs.Character), wantedId, StringComparison.OrdinalIgnoreCase))
               ?? selects.FirstOrDefault(a => allowed.Contains(a.Arg(ActionArgs.Character) ?? "", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Unlocked characters the user allows: all unlocked ones when the allow-list is empty; otherwise only allowed ones
    /// (possibly none — the restriction is never silently widened).
    /// </summary>
    public static List<string> AllowedIds(CharacterSelectInfo cs, PilotSettings settings)
    {
        var unlocked = cs.Characters.Where(c => !c.Locked).Select(c => c.Id).ToList();
        if (settings.Characters.Allowed.Count == 0) return unlocked;
        return unlocked.Where(id => settings.Characters.Allowed.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static int DesiredAscension(CharacterSelectInfo cs, PilotSettings settings) =>
        settings.Characters.Ascension < 0 ? cs.MaxAscension : Math.Min(settings.Characters.Ascension, cs.MaxAscension);

    private static string? _randomPick;
    private static string _randomPickPool = "";

    /// <summary>Forget the random roll (called when the character select screen is left, so each visit rolls once).</summary>
    public static void ResetVisit()
    {
        lock (Rng) _randomPick = null;
    }

    private static string? PickId(List<string> allowed, PilotSettings settings)
    {
        if (allowed.Count == 0) return null;
        switch (settings.Characters.Policy)
        {
            case CharacterPickPolicy.First:
                return allowed[0];
            case CharacterPickPolicy.RoundRobin:
            {
                int i = ((settings.Characters.RoundRobinCursor % allowed.Count) + allowed.Count) % allowed.Count;
                return allowed[i];
            }
            case CharacterPickPolicy.AiChoice:
                return null;
            default:
            {
                // One roll per visit: re-rolling every tick would flip between characters forever.
                var pool = string.Join(",", allowed);
                lock (Rng)
                {
                    if (_randomPick == null || _randomPickPool != pool || !allowed.Contains(_randomPick))
                    {
                        _randomPick = allowed[Rng.Next(allowed.Count)];
                        _randomPickPool = pool;
                    }
                    return _randomPick;
                }
            }
        }
    }
}
