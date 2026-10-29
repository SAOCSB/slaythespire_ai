using System.Globalization;
using System.Text;
using SpirePilot.Core.Agent;

namespace SpirePilot.Bench;

/// <summary>All attempts at one fixture.</summary>
public sealed class FixtureSummary
{
    public FixtureSummary(BenchFixture fixture) => Fixture = fixture;

    public BenchFixture Fixture { get; }
    public List<AttemptResult> Attempts { get; } = new();

    public int Passed => Attempts.Count(a => a.Verdict == Verdict.Pass);
    public bool IsDryRun => Attempts.Count > 0 && Attempts.All(a => a.Verdict == Verdict.DryRun);
    public bool AllPassed => Attempts.Count > 0 && Passed == Attempts.Count;
    public int Errors => Attempts.Count(a => a.Verdict == Verdict.Error);

    /// <summary>PASS / FAIL / FORBIDDEN / ERROR, "FLAKY 2/3" for mixed results, "PASS 3/3" with repeats.</summary>
    public string Status
    {
        get
        {
            if (IsDryRun) return "DRY";
            int n = Attempts.Count;
            string suffix = n > 1 ? $" {Passed}/{n}" : "";
            if (AllPassed) return "PASS" + suffix;
            if (Passed > 0) return "FLAKY" + suffix;
            var worst = Attempts.GroupBy(a => a.Verdict).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            return worst.ToString().ToUpperInvariant() + suffix;
        }
    }

    /// <summary>Label of the most frequent first action ("+1 other" when attempts disagreed).</summary>
    public string Chosen
    {
        get
        {
            var groups = Attempts.Where(a => a.Chosen != null).GroupBy(a => a.Chosen!.SemanticKey).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0)
                return IsDryRun ? "(prompt only)" : Attempts.FirstOrDefault(a => a.Verdict == Verdict.Error)?.Problems.FirstOrDefault() ?? "-";
            var label = groups[0].First().Chosen!.Label;
            return groups.Count > 1 ? $"{label} (+{groups.Count - 1} other)" : label;
        }
    }

    public TimeSpan AverageLatency => Attempts.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)Attempts.Average(a => a.Latency.Ticks));
    public int LlmCalls => Attempts.Sum(a => a.LlmCalls);
    public long InputTokens => Attempts.Sum(a => a.InputTokens);
    public long OutputTokens => Attempts.Sum(a => a.OutputTokens);
    public long CachedTokens => Attempts.Sum(a => a.CachedTokens);
}

/// <summary>Describes the run for report headers.</summary>
public sealed class BenchRunInfo
{
    public string Model { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Effort { get; init; } = "";
    public int Repeat { get; init; } = 1;
    public string FixturesDir { get; init; } = "";
    public DateTime Started { get; init; } = DateTime.Now;
    public TimeSpan Wall { get; set; }
    public bool DryRun { get; init; }
}

/// <summary>Console table and markdown report.</summary>
public static class BenchReport
{
    private const int IdWidth = 36, StatusWidth = 15, ChosenWidth = 50, LatencyWidth = 8;

    public static string TableHeader() =>
        Fit("Fixture", IdWidth) + " " + Fit("Result", StatusWidth) + " " + Fit("Chosen", ChosenWidth) + " " +
        Fit("Latency", LatencyWidth) + " Tokens in/out";

    public static string TableRow(FixtureSummary s)
    {
        bool called = s.LlmCalls > 0;
        string latency = called ? Seconds(s.AverageLatency) : "-";
        string tokens = called ? $"{Tokens(s.InputTokens / Math.Max(1, s.Attempts.Count))}/{Tokens(s.OutputTokens / Math.Max(1, s.Attempts.Count))}" : "-";
        if (s.Fixture.IsRule) tokens += " (rule)";
        return Fit(s.Fixture.Id, IdWidth) + " " + Fit(s.Status, StatusWidth) + " " + Fit(OneLine(s.Chosen), ChosenWidth) + " " +
               Fit(latency, LatencyWidth) + " " + tokens;
    }

    /// <summary>One-paragraph totals: fixtures passed, attempts, errors, tokens, latency.</summary>
    public static string Totals(IReadOnlyList<FixtureSummary> results, TimeSpan wall)
    {
        var scored = results.Where(r => !r.IsDryRun).ToList();
        var attempts = scored.SelectMany(r => r.Attempts).ToList();
        int fixturesPassed = scored.Count(r => r.AllPassed);
        int attemptsPassed = attempts.Count(a => a.Verdict == Verdict.Pass);
        var timed = scored.SelectMany(r => r.Attempts).Where(a => a.LlmCalls > 0).Select(a => a.Latency).OrderBy(t => t).ToList();
        if (results.Count == 0) return $"No fixture finished · wall {Seconds(wall)}";
        if (scored.Count == 0) return $"Dry run: {results.Count} prompt(s) built, no model calls · wall {Seconds(wall)}";
        var sb = new StringBuilder();
        sb.Append($"Passed {fixturesPassed}/{scored.Count} fixtures ({Percent(fixturesPassed, scored.Count)})");
        if (attempts.Count != scored.Count) sb.Append($", {attemptsPassed}/{attempts.Count} attempts ({Percent(attemptsPassed, attempts.Count)})");
        sb.Append($" · forbidden {attempts.Count(a => a.Verdict == Verdict.Forbidden)} · errors {attempts.Count(a => a.Verdict == Verdict.Error)}");
        if (results.Count > scored.Count) sb.Append($" · {results.Count - scored.Count} dry");
        sb.Append($" · model calls {results.Sum(r => r.LlmCalls)}");
        long cached = results.Sum(r => r.CachedTokens);
        sb.Append($" · tokens in {Tokens(results.Sum(r => r.InputTokens))}{(cached > 0 ? $" (cached {Tokens(cached)})" : "")} / out {Tokens(results.Sum(r => r.OutputTokens))}");
        if (timed.Count > 0)
            sb.Append($" · latency avg {Seconds(TimeSpan.FromTicks((long)timed.Average(t => t.Ticks)))}, median {Seconds(timed[timed.Count / 2])}, max {Seconds(timed[^1])}");
        sb.Append($" · wall {Seconds(wall)}");
        return sb.ToString();
    }

    public static string Markdown(IReadOnlyList<FixtureSummary> results, BenchRunInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SpirePilot decision benchmark").AppendLine();
        sb.AppendLine($"- Date: {info.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Model: **{Md(info.Model)}** (profile `{info.Profile}`, {Md(info.Endpoint)}{(info.Effort.Length > 0 ? ", effort " + info.Effort : "")})");
        sb.AppendLine($"- Fixtures: {results.Count} from `{info.FixturesDir}`, {info.Repeat} attempt(s) each{(info.DryRun ? " — dry run, no model calls" : "")}");
        sb.AppendLine($"- Result: {Totals(results, info.Wall)}").AppendLine();

        sb.AppendLine("| Fixture | Tags | Result | Chosen | Latency | Tokens in/out |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            int n = Math.Max(1, r.Attempts.Count);
            bool called = r.LlmCalls > 0;
            sb.Append("| ").Append(Md(r.Fixture.Id)).Append(" | ").Append(Md(string.Join(", ", r.Fixture.Tags)))
              .Append(" | ").Append(r.AllPassed ? "✅ " : r.IsDryRun ? "" : "❌ ").Append(r.Status)
              .Append(" | ").Append(Md(OneLine(r.Chosen)))
              .Append(" | ").Append(called ? Seconds(r.AverageLatency) : "-")
              .Append(" | ").Append(called ? $"{Tokens(r.InputTokens / n)}/{Tokens(r.OutputTokens / n)}" : r.Fixture.IsRule ? "rule" : "-")
              .AppendLine(" |");
        }

        var failing = results.Where(r => !r.AllPassed && !r.IsDryRun).ToList();
        if (failing.Count > 0)
        {
            sb.AppendLine().AppendLine("## Not passed").AppendLine();
            foreach (var r in failing)
            {
                var f = r.Fixture;
                sb.AppendLine($"### {Md(f.Id)} — {r.Status}").AppendLine();
                sb.AppendLine($"- Why: {Md(f.Rationale)}");
                if (f.ExpectAny.Count > 0) sb.AppendLine($"- Expected: {string.Join(", ", f.ExpectAny.Select(Code))}");
                if (f.Forbid.Count > 0) sb.AppendLine($"- Forbidden: {string.Join(", ", f.Forbid.Select(Code))}");
                if (f.ExpectMessageKind.Count > 0) sb.AppendLine($"- Expected message: {string.Join("/", f.ExpectMessageKind)}{(f.ExpectReplyTo != null ? $" replying to `{f.ExpectReplyTo}`" : "")}");
                for (int i = 0; i < r.Attempts.Count; i++)
                {
                    var a = r.Attempts[i];
                    if (a.Verdict == Verdict.Pass) continue;
                    var d = a.Decision;
                    sb.Append(r.Attempts.Count > 1 ? $"- Attempt {i + 1}: " : "- Answer: ");
                    sb.Append(a.Verdict.ToString().ToUpperInvariant());
                    if (a.Chosen != null) sb.Append($" — {Md(a.Chosen.Label)} ({Code(a.Chosen.SemanticKey)})");
                    if (d != null && d.ActionIds.Count > 1) sb.Append($", plan {string.Join(" ", d.ActionIds)}");
                    sb.AppendLine();
                    if (!string.IsNullOrWhiteSpace(d?.Thought)) sb.AppendLine($"  - Thought: {Md(OneLine(d!.Thought))}");
                    if (d is { Messages.Count: > 0 })
                        sb.AppendLine($"  - Messages: {string.Join("; ", d.Messages.Select(m => $"{DecisionParser.KindToWire(m.Kind)}{(m.ReplyTo != null ? "→" + m.ReplyTo : "")} \"{Md(OneLine(m.Text))}\""))}");
                    foreach (var p in a.Problems) sb.AppendLine($"  - Problem: {Md(OneLine(p))}");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string Tokens(long n) => n >= 10_000 ? (n / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : n >= 1000 ? (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    public static string Seconds(TimeSpan t) => t.TotalSeconds >= 120
        ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s"
        : t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Percent(int n, int total) => total == 0 ? "-" : (100.0 * n / total).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Code(string s) => "`" + s.Replace("`", "'") + "`";

    /// <summary>Pads / truncates to a terminal width, counting CJK characters as two columns.</summary>
    public static string Fit(string s, int width)
    {
        int total = Width(s);
        if (total <= width) return s + new string(' ', width - total);
        var sb = new StringBuilder();
        int used = 0;
        foreach (var ch in s)
        {
            int w = IsWide(ch) ? 2 : 1;
            if (used + w > width - 1) break; // keep one column for the ellipsis
            sb.Append(ch);
            used += w;
        }
        return sb.Append('…').Append(' ', width - used - 1).ToString();
    }

    public static int Width(string s) => s.Sum(c => IsWide(c) ? 2 : 1);

    private static bool IsWide(char c) =>
        c is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3' or
            >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6';
}
