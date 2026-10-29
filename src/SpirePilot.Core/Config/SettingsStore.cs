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

    public event Action<PilotSettings>? Saved;

    public SettingsStore(string path) => Path = path;

    public PilotSettings Load()
    {
        lock (_lock)
        {
            LoadError = null;
            PilotSettings? loaded = null;
            try
            {
                if (File.Exists(Path)) loaded = JsonSerializer.Deserialize<PilotSettings>(File.ReadAllText(Path), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LoadError = ex.Message;
                // Keep the broken file for the user to inspect instead of silently overwriting it later.
                try { File.Copy(Path, Path + ".broken", overwrite: true); } catch (Exception) { /* best effort */ }
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
