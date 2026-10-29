using SpirePilot.Core.Model;

namespace SpirePilot.Core.Tests;

internal static class TestData
{
    public static GameSnapshot Combat()
    {
        var s = new GameSnapshot
        {
            Sequence = 7,
            Screen = ScreenKind.Combat,
            InRun = true,
            CanAct = true,
            LocalPlayerId = 1,
            Run = new RunInfo { Act = 1, Floor = 3 },
            Players =
            {
                new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 50, MaxHp = 80, Energy = 3, MaxEnergy = 3, PotionSlots = 3 },
            },
            Combat = new CombatInfo
            {
                Round = 2,
                IsPlayerTurn = true,
                Hand =
                {
                    new CardInfo { Key = "h0", Name = "Strike", Type = "Attack", Cost = 1, Target = "AnyEnemy", Description = "Deal [gold]6[/gold] damage." },
                    new CardInfo { Key = "h1", Name = "Defend", Type = "Skill", Cost = 1, Target = "Self", Description = "Gain 5 Block." },
                    new CardInfo { Key = "h2", Name = "Strike", Type = "Attack", Cost = 1, Target = "AnyEnemy", Description = "Deal 6 damage." },
                },
                Enemies =
                {
                    new CreatureInfo { Key = "e0", Name = "Cultist", Hp = 40, MaxHp = 48, Intents = { new IntentInfo { Type = "Attack", Damage = 9, Hits = 1 } } },
                    new CreatureInfo { Key = "e1", Name = "Louse", Hp = 8, MaxHp = 12 },
                },
            },
        };
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.PlayCard, Label = "Play Strike → Cultist", SemanticKey = "play|Strike|e0", Args = { ["index"] = "0", ["target"] = "e0" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.PlayCard, Label = "Play Strike → Louse", SemanticKey = "play|Strike|e1", Args = { ["index"] = "0", ["target"] = "e1" } });
        s.Actions.Add(new ActionOption { Id = "a2", Kind = ActionKind.PlayCard, Label = "Play Defend", SemanticKey = "play|Defend|", Args = { ["index"] = "1" } });
        s.Actions.Add(new ActionOption { Id = "a3", Kind = ActionKind.EndTurn, Label = "End turn", SemanticKey = "end_turn" });
        return s;
    }

    /// <summary>
    /// Small act map:
    ///   row0: 0,0 (M)            1,0 (M)
    ///   row1: 0,1 (E)            1,1 (R)
    ///   row2: 0,2 (R)            1,2 (M)
    ///   row3: 0,3 (Boss)
    /// edges: 0,0->0,1 ; 1,0->1,1 ; 0,1->0,2 ; 1,1->1,2 ; 0,2->0,3 ; 1,2->0,3
    /// </summary>
    public static MapInfo Map() => new()
    {
        Rows = 3,
        Reachable = { "0,0", "1,0" },
        Nodes =
        {
            new MapNodeInfo { Key = "0,0", Col = 0, Row = 0, Type = MapNodeType.Monster, Children = { "0,1" } },
            new MapNodeInfo { Key = "1,0", Col = 1, Row = 0, Type = MapNodeType.Monster, Children = { "1,1" } },
            new MapNodeInfo { Key = "0,1", Col = 0, Row = 1, Type = MapNodeType.Elite, Children = { "0,2" } },
            new MapNodeInfo { Key = "1,1", Col = 1, Row = 1, Type = MapNodeType.RestSite, Children = { "1,2" } },
            new MapNodeInfo { Key = "0,2", Col = 0, Row = 2, Type = MapNodeType.RestSite, Children = { "0,3" } },
            new MapNodeInfo { Key = "1,2", Col = 1, Row = 2, Type = MapNodeType.Monster, Children = { "0,3" } },
            new MapNodeInfo { Key = "0,3", Col = 0, Row = 3, Type = MapNodeType.Boss },
        },
    };
}
