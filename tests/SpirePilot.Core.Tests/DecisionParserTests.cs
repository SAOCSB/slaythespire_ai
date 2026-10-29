using SpirePilot.Core.Agent;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

public class DecisionParserTests
{
    [Fact]
    public void Parses_json_inside_code_fence_and_prose()
    {
        var text = "Sure! Here is my move:\n```json\n{\"thought\":\"kill the louse {first}\",\"actions\":[\"a1\"],\"messages\":[],\"memo\":\"\"}\n```";
        var r = DecisionParser.Parse(text, TestData.Combat());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { "a1" }, r.Decision!.ActionIds);
        Assert.Equal("kill the louse {first}", r.Decision.Thought);
    }

    [Theory]
    [InlineData("{\"thought\":\"\",\"actions\":[2]}", "a2")]
    [InlineData("{\"thought\":\"\",\"actions\":[\"A3\"]}", "a3")]
    [InlineData("{\"thought\":\"\",\"actions\":[\"a0: Play Strike\"]}", "a0")]
    [InlineData("{\"thought\":\"\",\"action\":\"a2\"}", "a2")]
    [InlineData("{\"thought\":\"\",\"actions\":[{\"id\":\"a1\"}]}", "a1")]
    public void Normalizes_ids(string json, string expected)
    {
        var r = DecisionParser.Parse(json, TestData.Combat());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expected, r.Decision!.FirstActionId);
    }

    [Fact]
    public void Rejects_unknown_first_action()
    {
        var r = DecisionParser.Parse("{\"thought\":\"x\",\"actions\":[\"a9\",\"a0\"]}", TestData.Combat());
        Assert.False(r.Ok);
        Assert.Contains("a9", r.Error);
    }

    [Fact]
    public void Plan_stops_at_first_unknown_id_and_records_semantic_keys()
    {
        var r = DecisionParser.Parse("{\"thought\":\"\",\"actions\":[\"a2\",\"a0\",\"zz\",\"a3\"]}", TestData.Combat());
        Assert.True(r.Ok);
        Assert.Equal(new[] { "a2", "a0" }, r.Decision!.ActionIds);
        Assert.Equal(new[] { "play|Strike|e0" }, r.Decision.PlanKeys);
    }

    [Fact]
    public void Parses_team_messages()
    {
        var json = """
        {"thought":"t","actions":["a3"],"messages":[
          {"to":"Bob","type":"request_debuff","text":"给邪教徒上易伤","debuff":"Vulnerable","target":"e0"},
          {"to":"all","type":"Accept","text":"ok","reply_to":"abc"},
          {"to":"all","type":"chat","text":"   "}
        ],"memo":"keep fire potion for boss"}
        """;
        var r = DecisionParser.Parse(json, TestData.Combat());
        Assert.True(r.Ok, r.Error);
        var d = r.Decision!;
        Assert.Equal(2, d.Messages.Count);
        Assert.Equal(TeamMessageKind.RequestDebuff, d.Messages[0].Kind);
        Assert.Equal("Vulnerable", d.Messages[0].Debuff);
        Assert.Equal("e0", d.Messages[0].Target);
        Assert.Equal(TeamMessageKind.Accept, d.Messages[1].Kind);
        Assert.Equal("abc", d.Messages[1].ReplyTo);
        Assert.Equal("keep fire potion for boss", d.Memo);
    }

    [Fact]
    public void Extracts_first_balanced_object_ignoring_braces_in_strings()
    {
        var text = "noise { not json } then {\"thought\":\"a } b\",\"actions\":[\"a0\"]} trailing";
        Assert.Equal("{\"thought\":\"a } b\",\"actions\":[\"a0\"]}", DecisionParser.ExtractJsonObject(text));
    }

    [Fact]
    public void Strips_reasoning_tags_from_local_models()
    {
        var r = DecisionParser.Parse("<think>maybe {\"actions\":[\"a9\"]}</think>{\"thought\":\"x\",\"actions\":[\"a1\"]}", TestData.Combat());
        Assert.True(r.Ok, r.Error);
        Assert.Equal("a1", r.Decision!.FirstActionId);
    }

    [Fact]
    public void Plan_ends_after_unpredictable_steps()
    {
        var snap = TestData.Combat();
        snap.Combat!.Hand[1].Description = "Gain 5 Block. Draw 1 card.";
        var r = DecisionParser.Parse("{\"thought\":\"\",\"actions\":[\"a2\",\"a0\",\"a3\"]}", snap);
        Assert.Equal(new[] { "a2" }, r.Decision!.ActionIds);
        snap.Actions[0].Unpredictable = true;
        r = DecisionParser.Parse("{\"thought\":\"\",\"actions\":[\"a1\",\"a0\"]}", snap);
        Assert.Equal(new[] { "a1", "a0" }, r.Decision!.ActionIds);
    }

    [Fact]
    public void Reports_missing_json()
    {
        var r = DecisionParser.Parse("I would play Strike.", TestData.Combat());
        Assert.False(r.Ok);
    }

    [Fact]
    public void Schema_is_valid_json()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(DecisionParser.Schema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
    }
}
