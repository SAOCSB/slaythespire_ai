using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpirePilot.Core.Config;

/// <summary>Loads and saves <see cref="PilotSettings"/> as JSON (atomic write, tolerant read).</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    public string Path { get; }
    public PilotSettings Current { get; private set; } = new();
    /// <summary>Error from the last load, when the file existed but could not be read.</summary>
    public string? LoadError { get; private set; }
    private bool _protectFile;

    public event Action<PilotSettings>? Saved;

    public SettingsStore(string path) => Path = path;

    public PilotSettings Load()
    {
        lock (_lock)
        {
            LoadError = null;
            PilotSettings? loaded = null;
            string? text = null;
            // A sharing violation (antivirus, sync tool, editor) is transient: retry before treating the file as bad.
            for (int attempt = 0; attempt < 5 && text == null && File.Exists(Path); attempt++)
            {
                try { text = File.ReadAllText(Path); }
                catch (IOException ex) when (attempt < 4) { LoadError = ex.Message; Thread.Sleep(100); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    LoadError = ex.Message;
                    // Unreadable: never overwrite it with defaults behind the user's back.
                    _protectFile = true;
                }
            }
            if (text != null)
            {
                try
                {
                    LoadError = null;
                    loaded = JsonSerializer.Deserialize<PilotSettings>(text, JsonOptions);
                    loaded?.Normalize();
                }
                catch (Exception ex)
                {
                    LoadError = ex.Message;
                    loaded = null;
                    // Keep the broken file for the user to inspect instead of silently overwriting it later.
                    try { File.Copy(Path, Path + ".broken", overwrite: true); } catch (Exception) { /* best effort */ }
                }
            }
            Current = loaded ?? new PilotSettings();
            Current.Normalize();
            return Current;
        }
    }

    public void Save(PilotSettings? settings = null)
    {
        lock (_lock)
        {
            if (settings != null) Current = settings;
            Current.Normalize();
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (_protectFile && File.Exists(Path))
            {
                // The existing file could not be read at startup: keep it as a backup before the first overwrite.
                try { File.Copy(Path, Path + ".bak", overwrite: true); _protectFile = false; }
                catch (Exception) { /* still unreadable: keep protecting */ }
                if (_protectFile) return;
            }
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, Path, overwrite: true);
        }
        Saved?.Invoke(Current);
    }

    /// <summary>Deep copy via JSON (used by the UI to edit a draft without touching live settings).</summary>
    public static PilotSettings Clone(PilotSettings settings)
    {
        var copy = JsonSerializer.Deserialize<PilotSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? new PilotSettings();
        copy.Normalize();
        return copy;
    }
}
