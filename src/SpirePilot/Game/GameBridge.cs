using SpirePilot.Core.Agent;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Game.Readers;
using SpirePilot.Infra;
using SpirePilot.Team;

namespace SpirePilot.Game;

/// <summary>
/// <see cref="IGameBridge"/> implementation: builds snapshots and runs executors on the main thread. Keeps the
/// action sinks of the last few snapshots so an action chosen from a slightly older snapshot can still be found
/// (by id within its own snapshot, or by semantic key in the newest one).
/// </summary>
internal sealed class GameBridge : IGameBridge
{
    private const int KeepSinks = 8;
    private const int KeepMenus = 64;
    private readonly object _lock = new();
    private readonly LinkedList<CaptureEntry> _sinks = new();
    /// <summary>Distinct menus of recent captures: (first sequence, last sequence, menu signature).</summary>
    private readonly LinkedList<MenuSpan> _menus = new();
    private long _sequence;
    private GameSnapshot? _latest;
    private int _executing;

    /// <summary>Newest snapshot (from any caller: pilot, UI, HTTP API).</summary>
    public GameSnapshot? Latest { get { lock (_lock) return _latest; } }

    /// <summary>Raised on the main thread after each capture.</summary>
    public event Action<GameSnapshot>? Captured;

    public Task<GameSnapshot?> CaptureAsync(CancellationToken cancellationToken) => MainThread.Run(CaptureOnMainThread);

    /// <summary>Main thread only.</summary>
    public GameSnapshot? CaptureOnMainThread()
    {
        GameSnapshot snap;
        ActionSink sink;
        try
        {
            (snap, sink) = SnapshotBuilder.Build(Interlocked.Increment(ref _sequence));
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("capture", "snapshot failed: " + ex);
            return null;
        }
        lock (_lock)
        {
            _latest = snap;
            _sinks.AddLast(new CaptureEntry(sink, snap.CanAct, snap.WaitReason));
            while (_sinks.Count > KeepSinks) _sinks.RemoveFirst();
            var signature = MenuSignature(snap);
            if (_menus.Last?.Value is { } last && last.Signature == signature) last.To = snap.Sequence;
            else _menus.AddLast(new MenuSpan(snap.Sequence, signature));
            while (_menus.Count > KeepMenus) _menus.RemoveFirst();
        }
        try { Captured?.Invoke(snap); } catch (Exception ex) { ModLog.Warn("capture listener failed: " + ex.Message); }
        return snap;
    }

    /// <summary>
    /// True when the menu of snapshot <paramref name="sequence"/> is the menu of <paramref name="current"/> (same ids,
    /// semantic keys and labels), so an id read from that snapshot still means the same action. Captures that do not
    /// change the menu (the panel, the advisor refreshing) never make an id stale; a snapshot too old to remember does.
    /// </summary>
    public bool MenuUnchangedSince(long sequence, GameSnapshot current)
    {
        var signature = MenuSignature(current);
        lock (_lock)
        {
            foreach (var span in _menus)
                if (sequence >= span.From && sequence <= span.To) return span.Signature == signature;
        }
        return false;
    }

    private static string MenuSignature(GameSnapshot snap) =>
        string.Join("\n", snap.Actions.Select(a => a.Id + "|" + a.SemanticKey + "|" + a.Label));

    private sealed class MenuSpan
    {
        public MenuSpan(long sequence, string signature)
        {
            From = To = sequence;
            Signature = signature;
        }

        public long From { get; }
        public long To { get; set; }
        public string Signature { get; }
    }

    public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken cancellationToken) =>
        MainThread.RunAsync(() => ExecuteOnMainThread(action, cancellationToken));

    private async Task<ActionResult> ExecuteOnMainThread(ActionOption action, CancellationToken ct)
    {
        // One action at a time: overlapping game actions (pilot + HTTP API) would race each other.
        if (Interlocked.Exchange(ref _executing, 1) == 1) return ActionResult.Fail("another action is still running");
        try
        {
            var (executor, why) = FindExecutor(action);
            if (executor == null) return ActionResult.Fail(why);
            ModLog.Info($"execute {action.Kind}: {action.Label}");
            var result = await executor(ct);
            if (!result.Success) ModLog.Warn($"action failed ({action.Label}): {result.Message}");
            return result;
        }
        catch (OperationCanceledException)
        {
            return ActionResult.Fail("cancelled");
        }
        catch (Exception ex)
        {
            ModLog.Error($"action threw ({action.Label}): {ex}");
            return ActionResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
        }
    }

    /// <summary>
    /// The executor for <paramref name="action"/>, taken from a snapshot in which the game was ready to act; otherwise
    /// null and the reason. Never hands out an executor while <see cref="Readiness"/> says the game is not ready.
    /// </summary>
    private (ActionExecutor? executor, string why) FindExecutor(ActionOption action)
    {
        lock (_lock)
        {
            var newest = _sinks.Last?.Value;
            // Only trust the action's own snapshot if nothing newer exists; otherwise the screen may have changed.
            if (newest != null && newest.Sink.Sequence == action.SnapshotSequence)
            {
                if (!newest.CanAct) return (null, "cannot act now: " + (newest.WaitReason ?? "not ready"));
                var byId = newest.Sink.Find(action.Id);
                return byId != null ? (byId, "") : (null, "action is no longer available");
            }
        }
        // Re-read the game and look the action up by meaning.
        var fresh = CaptureOnMainThread();
        if (fresh == null) return (null, "no game state");
        if (!fresh.CanAct) return (null, "cannot act now: " + (fresh.WaitReason ?? "not ready"));
        lock (_lock)
        {
            var sink = _sinks.Last?.Value.Sink;
            var bySemantic = sink?.FindBySemanticKey(action.SemanticKey);
            return bySemantic != null ? (bySemantic, "") : (null, "action is no longer available");
        }
    }

    /// <summary>One capture's action sink with the readiness verdict it was built under.</summary>
    private sealed record CaptureEntry(ActionSink Sink, bool CanAct, string? WaitReason);

    public void ShowSpeech(ulong playerNetId, string text) => MainThread.Post(() => SpeechBubbles.Show(playerNetId, text));

    public void PingMapNode(string nodeKey) => MainThread.Post(() =>
    {
        try { MapReader.Ping(nodeKey); }
        catch (Exception ex) { ModLog.Warn("map ping failed: " + ex.Message); }
    });

    /// <summary>
    /// The vanilla reaction emote closest to <paramref name="kind"/> (thumbs up / down, exclamation, question mark),
    /// for teammates without SpirePilot. Runs on the main thread (inline when called there, else waits briefly);
    /// true only when the reaction was sent, in a co-op run.
    /// </summary>
    public bool TrySignal(TeamMessageKind kind)
    {
        if (!Reactions.Has(kind)) return false;
        if (MainThread.IsMainThread) return SignalOnMainThread(kind);
        // 0 = pending, 1 = claimed by the main thread, 2 = abandoned by us (a late frame then sends nothing).
        int state = 0;
        var task = MainThread.Run(() => Interlocked.CompareExchange(ref state, 1, 0) == 0 && SignalOnMainThread(kind));
        try
        {
            if (task.Wait(SignalTimeout)) return task.Result;
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) return false;
            // Already running on the main thread: it finishes within the frame.
            return task.Wait(SignalTimeout) && task.Result;
        }
        catch (Exception) { return false; }
    }

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(2);

    private static bool SignalOnMainThread(TeamMessageKind kind)
    {
        try { return Reactions.Send(kind); }
        catch (Exception ex)
        {
            ThrottledLog.Warn("reaction", "reaction emote failed: " + ex.Message);
            return false;
        }
    }
}
