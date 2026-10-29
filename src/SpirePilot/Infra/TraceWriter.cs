using System.Text.Encodings.Web;
using System.Text.Json;
using SpirePilot.Core.Agent;

namespace SpirePilot.Infra;

/// <summary>Appends decision traces as JSON lines to &lt;data dir&gt;/traces/trace-yyyyMMdd.jsonl (one file per day, capped).</summary>
internal sealed class TraceWriter
{
    private const long MaxFileBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _dir;
    private readonly object _lock = new();

    public TraceWriter(string dataDir) => _dir = Path.Combine(dataDir, "traces");

    public void Write(DecisionTrace trace)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                var path = Path.Combine(_dir, $"trace-{DateTime.Now:yyyyMMdd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) return;
                File.AppendAllText(path, JsonSerializer.Serialize(trace, Json) + "\n");
            }
        }
        catch (Exception ex)
        {
            ModLog.Debug("trace write failed: " + ex.Message);
        }
    }
}
