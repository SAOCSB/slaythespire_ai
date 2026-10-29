using SpirePilot.Core.Agent;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench;

/// <summary>Outcome of one attempt at a fixture.</summary>
public enum Verdict
{
    Pass,
    /// <summary>An expectation was not met (wrong action, plan too long, missing reply ...).</summary>
    Fail,
    /// <summary>actions[0] is one of the fixture's forbidden actions.</summary>
    Forbidden,
    /// <summary>No usable decision: API error, unparseable answers, timeout.</summary>
    Error,
    /// <summary>--dry-run: the prompt was built but no model was asked.</summary>
    DryRun,
}

/// <summary>Everything measured for one attempt.</summary>
public sealed class AttemptResult
{
    public Verdict Verdict { get; set; }
    /// <summary>Why the attempt did not pass (empty on pass).</summary>
    public List<string> Problems { get; } = new();
    public AgentDecision? Decision { get; set; }
    /// <summary>The menu entry of actions[0].</summary>
    public ActionOption? Chosen { get; set; }
    public TimeSpan Latency { get; set; }
    /// <summary>Model calls, including the repair turn and HTTP retries.</summary>
    public int LlmCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    /// <summary>Requests as sent to the model (the first one is the decision prompt).</summary>
    public List<LlmRequest> Requests { get; } = new();
    /// <summary>Raw answer texts, in order.</summary>
    public List<string> Answers { get; } = new();
    /// <summary>Warnings the engine logged (unusable answers ...).</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>Scores an engine decision against a fixture's expectations.</summary>
public static class Scorer
{
    public static AttemptResult Score(BenchFixture f, GameSnapshot snapshot, AgentDecision? d, int llmCalls, Exception? error = null)
    {
        var r = new AttemptResult { Decision = d, LlmCalls = llmCalls };
        if (error != null || d == null)
        {
            r.Verdict = Verdict.Error;
            r.Problems.Add(error?.Message ?? "no decision (empty menu?)");
            return r;
        }

        // Source: rules must not cost a model call; LLM fixtures must actually reach the model.
        if (f.IsRule)
        {
            if (d.Source != f.ExpectSource) r.Problems.Add($"decided by {d.Source}, expected {f.ExpectSource}");
            if (llmCalls > 0) r.Problems.Add($"the model was called {llmCalls}x although a {f.ExpectSource} decision needs no call");
        }
        else if (d.Source != DecisionSource.Llm)
        {
            r.Problems.Add($"decided by {d.Source} ({d.Thought}) without asking the model");
        }

        r.Chosen = d.FirstActionId == null ? null : snapshot.FindAction(d.FirstActionId);
        bool forbidden = false;
        if (r.Chosen == null)
        {
            r.Problems.Add($"actions[0] \"{d.FirstActionId}\" is not in the menu");
        }
        else
        {
            var key = r.Chosen.SemanticKey;
            if (f.Forbid.Any(p => BenchFixture.Matches(p, r.Chosen)))
            {
                forbidden = true;
                r.Problems.Add($"chose forbidden {key}");
            }
            else if (f.ExpectAny.Count > 0 && !f.ExpectAny.Any(p => BenchFixture.Matches(p, r.Chosen)))
            {
                r.Problems.Add($"chose {key}, expected {string.Join(" | ", f.ExpectAny)}");
            }
        }

        if (f.MaxPlanLength is { } maxPlan && d.ActionIds.Count > maxPlan)
            r.Problems.Add($"answer has {d.ActionIds.Count} actions, at most {maxPlan} allowed");
        if (f.MaxLlmCalls is { } maxCalls && llmCalls > maxCalls)
            r.Problems.Add($"needed {llmCalls} model calls, at most {maxCalls} allowed (first answer invalid?)");

        ScoreMessages(f, d, r);
        r.Verdict = forbidden ? Verdict.Forbidden : r.Problems.Count > 0 ? Verdict.Fail : Verdict.Pass;
        return r;
    }

    private static void ScoreMessages(BenchFixture f, AgentDecision d, AttemptResult r)
    {
        var sent = d.Messages;
        string Describe() => sent.Count == 0 ? "none" : string.Join(", ", sent.Select(m => DecisionParser.KindToWire(m.Kind) + (m.ReplyTo != null ? $"→{m.ReplyTo}" : "")));

        var expected = Kinds(f.ExpectMessageKind);
        var candidates = expected.Count > 0 ? sent.Where(m => expected.Contains(m.Kind)).ToList() : sent;
        if (expected.Count > 0 && candidates.Count == 0)
            r.Problems.Add($"no {string.Join("/", f.ExpectMessageKind)} message (sent: {Describe()})");
        else if (f.ExpectReplyTo != null && !candidates.Any(m => SameId(m.ReplyTo, f.ExpectReplyTo)))
            r.Problems.Add($"no message replies to {f.ExpectReplyTo} (sent: {Describe()})");

        var forbidden = Kinds(f.ForbidMessageKind);
        if (sent.Any(m => forbidden.Contains(m.Kind)))
            r.Problems.Add($"sent a forbidden {string.Join("/", f.ForbidMessageKind)} message (sent: {Describe()})");
        if (f.MaxMessages is { } max && sent.Count > max)
            r.Problems.Add($"sent {sent.Count} team messages, at most {max} allowed (sent: {Describe()})");
    }

    private static HashSet<TeamMessageKind> Kinds(IEnumerable<string> raw) =>
        raw.Select(k => BenchFixture.TryParseKind(k, out var kind) ? (TeamMessageKind?)kind : null)
           .Where(k => k != null).Select(k => k!.Value).ToHashSet();

    /// <summary>Models sometimes echo the id as it appears in the prompt ("[m1]").</summary>
    private static bool SameId(string? replyTo, string id) =>
        replyTo != null && string.Equals(replyTo.Trim().Trim('[', ']', ' '), id, StringComparison.OrdinalIgnoreCase);
}
