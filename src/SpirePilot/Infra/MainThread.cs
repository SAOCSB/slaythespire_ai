using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;

namespace SpirePilot.Infra;

/// <summary>
/// Runs work on the Godot main thread. Driven by <see cref="SceneTree.SignalName.ProcessFrame"/>, so it needs no
/// custom Node subclass. Game objects (models, nodes, synchronizers) must only be touched from here.
/// </summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly List<TaskCompletionSource<bool>> FrameWaiters = new();
    private static int _mainThreadId = -1;
    private static bool _installed;

    /// <summary>Raised once per frame on the main thread (after queued work ran).</summary>
    public static event Action? Frame;

    public static ulong FrameCount { get; private set; }

    public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>Must be called once from the main thread (the mod initializer runs there).</summary>
    public static void Install()
    {
        if (_installed) return;
        _mainThreadId = Environment.CurrentManagedThreadId;
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(Pump));
        _installed = true;
    }

    private static void Pump()
    {
        FrameCount++;
        // Bounded work per frame so a burst of requests never stalls rendering.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8 && Queue.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex) { ModLog.Error("main-thread work failed: " + ex); }
        }

        List<TaskCompletionSource<bool>>? waiters = null;
        lock (FrameWaiters)
        {
            if (FrameWaiters.Count > 0)
            {
                waiters = new List<TaskCompletionSource<bool>>(FrameWaiters);
                FrameWaiters.Clear();
            }
        }
        if (waiters != null) foreach (var w in waiters) w.TrySetResult(true);

        try { Frame?.Invoke(); }
        catch (Exception ex) { ModLog.Error("frame handler failed: " + ex); }
    }

    /// <summary>Queues fire-and-forget work for the main thread.</summary>
    public static void Post(Action action) => Queue.Enqueue(action);

    public static Task<T> Run<T>(Func<T> func)
    {
        if (IsMainThread)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public static Task Run(Action action) => Run(() => { action(); return true; });

    /// <summary>
    /// Starts <paramref name="func"/> on the main thread. Its awaits continue on the main thread (Godot's
    /// synchronization context / <see cref="NextFrame"/>), and the returned task completes on a worker thread.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Start()
        {
            Task<T> inner;
            try { inner = func(); }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }
            inner.ContinueWith(t =>
            {
                if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(t.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        if (IsMainThread) Start();
        else Queue.Enqueue(Start);
        return tcs.Task;
    }

    /// <summary>Completes on the next frame. Awaited on the main thread, the continuation runs on the main thread.</summary>
    public static Task NextFrame()
    {
        var tcs = new TaskCompletionSource<bool>();
        lock (FrameWaiters) FrameWaiters.Add(tcs);
        return tcs.Task;
    }

    public static async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await NextFrame();
    }

    /// <summary>Main-thread polling helper: waits until <paramref name="condition"/> holds or the timeout elapses.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            bool ok;
            try { ok = condition(); }
            catch (Exception) { ok = false; }
            if (ok) return true;
            if (sw.Elapsed > timeout || ct.IsCancellationRequested) return false;
            await NextFrame();
        }
    }

    /// <summary>Main-thread delay measured in real time but resumed on a frame boundary.</summary>
    public static async Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < delay && !ct.IsCancellationRequested) await NextFrame();
    }
}
