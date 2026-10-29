using System.Diagnostics;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench;

/// <summary>The run cannot continue (bad key, unknown model ...): every further request would fail the same way.</summary>
public sealed class BenchAbortException : Exception
{
    public BenchAbortException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Runs fixtures through a fresh <see cref="DecisionEngine"/> each (fresh <see cref="AgentMemory"/> and
/// <see cref="TeamChatHub"/>), so the prompt is built exactly like the mod builds it, and scores the decision.
/// </summary>
public sealed class BenchRunner
{
    private readonly PilotSettings _baseSettings;
    private readonly Func<LlmProfile, ILlmClient> _clientFactory;
    private readonly bool _dryRun;

    /// <param name="baseSettings">User settings (profile) the fixture overrides are applied to.</param>
    /// <param name="clientFactory">Usually <see cref="LlmClientFactory.Create"/>; tests pass scripted clients.</param>
    /// <param name="dryRun">Build prompts only: LLM fixtures are reported as <see cref="Verdict.DryRun"/>.</param>
    public BenchRunner(PilotSettings baseSettings, Func<LlmProfile, ILlmClient> clientFactory, bool dryRun = false)
    {
        _baseSettings = baseSettings;
        _clientFactory = clientFactory;
        _dryRun = dryRun;
    }

    public async Task<AttemptResult> RunAsync(BenchFixture fixture, CancellationToken ct)
    {
        var settings = BenchSettings.ForFixture(_baseSettings, fixture);
        var snapshot = fixture.CreateSnapshot();
        var hub = new TeamChatHub
        {
            LocalNetId = snapshot.LocalPlayerId,
            LocalName = snapshot.LocalPlayer?.Name ?? "",
            MaxAiMessagesPerMinute = settings.Team.MaxAiMessagesPerMinute,
        };
        var log = new BufferLog();
        ObservedClient? client = null;
        var engine = new DecisionEngine(() => settings, hub, log, p => client = new ObservedClient(_clientFactory(p)))
        {
            MessageLanguage = () => fixture.Language.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "Simplified Chinese" : "English",
        };

        if (!string.IsNullOrWhiteSpace(fixture.Memo)) engine.Memory.SetMemo(fixture.Memo);
        foreach (var line in fixture.History) engine.Memory.AddHistory(line);
        // Fresh timestamps: the engine expires inbox messages older than a few minutes.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var m in fixture.Inbox)
        {
            var copy = m.Sanitized();
            copy.Time = now;
            hub.Receive(copy);
        }

        AgentDecision? decision = null;
        Exception? error = null;
        var watch = Stopwatch.StartNew();
        try
        {
            decision = await engine.DecideAsync(snapshot, fixture.LiveInstruction, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PilotConfigException ex) when (ex.InnerException is LlmException)
        {
            throw new BenchAbortException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        watch.Stop();

        int calls = client?.Calls ?? 0;
        var result = _dryRun && !fixture.IsRule && error == null
            ? new AttemptResult { Verdict = Verdict.DryRun, Decision = decision } // the dry-run client's calls are not model calls
            : Scorer.Score(fixture, snapshot, decision, calls, error);
        result.Latency = watch.Elapsed;
        result.InputTokens = engine.TotalInputTokens;
        result.OutputTokens = engine.TotalOutputTokens;
        result.CachedTokens = engine.TotalCachedTokens;
        if (client != null)
        {
            result.Requests.AddRange(client.Requests);
            result.Answers.AddRange(client.Answers);
        }
        result.Warnings.AddRange(log.Lines);
        return result;
    }

    /// <summary>Counts calls and keeps requests / raw answers for reports.</summary>
    private sealed class ObservedClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        public ObservedClient(ILlmClient inner) => _inner = inner;
        public int Calls { get; private set; }
        public List<LlmRequest> Requests { get; } = new();
        public List<string> Answers { get; } = new();
        public string Describe => _inner.Describe;

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (!Requests.Contains(request)) Requests.Add(request);
            var response = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            Answers.Add(response.Text);
            return response;
        }
    }

    private sealed class BufferLog : IPilotLog
    {
        public List<string> Lines { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message) => Lines.Add(message);
    }
}

/// <summary>--dry-run client: never touches the network, always answers with the first menu id.</summary>
public sealed class DryRunClient : ILlmClient
{
    public string Describe => "dry run (no API calls)";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LlmResponse { Text = "{\"thought\":\"(dry run)\",\"actions\":[\"a0\"]}" });
}
