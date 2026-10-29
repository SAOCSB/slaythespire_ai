using System.Text.Json.Nodes;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench.Tests;

/// <summary>Scoring through the real DecisionEngine with scripted model answers.</summary>
public class ScoringTests
{
    private static Task<AttemptResult> Run(BenchFixture f, params string[] answers) =>
        TestSupport.Runner(new ScriptedClient(answers)).RunAsync(f, CancellationToken.None);

    [Theory]
    [InlineData("a0")]
    [InlineData("A0")]
    [InlineData("0")]
    public async Task Expected_action_passes(string answer)
    {
        var r = await Run(TestSupport.Combat(), TestSupport.Answer(answer));
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Empty(r.Problems);
        Assert.Equal("a0", r.Chosen!.Id);
        Assert.Equal(1, r.LlmCalls);
        Assert.Equal(1000, r.InputTokens);
        Assert.Equal(800, r.CachedTokens);
    }

    [Fact]
    public async Task Other_action_fails_and_says_why()
    {
        var r = await Run(TestSupport.Combat(), TestSupport.Answer("a2"));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains(r.Problems, p => p.Contains("play:DEFEND_IRONCLAD+0@-") && p.Contains("expected"));
    }

    [Fact]
    public async Task Forbidden_action_is_reported_as_forbidden()
    {
        var r = await Run(TestSupport.Combat(), TestSupport.Answer("a4"));
        Assert.Equal(Verdict.Forbidden, r.Verdict);
        Assert.Contains(r.Problems, p => p.Contains("forbidden end_turn"));
    }

    [Fact]
    public async Task Expectations_match_ids_keys_and_key_prefixes()
    {
        var byId = TestSupport.Combat();
        byId.ExpectAny = new() { "a3" };
        Assert.Equal(Verdict.Pass, (await Run(byId, TestSupport.Answer("a3"))).Verdict);

        var byPrefix = TestSupport.Combat();
        byPrefix.ExpectAny = new() { "play:STRIKE_IRONCLAD+0@*" };
        Assert.Equal(Verdict.Pass, (await Run(byPrefix, TestSupport.Answer("a1"))).Verdict);
        Assert.Equal(Verdict.Fail, (await Run(byPrefix, TestSupport.Answer("a3"))).Verdict);

        var anything = TestSupport.Combat();
        anything.ExpectAny.Clear();
        Assert.Equal(Verdict.Pass, (await Run(anything, TestSupport.Answer("a2"))).Verdict);
        Assert.Equal(Verdict.Forbidden, (await Run(anything, TestSupport.Answer("a4"))).Verdict);
    }

    [Fact]
    public async Task Plan_longer_than_allowed_fails()
    {
        var f = TestSupport.Combat();
        f.MaxPlanLength = 2;
        // Strike, Defend, Strike are all predictable card plays: the parser keeps the whole plan.
        var r = await Run(f, TestSupport.Answer("a0", "a2", "a1"));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains(r.Problems, p => p.Contains("3 actions"));
        Assert.Equal(Verdict.Pass, (await Run(TestSupport.Combat().WithPlanLimit(2), TestSupport.Answer("a0", "a2"))).Verdict);
    }

    [Fact]
    public async Task Repair_turn_counts_against_max_llm_calls()
    {
        var f = TestSupport.Combat();
        f.MaxLlmCalls = 1;
        var r = await Run(f, TestSupport.Answer("a42"), TestSupport.Answer("a0"));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal(2, r.LlmCalls);
        Assert.Contains(r.Problems, p => p.Contains("2 model calls"));
        Assert.Equal("a0", r.Chosen!.Id);
        Assert.Contains(r.Warnings, w => w.Contains("Unusable LLM answer"));
    }

    [Fact]
    public async Task Unusable_answers_are_errors_not_fallback_answers()
    {
        var r = await Run(TestSupport.Combat(), "I would play Strike.", "{\"thought\":\"x\",\"actions\":[]}");
        Assert.Equal(Verdict.Error, r.Verdict);
        Assert.Null(r.Decision);
        Assert.Equal(2, r.LlmCalls);
        Assert.Contains(r.Problems, p => p.Contains("usable decision"));
        Assert.Equal(2, r.Answers.Count);
    }

    [Fact]
    public async Task Expected_message_kind_and_reply_are_checked()
    {
        var f = TestSupport.Combat(multiplayer: true);
        f.ExpectMessageKind = new() { "accept" };
        f.ExpectReplyTo = "m1";

        Assert.Equal(Verdict.Pass, (await Run(f, TestSupport.AnswerWithMessage("a0", "accept", "m1"))).Verdict);
        // Models often echo the id the way the prompt shows it.
        Assert.Equal(Verdict.Pass, (await Run(f, TestSupport.AnswerWithMessage("a0", "accept", "[m1]"))).Verdict);

        var noReply = await Run(f, TestSupport.Answer("a0"));
        Assert.Equal(Verdict.Fail, noReply.Verdict);
        Assert.Contains(noReply.Problems, p => p.Contains("no accept message"));

        var declined = await Run(f, TestSupport.AnswerWithMessage("a0", "decline", "m1"));
        Assert.Equal(Verdict.Fail, declined.Verdict);

        var wrongTarget = await Run(f, TestSupport.AnswerWithMessage("a0", "accept", "zzz"));
        Assert.Equal(Verdict.Fail, wrongTarget.Verdict);
        Assert.Contains(wrongTarget.Problems, p => p.Contains("replies to m1"));
    }

    [Fact]
    public async Task Forbidden_message_kinds_and_message_limits_are_checked()
    {
        var f = TestSupport.Combat(multiplayer: true);
        f.ForbidMessageKind = new() { "accept" };
        Assert.Equal(Verdict.Fail, (await Run(f, TestSupport.AnswerWithMessage("a0", "accept", "m1"))).Verdict);
        Assert.Equal(Verdict.Pass, (await Run(f, TestSupport.AnswerWithMessage("a0", "decline", "m1"))).Verdict);

        var quiet = TestSupport.Combat(multiplayer: true);
        quiet.MaxMessages = 0;
        var chatty = await Run(quiet, TestSupport.AnswerWithMessage("a0", "chat", null));
        Assert.Equal(Verdict.Fail, chatty.Verdict);
        Assert.Contains(chatty.Problems, p => p.Contains("1 team messages"));
        Assert.Equal(Verdict.Pass, (await Run(quiet, TestSupport.Answer("a0"))).Verdict);
    }

    [Fact]
    public async Task Team_inbox_reaches_the_prompt_and_messages_follow_engine_rules()
    {
        var client = new ScriptedClient(TestSupport.AnswerWithMessage("a0", "accept", "m1"));
        var r = await TestSupport.Runner(client).RunAsync(TestSupport.Combat(multiplayer: true), CancellationToken.None);
        Assert.Contains("[m1] Mira (AI) → you | request_debuff", client.Requests[0].Messages[0].Content);
        Assert.Single(r.Decision!.Messages);

        // Single-player: the engine drops team messages, as the mod does.
        var solo = await Run(TestSupport.Combat(), TestSupport.AnswerWithMessage("a0", "chat", null));
        Assert.Empty(solo.Decision!.Messages);
    }

    [Fact]
    public async Task Llm_fixture_decided_without_the_model_fails()
    {
        // Only one legal action: the engine decides "trivially" and never asks the model.
        var f = TestSupport.Combat().With(s => s.Actions.RemoveRange(0, 4));
        f.With(s => s.Actions[0].Id = "a0");
        f.ExpectAny = new() { "end_turn" };
        f.Forbid.Clear();
        var r = await Run(f, TestSupport.Answer("a0"));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains(r.Problems, p => p.Contains("without asking the model"));
    }

    [Fact]
    public async Task Rule_fixture_fails_when_the_rule_does_not_fire()
    {
        var f = TestSupport.LoadFixture("rule-pinned-route.json");
        Assert.Equal(Verdict.Pass, (await TestSupport.Runner(new ForbiddenClient()).RunAsync(f, CancellationToken.None)).Verdict);

        // Without the pinned path the engine asks the model: a rule fixture must flag that.
        f.Settings = null;
        var r = await Run(f, TestSupport.Answer("a1"));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Contains(r.Problems, p => p.Contains("expected Rule"));
        Assert.Contains(r.Problems, p => p.Contains("model was called"));
    }

    [Fact]
    public async Task Live_instruction_beats_the_pinned_route_and_reaches_the_model()
    {
        var client = new ScriptedClient(TestSupport.Answer("a1"));
        var f = TestSupport.LoadFixture("owner-instruction-overrides-pin.json");
        var r = await TestSupport.Runner(client).RunAsync(f, CancellationToken.None);
        Assert.Equal(Verdict.Pass, r.Verdict);
        var prompt = client.Requests.Single().Messages[0].Content;
        Assert.Contains("[OWNER INSTRUCTION]", prompt);
        Assert.Contains("its next node is 1,4", prompt);
    }

    [Fact]
    public async Task Memo_history_and_fixture_settings_reach_the_prompt()
    {
        var f = TestSupport.Combat();
        f.Memo = "boss is Vantom";
        f.History = new() { "F3 R1: Play Bash → Cultist" };
        f.Language = "zh";
        f.Settings = new JsonObject { ["route"] = new JsonObject { ["preset"] = "Aggressive" }, ["team"] = new JsonObject { ["allowGivingPotions"] = false } };
        var client = new ScriptedClient(TestSupport.Answer("a0"));
        await TestSupport.Runner(client).RunAsync(f, CancellationToken.None);
        var request = client.Requests.Single();
        Assert.Contains("[MEMO] (your own notes, possibly stale) boss is Vantom", request.Messages[0].Content);
        Assert.Contains("- F3 R1: Play Bash → Cultist", request.Messages[0].Content);
        Assert.Contains("Route style: aggressive", request.SystemSuffix);
        Assert.Contains("Never use your potions on teammates.", request.SystemSuffix);
        Assert.Contains("Team message language: Simplified Chinese", request.SystemSuffix);
        Assert.Contains("Ironclad:", request.SystemSuffix);
        Assert.Equal(PromptBuilder.CoreSystem, request.System);
    }

    [Fact]
    public async Task Dry_run_builds_the_prompt_but_scores_only_rule_fixtures()
    {
        var dry = TestSupport.Runner(new DryRunClient(), dryRun: true);
        var r = await dry.RunAsync(TestSupport.Combat(), CancellationToken.None);
        Assert.Equal(Verdict.DryRun, r.Verdict);
        Assert.Equal(0, r.LlmCalls);
        Assert.Single(r.Requests);
        Assert.Contains("a4 | End turn", r.Requests[0].Messages[0].Content);

        var rule = await dry.RunAsync(TestSupport.LoadFixture("rule-character-policy.json"), CancellationToken.None);
        Assert.Equal(Verdict.Pass, rule.Verdict);
        Assert.Empty(rule.Requests);
    }

    [Fact]
    public async Task Each_attempt_starts_from_fresh_memory_and_inbox()
    {
        // The first attempt leaves a plan, a memo and handled inbox messages behind; the second must not see them.
        var client = new ScriptedClient(
            "{\"thought\":\"t\",\"actions\":[\"a0\",\"a2\"],\"memo\":\"remember me\"}",
            TestSupport.Answer("a0"));
        var runner = TestSupport.Runner(client);
        var f = TestSupport.Combat(multiplayer: true);
        await runner.RunAsync(f, CancellationToken.None);
        var second = await runner.RunAsync(f, CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, second.Decision!.Source);
        Assert.Equal(2, client.Requests.Count);
        Assert.DoesNotContain("remember me", client.Requests[1].Messages[0].Content);
        Assert.Contains("[m1]", client.Requests[1].Messages[0].Content);
    }

    [Fact]
    public void Summary_status_reflects_repeats()
    {
        var s = new FixtureSummary(TestSupport.Combat());
        s.Attempts.Add(new AttemptResult { Verdict = Verdict.Pass });
        s.Attempts.Add(new AttemptResult { Verdict = Verdict.Fail });
        Assert.Equal("FLAKY 1/2", s.Status);
        s.Attempts[0].Verdict = Verdict.Forbidden;
        Assert.Equal("FORBIDDEN 0/2", s.Status);
        s.Attempts.RemoveAt(1);
        s.Attempts[0].Verdict = Verdict.Pass;
        Assert.Equal("PASS", s.Status);
    }

    [Fact]
    public void Table_columns_count_cjk_as_double_width()
    {
        Assert.Equal(10, BenchReport.Width(BenchReport.Fit("打出 打击", 10)));
        Assert.Equal(10, BenchReport.Width(BenchReport.Fit("打出 打击 → 小啃怪 (6 伤害)", 10)));
        Assert.EndsWith("…", BenchReport.Fit("abcdefghijklmnop", 8).TrimEnd());
        Assert.Equal("abc   ", BenchReport.Fit("abc", 6));
    }

    [Fact]
    public async Task Markdown_report_lists_failures_with_the_models_thought()
    {
        var pass = new FixtureSummary(TestSupport.Combat());
        pass.Attempts.Add(await Run(TestSupport.Combat(), TestSupport.Answer("a0")));
        var fail = new FixtureSummary(TestSupport.Combat());
        fail.Attempts.Add(await Run(TestSupport.Combat(), "{\"thought\":\"block | safe\",\"actions\":[\"a2\"]}"));
        var md = BenchReport.Markdown(new[] { pass, fail }, new BenchRunInfo { Model = "scripted", Profile = "p", Endpoint = "localhost" });
        Assert.Contains("Passed 1/2 fixtures", md);
        Assert.Contains("## Not passed", md);
        Assert.Contains("block \\| safe", md);
        Assert.Contains("`play:DEFEND_IRONCLAD+0@-`", md);
    }
}

internal static class FixtureTestExtensions
{
    public static BenchFixture WithPlanLimit(this BenchFixture f, int max)
    {
        f.MaxPlanLength = max;
        return f;
    }
}
