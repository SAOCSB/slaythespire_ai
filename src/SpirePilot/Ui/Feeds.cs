using System.Collections.Concurrent;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Team;

namespace SpirePilot.Ui;

/// <summary>
/// Pilot events and team chat entries arrive on worker threads; they are queued here and moved into main-thread
/// lists once per frame (<see cref="Pump"/>). The lists live even while the panel is closed or being rebuilt.
/// </summary>
internal static class Feeds
{
    public sealed class LogItem
    {
        public long Seq { get; init; }
        public DateTime Time { get; init; }
        public PilotEventKind Kind { get; init; }
        public string Text { get; init; } = "";
    }

    public const int MaxLogItems = 600;

    private static readonly ConcurrentQueue<PilotEvent> IncomingEvents = new();
    private static readonly ConcurrentQueue<ChatEntry> IncomingChat = new();
    private static long _seq;

    /// <summary>Main thread: newest last.</summary>
    public static readonly List<LogItem> Log = new();

    /// <summary>Main thread: chat entries received since the team page last drained them.</summary>
    public static readonly List<ChatEntry> NewChat = new();

    /// <summary>Increments whenever <see cref="Log"/> lost items at the front (views must re-render).</summary>
    public static int LogTrimVersion { get; private set; }

    public static void Attach(PilotLoop loop, TeamChatHub team)
    {
        loop.EventLogged += e => IncomingEvents.Enqueue(e);
        team.EntryAdded += e => IncomingChat.Enqueue(e);
    }

    public static void Pump()
    {
        int n = 0;
        while (n++ < 200 && IncomingEvents.TryDequeue(out var e))
            Log.Add(new LogItem { Seq = ++_seq, Time = e.Time, Kind = e.Kind, Text = e.Text ?? "" });
        // Trim in chunks so views re-render once per hundred events, not on every event.
        if (Log.Count > MaxLogItems + 100)
        {
            Log.RemoveRange(0, Log.Count - MaxLogItems);
            LogTrimVersion++;
        }
        n = 0;
        while (n++ < 200 && IncomingChat.TryDequeue(out var c)) NewChat.Add(c);
        if (NewChat.Count > 400) NewChat.RemoveRange(0, NewChat.Count - 400);
    }

    public static void ClearLog()
    {
        Log.Clear();
        LogTrimVersion++;
    }

    public static LogItem? LastOf(params PilotEventKind[] kinds)
    {
        for (int i = Log.Count - 1; i >= 0; i--)
            if (kinds.Contains(Log[i].Kind)) return Log[i];
        return null;
    }
}
