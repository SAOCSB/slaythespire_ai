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
            Ironclad: strength scaling, Exhaust synergies, cards that trade HP for power. Burning Blood heals after each
            fight, so cheap chip damage in hallway fights is acceptable. Lead with Bash / Vulnerable, then attacks. Value
            Strength multipliers and card draw. Remove Strikes first.
            """),
        ("SILENT", """
            Silent: Poison (ticks every turn and ignores block — great against high-HP bosses), Shivs (0-cost tokens),
            discard synergies (Sly cards pay off when discarded). Weak on attackers is your main defense. Keep the deck
            lean so key cards show up; draw and discard engines carry runs.
            """),
        ("DEFECT", """
            Defect: orbs — Lightning hits (random target passively), Frost blocks, Dark charges, Plasma gives energy. Focus
            scales every orb; extra orb slots scale the engine. Plan exact lethal with evoke math and never count random
            Lightning passives on a specific target. Zap+ (0 cost) is a strong early upgrade. Avoid bloating the deck.
            """),
        ("NECROBINDER", """
            Necrobinder: your companion Osty takes unblocked damage aimed at you; Summon grows it. Treat Osty's HP as extra
            block and don't let it die needlessly; "your Osty deals N" cards scale with it alive. Doom: an enemy dies at
            the end of its turn when HP <= Doom — stack it on big targets and count it in lethal math. Play Ethereal cards
            the turn you draw them.
            """),
        ("REGENT", """
            Regent: Stars are a second resource for star-cost cards — spend them, don't hoard. Forge creates / empowers the
            Sovereign Blade (retained); keep forging and swing it on high-value turns. Falling Star (Weak + Vulnerable)
            is a strong opener.
            """),
    };
}
