using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;

namespace SpirePilot.Bench;

/// <summary>Entry point: validate fixtures, then run (or dry-run) them and print / write the results.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* redirected / no console */ }

        BenchOptions o;
        try { o = BenchOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message + "\n");
            Console.Error.WriteLine(BenchOptions.Usage);
            return 2;
        }
        if (o.Help)
        {
            Console.WriteLine(BenchOptions.Usage);
            return 0;
        }

        string dir;
        try { dir = o.ResolveFixturesDir(); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }

        var (fixtures, problems) = LoadAndValidate(dir);
        if (o.Validate)
        {
            foreach (var f in fixtures.Where(f => problems.All(p => p.file != Path.GetFileName(f.Path))))
                Console.WriteLine($"ok    {Path.GetFileName(f.Path)}");
            foreach (var (file, problem) in problems) Console.WriteLine($"FAIL  {file}: {problem}");
            Console.WriteLine($"\n{fixtures.Count} fixtures in {Display(dir)}, {problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 1;
        }
        if (problems.Count > 0)
        {
            foreach (var (file, problem) in problems) Console.Error.WriteLine($"invalid fixture {file}: {problem}");
            Console.Error.WriteLine("Fix the fixtures first (see --validate).");
            return 1;
        }

        var selected = o.Filters.Count == 0 ? fixtures : fixtures.Where(f => o.Filters.Any(f.MatchesFilter)).ToList();
        if (selected.Count == 0)
        {
            Console.Error.WriteLine($"error: no fixture matches {string.Join(", ", o.Filters)}");
            return 2;
        }

        PilotSettings settings;
        try
        {
            settings = o.SettingsPath != null ? BenchSettings.Load(o.SettingsPath, o.Profile, o.KeepSettings) : new PilotSettings();
            if (o.DryRun) settings = BenchSettings.ForDryRun(settings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: cannot read settings \"{o.SettingsPath}\": {ex.Message}");
            return 2;
        }
        var profile = settings.Active!;
        if (!o.DryRun && LlmClientFactory.Validate(profile) is { Count: > 0 } incomplete)
        {
            Console.Error.WriteLine($"error: profile \"{profile.Name}\" is incomplete: {string.Join(", ", incomplete)} (apiKey may be \"env:NAME\")");
            return 2;
        }

        var info = new BenchRunInfo
        {
            Model = o.DryRun ? "dry run (no API calls)" : LlmClientFactory.Create(profile).Describe,
            Profile = profile.Name,
            Endpoint = Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : profile.BaseUrl,
            Effort = Effort(profile),
            Repeat = o.DryRun ? 1 : o.Repeat,
            FixturesDir = Display(dir),
            DryRun = o.DryRun,
        };
        var runner = new BenchRunner(settings, o.DryRun ? _ => new DryRunClient() : LlmClientFactory.Create, o.DryRun);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (o.DryRun)
        {
            Console.WriteLine("════ SYSTEM (constant, identical for every decision) ════");
            Console.WriteLine(PromptBuilder.CoreSystem);
        }
        else
        {
            Console.WriteLine($"{info.Model} · profile {info.Profile} · {info.Endpoint}{(info.Effort.Length > 0 ? " · effort " + info.Effort : "")} · {selected.Count} fixtures × {info.Repeat}\n");
            Console.WriteLine(BenchReport.TableHeader());
        }

        var results = new List<FixtureSummary>();
        var wall = Stopwatch.StartNew();
        string? abort = null;
        try
        {
            foreach (var f in selected)
            {
                var summary = new FixtureSummary(f);
                results.Add(summary);
                for (int i = 0; i < info.Repeat; i++) summary.Attempts.Add(await runner.RunAsync(f, cts.Token));
                if (o.DryRun) PrintPrompt(summary);
                else PrintRow(summary);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            abort = "cancelled";
        }
        catch (BenchAbortException ex)
        {
            abort = ex.Message;
        }
        wall.Stop();
        info.Wall = wall.Elapsed;
        results.RemoveAll(r => r.Attempts.Count == 0);

        if (o.DryRun)
        {
            Console.WriteLine("════ SUMMARY ════");
            Console.WriteLine(BenchReport.TableHeader());
            foreach (var r in results) PrintRow(r);
        }
        Console.WriteLine();
        Console.WriteLine(BenchReport.Totals(results, info.Wall));
        if (abort != null) Console.Error.WriteLine($"stopped: {abort}");

        if (!string.IsNullOrWhiteSpace(o.OutPath))
        {
            try
            {
                var outDir = Path.GetDirectoryName(Path.GetFullPath(o.OutPath));
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                File.WriteAllText(o.OutPath, BenchReport.Markdown(results, info));
                Console.WriteLine($"Report written to {o.OutPath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: cannot write {o.OutPath}: {ex.Message}");
                return 2;
            }
        }
        if (abort != null) return 2;
        return results.All(r => r.AllPassed || r.IsDryRun) ? 0 : 1;
    }

    private static (List<BenchFixture> fixtures, List<(string file, string problem)> problems) LoadAndValidate(string dir)
    {
        var (fixtures, loadErrors) = FixtureLoader.LoadAll(dir);
        var problems = loadErrors.Select(e => (Path.GetFileName(e.path), e.error)).ToList();
        foreach (var f in fixtures)
            problems.AddRange(FixtureValidator.Validate(f).Select(p => (Path.GetFileName(f.Path), p)));
        problems.AddRange(FixtureValidator.ValidateSet(fixtures).Select(p => ("(all)", p)));
        return (fixtures, problems);
    }

    private static void PrintRow(FixtureSummary s)
    {
        Console.WriteLine(BenchReport.TableRow(s));
        if (s.AllPassed || s.IsDryRun) return;
        foreach (var a in s.Attempts.Where(a => a.Verdict != Verdict.Pass).Take(3))
        {
            Console.WriteLine("    ↳ " + string.Join("; ", a.Problems));
            if (!string.IsNullOrWhiteSpace(a.Decision?.Thought)) Console.WriteLine("      thought: " + a.Decision!.Thought.Replace("\n", " "));
        }
    }

    private static void PrintPrompt(FixtureSummary s)
    {
        var f = s.Fixture;
        var a = s.Attempts[0];
        Console.WriteLine($"\n════ {f.Id} [{string.Join(", ", f.Tags)}] ════");
        Console.WriteLine("why:    " + f.Rationale);
        if (f.ExpectAny.Count > 0) Console.WriteLine("expect: " + string.Join(" | ", f.ExpectAny));
        if (f.Forbid.Count > 0) Console.WriteLine("forbid: " + string.Join(" | ", f.Forbid));
        if (f.ExpectMessageKind.Count > 0) Console.WriteLine("message: " + string.Join("/", f.ExpectMessageKind) + (f.ExpectReplyTo != null ? " → " + f.ExpectReplyTo : ""));
        if (a.Requests.Count == 0)
        {
            var label = a.Chosen?.Label ?? a.Decision?.FirstActionId ?? "-";
            Console.WriteLine($"── no LLM call: {a.Decision?.Source} {a.Decision?.Thought} → {label} → {a.Verdict.ToString().ToUpperInvariant()} {string.Join("; ", a.Problems)}");
            return;
        }
        var request = a.Requests[0];
        Console.WriteLine("──── SYSTEM SUFFIX (owner settings + character guide) ────");
        Console.WriteLine(request.SystemSuffix.TrimEnd());
        if (!string.IsNullOrEmpty(request.Effort)) Console.WriteLine($"(effort: {request.Effort})");
        Console.WriteLine("──── USER ────");
        Console.WriteLine(request.Messages[0].Content);
    }

    private static string Effort(LlmProfile p)
    {
        var main = string.IsNullOrWhiteSpace(p.ReasoningEffort) ? "" : p.ReasoningEffort;
        var combat = string.IsNullOrWhiteSpace(p.CombatReasoningEffort) || p.CombatReasoningEffort == main ? "" : p.CombatReasoningEffort;
        return combat.Length == 0 ? main : $"{(main.Length > 0 ? main : "default")} / combat {combat}";
    }

    private static string Display(string dir)
    {
        var rel = Path.GetRelativePath(Environment.CurrentDirectory, dir);
        return rel.StartsWith("..", StringComparison.Ordinal) ? dir : rel;
    }
}
