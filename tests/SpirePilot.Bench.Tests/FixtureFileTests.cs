using System.Text.Json;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench.Tests;

/// <summary>Every shipped fixture must load, be internally consistent and be passable through the real engine.</summary>
public class FixtureFileTests
{
    public static IEnumerable<object[]> Files() => TestSupport.FixtureFiles();

    [Theory]
    [MemberData(nameof(Files))]
    public void Fixture_deserializes_and_validates(string file)
    {
        var f = TestSupport.LoadFixture(file);
        Assert.Empty(FixtureValidator.Validate(f));
        Assert.False(string.IsNullOrWhiteSpace(f.Rationale));
        Assert.Equal(Path.GetFileNameWithoutExtension(file), f.Id);

        var s = f.CreateSnapshot();
        Assert.NotEmpty(s.Actions);
        for (int i = 0; i < s.Actions.Count; i++) Assert.Equal("a" + i, s.Actions[i].Id);
        foreach (var p in f.ExpectAny.Concat(f.Forbid))
            Assert.True(s.Actions.Any(a => BenchFixture.Matches(p, a)), $"{file}: \"{p}\" matches no menu action");
        Assert.NotNull(s.LocalPlayer);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Fixture_is_passable_with_a_correct_answer_and_catches_a_forbidden_one(string file)
    {
        var f = TestSupport.LoadFixture(file);
        if (f.IsRule)
        {
            var client = new ForbiddenClient();
            var r = await TestSupport.Runner(client).RunAsync(f, CancellationToken.None);
            Assert.True(r.Verdict == Verdict.Pass, $"{file}: {string.Join("; ", r.Problems)}");
            Assert.Equal(0, client.Calls);
            return;
        }

        var s = f.CreateSnapshot();
        var good = s.Actions.First(a => (f.ExpectAny.Count == 0 || f.ExpectAny.Any(p => BenchFixture.Matches(p, a))) && !f.Forbid.Any(p => BenchFixture.Matches(p, a)));
        var oracle = new ScriptedClient(OracleAnswer(f, good.Id));
        var pass = await TestSupport.Runner(oracle).RunAsync(f, CancellationToken.None);
        Assert.True(pass.Verdict == Verdict.Pass, $"{file}: {string.Join("; ", pass.Problems)}");
        Assert.Single(oracle.Requests);
        Assert.Contains("[MENU]", oracle.Requests[0].Messages[0].Content);

        var bad = s.Actions.FirstOrDefault(a => f.Forbid.Any(p => BenchFixture.Matches(p, a)));
        if (bad == null) return;
        var wrong = await TestSupport.Runner(new ScriptedClient(OracleAnswer(f, bad.Id))).RunAsync(f, CancellationToken.None);
        Assert.Equal(Verdict.Forbidden, wrong.Verdict);
    }

    [Fact]
    public void Fixture_set_is_large_unique_and_covers_the_required_situations()
    {
        var (fixtures, errors) = FixtureLoader.LoadAll(TestSupport.FixturesDir);
        Assert.Empty(errors);
        Assert.True(fixtures.Count >= 25, $"only {fixtures.Count} fixtures");
        Assert.Empty(FixtureValidator.ValidateSet(fixtures));

        var tags = fixtures.SelectMany(f => f.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[]
                 {
                     "combat", "map", "card_reward", "rest_site", "shop", "event", "coop", "rule", "owner_instruction",
                     "ironclad", "silent", "defect", "necrobinder", "regent", "zh", "en",
                 })
            Assert.Contains(required, tags);
        Assert.Contains(fixtures, f => f.IsRule && f.ExpectSource == DecisionSource.Rule);
        Assert.Contains(fixtures, f => f.ExpectMessageKind.Contains("accept"));
        Assert.Contains(fixtures, f => f.MaxMessages == 0 && f.Inbox.Any(m => m.FromAi && m.Kind == TeamMessageKind.Accept));
        Assert.Contains(fixtures, f => f.LiveInstruction != null);
    }

    [Fact]
    public void Chinese_fixtures_really_use_localized_names()
    {
        var (fixtures, _) = FixtureLoader.LoadAll(TestSupport.FixturesDir);
        foreach (var f in fixtures.Where(f => f.Language == "zh"))
        {
            var labels = string.Join(" ", f.CreateSnapshot().Actions.Select(a => a.Label));
            Assert.True(labels.Any(c => c >= '一' && c <= '鿿'), $"{f.Id} is tagged zh but its menu is not Chinese");
        }
    }

    [Fact]
    public void Snapshot_json_from_the_http_api_loads_including_computed_properties()
    {
        // GET /state output loads as a fixture as is (computed properties such as localPlayer are not serialized).
        var snap = TestSupport.Combat(multiplayer: true).CreateSnapshot();
        var apiJson = JsonSerializer.Serialize(snap, FixtureLoader.JsonOptions);
        Assert.DoesNotContain("\"localPlayer\"", apiJson);
        Assert.Contains("\"PlayCard\"", apiJson);
        var fixtureJson = $$"""{ "rationale": "r", "expect_any": ["a0"], "snapshot": {{apiJson}} }""";
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, fixtureJson);
            var f = FixtureLoader.Load(path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), f.Id);
            var back = f.CreateSnapshot();
            Assert.Equal(2, back.Players.Count);
            Assert.Equal(ActionKind.PlayCard, back.Actions[0].Kind);
            Assert.Equal("Me", back.LocalPlayer!.Name);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The answer a perfect model would give: the action plus whatever messages the fixture demands.</summary>
    private static string OracleAnswer(BenchFixture f, string actionId)
    {
        if (f.ExpectMessageKind.Count == 0) return TestSupport.Answer(actionId);
        return TestSupport.AnswerWithMessage(actionId, f.ExpectMessageKind[0], f.ExpectReplyTo ?? f.Inbox.FirstOrDefault()?.Id);
    }
}
