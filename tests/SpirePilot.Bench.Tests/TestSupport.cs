using System.Text.Json;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench.Tests;

/// <summary>Answers with prepared texts, in order, and records every request.</summary>
internal sealed class ScriptedClient : ILlmClient
{
    private readonly Queue<Func<LlmRequest, string>> _answers;
    public List<LlmRequest> Requests { get; } = new();
    public ScriptedClient(params string[] answers) => _answers = new(answers.Select<string, Func<LlmRequest, string>>(a => _ => a));
    public ScriptedClient(params Func<LlmRequest, string>[] answers) => _answers = new(answers);
    public string Describe => "scripted";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_answers.Count == 0) throw new LlmException("no more scripted answers");
        return Task.FromResult(new LlmResponse { Text = _answers.Dequeue()(request), InputTokens = 1000, OutputTokens = 50, CachedInputTokens = 800 });
    }
}

/// <summary>Fails the test if the engine ever asks the model.</summary>
internal sealed class ForbiddenClient : ILlmClient
{
    public int Calls { get; private set; }
    public string Describe => "must not be called";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("the model must not be called for this fixture");
    }
}

internal static class TestSupport
{
    /// <summary>Repository root (the directory holding SpirePilot.sln).</summary>
    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "SpirePilot.sln"))) return dir.FullName;
            throw new DirectoryNotFoundException("SpirePilot.sln not found above " + AppContext.BaseDirectory);
        }
    }

    public static string FixturesDir => Path.Combine(RepoRoot, "tools", "SpirePilot.Bench", "fixtures");

    public static IEnumerable<object[]> FixtureFiles() =>
        FixtureLoader.Files(FixturesDir).Select(f => new object[] { Path.GetFileName(f) });

    public static BenchFixture LoadFixture(string fileName) => FixtureLoader.Load(Path.Combine(FixturesDir, fileName));

    public static PilotSettings Settings()
    {
        var s = new PilotSettings();
        s.Profiles[0].ApiKey = "test-key";
        s.Normalize();
        return s;
    }

    public static BenchRunner Runner(ILlmClient client, PilotSettings? settings = null, bool dryRun = false) =>
        new(settings ?? Settings(), _ => client, dryRun);

    /// <summary>
    /// Small combat: Strike → Cultist (a0), Strike → Louse (a1), Defend (a2), Bash → Cultist (a3), End turn (a4).
    /// Expects a0, forbids end_turn unless the caller changes it.
    /// </summary>
    public static BenchFixture Combat(bool multiplayer = false)
    {
        var s = new GameSnapshot
        {
            Sequence = 7,
            Screen = ScreenKind.Combat,
            InRun = true,
            CanAct = true,
            IsMultiplayer = multiplayer,
            LocalPlayerId = 1,
            Run = new RunInfo { Act = 1, Floor = 3 },
            Players = { new PlayerInfo { NetId = 1, Name = "Me", CharacterId = "IRONCLAD", IsLocal = true, Hp = 50, MaxHp = 80, Energy = 3, MaxEnergy = 3, PotionSlots = 3 } },
            Combat = new CombatInfo
            {
                Round = 2,
                IsPlayerTurn = true,
                Hand =
                {
                    new CardInfo { Key = "h0", Id = "STRIKE_IRONCLAD", Name = "Strike", Type = "Attack", Cost = 1, Target = "AnyEnemy", Description = "Deal 6 damage." },
                    new CardInfo { Key = "h1", Id = "DEFEND_IRONCLAD", Name = "Defend", Type = "Skill", Cost = 1, Target = "Self", Description = "Gain 5 Block." },
                    new CardInfo { Key = "h2", Id = "BASH", Name = "Bash", Type = "Attack", Cost = 2, Target = "AnyEnemy", Description = "Deal 8 damage. Apply 2 Vulnerable." },
                },
                Enemies =
                {
                    new CreatureInfo { Key = "e0", Id = "CULTIST", Name = "Cultist", Hp = 40, MaxHp = 48, Intents = { new IntentInfo { Type = "Attack", Damage = 9, Hits = 1 } } },
                    new CreatureInfo { Key = "e1", Id = "LOUSE", Name = "Louse", Hp = 8, MaxHp = 12 },
                },
            },
        };
        if (multiplayer)
            s.Players.Add(new PlayerInfo { NetId = 2, Name = "Mira", CharacterId = "SILENT", Hp = 30, MaxHp = 70, IsAiControlled = true, HasTeamLink = true });
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.PlayCard, Label = "Play Strike → Cultist (6 dmg)", SemanticKey = "play:STRIKE_IRONCLAD+0@E:CULTIST#0", Args = { ["index"] = "0", ["target"] = "e0" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.PlayCard, Label = "Play Strike → Louse (6 dmg)", SemanticKey = "play:STRIKE_IRONCLAD+0@E:LOUSE#0", Args = { ["index"] = "0", ["target"] = "e1" } });
        s.Actions.Add(new ActionOption { Id = "a2", Kind = ActionKind.PlayCard, Label = "Play Defend (5 block)", SemanticKey = "play:DEFEND_IRONCLAD+0@-", Args = { ["index"] = "1" } });
        s.Actions.Add(new ActionOption { Id = "a3", Kind = ActionKind.PlayCard, Label = "Play Bash → Cultist (8 dmg, +2 Vulnerable)", SemanticKey = "play:BASH+0@E:CULTIST#0", Args = { ["index"] = "2", ["target"] = "e0" } });
        s.Actions.Add(new ActionOption { Id = "a4", Kind = ActionKind.EndTurn, Label = "End turn", SemanticKey = "end_turn" });

        var fixture = new BenchFixture
        {
            Id = "test-combat",
            Rationale = "test",
            ExpectAny = { "play:STRIKE_IRONCLAD+0@E:CULTIST#0" },
            Forbid = { "end_turn" },
            Snapshot = JsonSerializer.SerializeToElement(s, FixtureLoader.JsonOptions),
        };
        if (multiplayer)
            fixture.Inbox.Add(new TeamMessage { Id = "m1", Kind = TeamMessageKind.RequestDebuff, From = 2, To = 1, FromName = "Mira", FromAi = true, Text = "Vulnerable on Cultist please", Debuff = "Vulnerable", Target = "Cultist" });
        return fixture;
    }

    /// <summary>Replaces the snapshot after editing it.</summary>
    public static BenchFixture With(this BenchFixture f, Action<GameSnapshot> edit)
    {
        var s = f.CreateSnapshot();
        edit(s);
        f.Snapshot = JsonSerializer.SerializeToElement(s, FixtureLoader.JsonOptions);
        return f;
    }

    public static string Answer(params string[] actions) =>
        JsonSerializer.Serialize(new { thought = "t", actions });

    public static string AnswerWithMessage(string action, string type, string? replyTo) =>
        JsonSerializer.Serialize(new { thought = "t", actions = new[] { action }, messages = new[] { new { to = "all", type, text = "ok", reply_to = replyTo } } });
}
