using SpirePilot.Core.Agent;
using SpirePilot.Core.Model;
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
    private readonly object _lock = new();
    private readonly LinkedList<ActionSink> _sinks = new();
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
            _sinks.AddLast(sink);
            while (_sinks.Count > KeepSinks) _sinks.RemoveFirst();
        }
        try { Captured?.Invoke(snap); } catch (Exception ex) { ModLog.Warn("capture listener failed: " + ex.Message); }
        return snap;
    }

    public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken cancellationToken) =>
        MainThread.RunAsync(() => ExecuteOnMainThread(action, cancellationToken));

    private async Task<ActionResult> ExecuteOnMainThread(ActionOption action, CancellationToken ct)
    {
        // One action at a time: overlapping game actions (pilot + HTTP API) would race each other.
        if (Interlocked.Exchange(ref _executing, 1) == 1) return ActionResult.Fail("another action is still running");
        try
        {
            var executor = FindExecutor(action);
            if (executor == null) return ActionResult.Fail("action is no longer available");
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

    private ActionExecutor? FindExecutor(ActionOption action)
    {
        lock (_lock)
        {
            var newest = _sinks.Last?.Value;
            // Only trust the action's own snapshot if nothing newer exists; otherwise the screen may have changed.
            if (newest != null && newest.Sequence == action.SnapshotSequence) return newest.Find(action.Id);
        }
        // Re-read the game and look the action up by meaning.
        var fresh = CaptureOnMainThread();
        if (fresh == null) return null;
        lock (_lock)
        {
            var sink = _sinks.Last?.Value;
            if (sink == null) return null;
            return sink.FindBySemanticKey(action.SemanticKey);
        }
    }

    public void ShowSpeech(ulong playerNetId, string text) => MainThread.Post(() => SpeechBubbles.Show(playerNetId, text));

    public void PingMapNode(string nodeKey) => MainThread.Post(() =>
    {
        try { MapReader.Ping(nodeKey); }
        catch (Exception ex) { ModLog.Warn("map ping failed: " + ex.Message); }
    });
}
