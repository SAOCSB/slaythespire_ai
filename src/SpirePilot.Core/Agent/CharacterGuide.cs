namespace SpirePilot.Core.Agent;

/// <summary>Short strategy notes per character, added to the cached owner block for the current run.</summary>
public static class CharacterGuide
{
    public static string? For(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return null;
        var id = characterId.Trim().ToUpperInvariant();
        foreach (var (key, text) in Guides)
            if (id.Contains(key)) return text;
        return null;
    }

    private static readonly (string key, string text)[] Guides =
    {
        ("IRONCLAD", """
            Ironclad: Strength scaling, Exhaust synergies, cards that trade HP for power. Burning Blood heals 6 HP after
            each won combat, so small chip damage in hallway fights is fine. Lead with Bash (2 Vulnerable), then attacks.
            Value Strength multipliers and card draw. Remove Strikes first.
            """),
        ("SILENT", """
            Silent: Poison ticks at the start of the victim's turn (enemies before they act), ignores block, then drops
            by 1 - great against high-HP bosses. Shivs: 0-cost Exhaust attacks. Sly cards discarded by an effect during
            your turn are played for free (not at end of turn): discard them rather than play them. Weak on attackers is
            your main defense. Keep the deck lean.
            """),
        ("DEFECT", """
            Defect: orb passives fire at your turn end, before enemies act (Plasma: energy at turn start). Lightning hits a
            random enemy, passive and evoke; Frost blocks; Dark grows; Glass hits all enemies, weaker each turn. Shown orb
            values include Focus (mostly temporary). Orb slots scale the engine. Count Lightning toward lethal only on the
            last enemy. Zap+ (0 cost) is a strong early upgrade.
            """),
        ("NECROBINDER", """
            Necrobinder: Osty takes attack damage that gets past your block, up to his HP (not Poison or Doom). His HP
            stays between turns; Summon revives him or raises his Max HP for the fight; Osty attack cards need him alive.
            Doom >= HP kills a creature when its own turn ends, ignoring block - enemies still attack first; your own Doom
            (Borrowed Time, Neurosurge) can kill you. Play Ethereal cards the turn you draw them.
            """),
        ("REGENT", """
            Regent: Stars carry over between turns but not between fights: bank them for a big star card, never end a
            fight holding them. Forge creates or grows the Sovereign Blade (Retain) for the whole combat: forge early,
            swing it on high-value turns. Falling Star (0 energy, 2 Stars; Weak + Vulnerable) is a strong opener.
            """),
    };
}
