using System.Text;
using MegaCrit.Sts2.Core.Logging;
using SpirePilot.Core.Agent;

namespace SpirePilot.Infra;

/// <summary>Logs to the game's log (visible in the game's log file / dev console) and to SpirePilot's own file.</summary>
internal static class ModLog
{
    private const string Prefix = "[SpirePilot] ";
    private static readonly object FileLock = new();
    private static string? _filePath;

    public static void Configure(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "spirepilot.log");
            // Keep one previous log for bug reports.
            if (File.Exists(_filePath) && new FileInfo(_filePath).Length > 0)
                File.Copy(_filePath, _filePath + ".prev", overwrite: true);
            File.WriteAllText(_filePath, $"SpirePilot log started {DateTime.Now:O}{System.Environment.NewLine}");
        }
        catch (Exception)
        {
            _filePath = null;
        }
    }

    public static string? FilePath => _filePath;

    /// <summary>
    /// The last <paramref name="count"/> lines of the log file (empty without one). Reads only the end of the file, so
    /// it stays cheap however large the log grew; a line longer than that window is cut. Any thread.
    /// </summary>
    public static List<string> Tail(int count)
    {
        const int Window = 256 * 1024;
        var path = _filePath;
        if (path == null || !File.Exists(path)) return new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        long start = Math.Max(0, length - Window);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[length - start];
        int read = 0, n;
        while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
        var lines = Encoding.UTF8.GetString(bytes, 0, read).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // starts mid-line
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
    }

    public static void Info(string message)
    {
        try { Log.Info(Prefix + message); } catch (Exception) { Godot.GD.Print(Prefix + message); }
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        try { Log.Warn(Prefix + message); } catch (Exception) { Godot.GD.Print(Prefix + "WARN " + message); }
        Write("WARN", message);
    }

    public static void Error(string message)
    {
        try { Log.Error(Prefix + message); } catch (Exception) { Godot.GD.PrintErr(Prefix + message); }
        Write("ERROR", message);
    }

    /// <summary>File-only (verbose) logging, e.g. prompts.</summary>
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        var path = _filePath;
        if (path == null) return;
        try
        {
            lock (FileLock)
            {
                if (new FileInfo(path).Length > 20 * 1024 * 1024) File.WriteAllText(path, "(log truncated)\n");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{System.Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception) { /* logging must never throw */ }
    }

    /// <summary>Adapter for the core library.</summary>
    public sealed class Sink : IPilotLog
    {
        public void Info(string message) => ModLog.Info(message);
        public void Warn(string message) => ModLog.Warn(message);
        public void Error(string message) => ModLog.Error(message);
    }
}
