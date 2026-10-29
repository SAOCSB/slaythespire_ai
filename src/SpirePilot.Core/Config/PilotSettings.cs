using System.Text.Json.Serialization;

namespace SpirePilot.Core.Config;

/// <summary>What the pilot is allowed to do on its own.</summary>
public enum PilotMode
{
    /// <summary>The AI does nothing.</summary>
    Off,
    /// <summary>The AI plays every decision (combat, map, rewards, shop, events, menus).</summary>
    FullAuto,
    /// <summary>The AI only plays combat turns; the human handles everything else.</summary>
    CombatOnly,
    /// <summary>The AI handles everything except combat (map, rewards, shop, events...).</summary>
    OutOfCombatOnly,
    /// <summary>The AI only recommends: it shows what it would do, the human acts.</summary>
    Advisor,
}

public enum LlmProvider
{
    /// <summary>Any server speaking the OpenAI chat-completions protocol (OpenAI, DeepSeek, Qwen, Kimi, GLM, OpenRouter, Ollama, ...).</summary>
    OpenAiCompatible,
    /// <summary>Anthropic's native Messages API (Claude).</summary>
    Anthropic,
}

public enum RoutePreset
{
    Balanced,
    /// <summary>Seek elites for relics; accept more risk.</summary>
    Aggressive,
    /// <summary>Avoid elites, prefer rest sites.</summary>
    Safe,
    /// <summary>Prefer shops (deck thinning, buying).</summary>
    Shopper,
    /// <summary>Prefer unknown rooms / events.</summary>
    Explorer,
    /// <summary>Use <see cref="RouteSettings.CustomWeights"/>.</summary>
    Custom,
}

public enum CharacterPickPolicy
{
    /// <summary>Pick uniformly at random among allowed characters.</summary>
    Random,
    /// <summary>Cycle through the allowed characters run after run.</summary>
    RoundRobin,
    /// <summary>Always pick the first allowed character.</summary>
    First,
    /// <summary>Let the LLM choose among the allowed characters.</summary>
    AiChoice,
}

public sealed class LlmProfile
{
    public string Name { get; set; } = "default";
    public LlmProvider Provider { get; set; } = LlmProvider.OpenAiCompatible;
    /// <summary>Base URL. OpenAI-compatible: up to and including "/v1" (e.g. https://api.openai.com/v1). Anthropic: https://api.anthropic.com.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>API key. "env:NAME" reads the key from environment variable NAME. Empty for keyless local servers.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Sampling temperature. Only sent to OpenAI-compatible servers, and only when set.</summary>
    public double? Temperature { get; set; }
    /// <summary>Reasoning effort: OpenAI-compatible "reasoning_effort" / Anthropic "output_config.effort" (low, medium, high, ...). Empty = provider default.</summary>
    public string ReasoningEffort { get; set; } = "";
    /// <summary>Effort for routine combat turns (often lower than <see cref="ReasoningEffort"/> to save time). Empty = same.</summary>
    public string CombatReasoningEffort { get; set; } = "";
    /// <summary>Ask the server for JSON-only output (OpenAI response_format / Anthropic structured outputs).</summary>
    public bool JsonMode { get; set; } = true;
    /// <summary>Additional HTTP headers sent with every request.</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    /// <summary>JSON object whose properties are merged into the request body (provider-specific knobs).</summary>
    public string ExtraBodyJson { get; set; } = "";
    /// <summary>Anthropic only: value of the anthropic-version header.</summary>
    public string AnthropicVersion { get; set; } = "2026-10-21";
    /// <summary>Anthropic only: let the API retry a declined request on a fallback model (server-side fallbacks, beta).</summary>
    public bool AnthropicServerFallback { get; set; } = true;
    /// <summary>Anthropic only: mark the stable system prompt as cacheable.</summary>
    public bool PromptCaching { get; set; } = true;
    /// <summary>Optional HTTP(S) proxy for this profile, e.g. http://127.0.0.1:7890. Empty = system proxy settings.</summary>
    public string ProxyUrl { get; set; } = "";

    public LlmProfile Clone()
    {
        var copy = (LlmProfile)MemberwiseClone();
        copy.ExtraHeaders = new Dictionary<string, string>(ExtraHeaders ?? new());
        return copy;
    }
}

public sealed class ControlSettings
{
    public PilotMode Mode { get; set; } = PilotMode.Off;
    /// <summary>When true the pilot waits for the user to press "Step" before executing each decision.</summary>
    public bool StepMode { get; set; }
    /// <summary>Pause between executed actions so humans can follow along.</summary>
    public int ActionDelayMs { get; set; } = 400;
    /// <summary>Start another run automatically after a run ends (full-auto only).</summary>
    public bool AutoStartNextRun { get; set; }
    /// <summary>Maximum number of runs to start automatically (0 = unlimited).</summary>
    public int MaxAutoRuns { get; set; }
    /// <summary>Execute the only legal action (or obviously forced ones) without asking the LLM.</summary>
    public bool SkipTrivialDecisions { get; set; } = true;
    /// <summary>Use the rule-based fallback policy when the LLM fails repeatedly instead of stopping.</summary>
    public bool FallbackOnLlmFailure { get; set; } = true;
    /// <summary>Let the LLM return a multi-action plan for the current combat turn.</summary>
    public bool AllowTurnPlans { get; set; } = true;
}

public sealed class RouteSettings
{
    public RoutePreset Preset { get; set; } = RoutePreset.Balanced;
    /// <summary>Per room type weight used by <see cref="RoutePreset.Custom"/>. Keys are MapNodeType names.</summary>
    public Dictionary<string, double> CustomWeights { get; set; } = new()
    {
        ["Monster"] = 1.0, ["Elite"] = 1.0, ["RestSite"] = 1.0, ["Shop"] = 1.0, ["Treasure"] = 1.5, ["Event"] = 1.0,
    };
    /// <summary>Path the user pinned in the route tab (node keys in travel order), for <see cref="PinnedAct"/>.</summary>
    public List<string> PinnedPath { get; set; } = new();
    public int PinnedAct { get; set; }
    /// <summary>Seed of the run the path was pinned in: a pin never applies to another run's map.</summary>
    public string PinnedSeed { get; set; } = "";
    /// <summary>Free-text routing instructions passed to the AI.</summary>
    public string Instruction { get; set; } = "";
    /// <summary>Ping the chosen node on the map so teammates can see where the AI wants to go.</summary>
    public bool PingChoiceToTeammates { get; set; } = true;
    /// <summary>When true the AI picks the map node itself; when false it follows the route planner's best path.</summary>
    public bool LetAiChooseNodes { get; set; } = true;
}

public sealed class CharacterSettings
{
    /// <summary>Character ids the AI may pick. Empty = any unlocked character.</summary>
    public List<string> Allowed { get; set; } = new();
    public CharacterPickPolicy Policy { get; set; } = CharacterPickPolicy.Random;
    /// <summary>Ascension to start new runs at; -1 = highest unlocked.</summary>
    public int Ascension { get; set; }
    /// <summary>Round-robin cursor (persisted so rotation survives restarts).</summary>
    public int RoundRobinCursor { get; set; }
}

public sealed class TeamSettings
{
    /// <summary>Let the AI answer teammates' requests and questions.</summary>
    public bool AutoReply { get; set; } = true;
    /// <summary>Let the AI start requests itself (ask for potions, debuffs, ...).</summary>
    public bool ProactiveRequests { get; set; } = true;
    /// <summary>Let the AI use its potions on teammates when asked.</summary>
    public bool AllowGivingPotions { get; set; } = true;
    /// <summary>Hard cap of AI-originated team messages per minute.</summary>
    public int MaxAiMessagesPerMinute { get; set; } = 4;
    /// <summary>Show received messages as speech bubbles above the sender's character.</summary>
    public bool SpeechBubbles { get; set; } = true;
    /// <summary>Language for messages the AI writes to teammates ("auto" = UI language).</summary>
    public string MessageLanguage { get; set; } = "auto";
}

public sealed class StrategySettings
{
    /// <summary>Standing play-style instructions (e.g. "build around strength", "never take curses").</summary>
    public string Instruction { get; set; } = "";
    /// <summary>Include card descriptions in prompts (more tokens, better play).</summary>
    public bool VerboseCards { get; set; } = true;
    /// <summary>Include the sorted draw / discard pile in combat prompts.</summary>
    public bool IncludePiles { get; set; } = true;
    /// <summary>How many recent decisions to keep in the prompt as history.</summary>
    public int HistoryLength { get; set; } = 12;
}

public sealed class HttpApiSettings
{
    /// <summary>Expose the local HTTP API (127.0.0.1 only) so external AI tools can read state and act.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = 17654;
    /// <summary>Optional bearer token required by the HTTP API. Empty = no auth (localhost only).</summary>
    public string Token { get; set; } = "";
}

public sealed class UiSettings
{
    /// <summary>"auto", "zh" or "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Godot key name toggling the overlay.</summary>
    public string ToggleKey { get; set; } = "F8";
    public bool ShowOnStart { get; set; } = true;
    public float PanelX { get; set; } = 24;
    public float PanelY { get; set; } = 96;
    public float PanelWidth { get; set; } = 460;
    public float PanelHeight { get; set; } = 620;
    public float Scale { get; set; } = 1.0f;
}

/// <summary>All persisted settings of the mod.</summary>
public sealed class PilotSettings
{
    public int Version { get; set; } = 1;
    public ControlSettings Control { get; set; } = new();
    public RouteSettings Route { get; set; } = new();
    public CharacterSettings Characters { get; set; } = new();
    public TeamSettings Team { get; set; } = new();
    public StrategySettings Strategy { get; set; } = new();
    public HttpApiSettings HttpApi { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public List<LlmProfile> Profiles { get; set; } = new() { LlmPresets.Default() };
    public string ActiveProfile { get; set; } = "default";
    /// <summary>Log full prompts and responses to the mod log (debugging; may contain a lot of text).</summary>
    public bool LogPrompts { get; set; }
    /// <summary>Append every executed decision (menu offered, choice, result) to traces/*.jsonl in the data folder.</summary>
    public bool SaveDecisionTraces { get; set; } = true;

    [JsonIgnore]
    public LlmProfile? Active =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles.FirstOrDefault();

    /// <summary>Repairs missing / out-of-range values after loading an older or hand-edited file.</summary>
    public void Normalize()
    {
        Control ??= new();
        Route ??= new();
        Characters ??= new();
        Team ??= new();
        Strategy ??= new();
        HttpApi ??= new();
        Ui ??= new();
        Profiles ??= new();
        // Hand-edited files may contain nulls in lists; enums may hold undefined numbers.
        Profiles.RemoveAll(p => p == null);
        if (Profiles.Count == 0) Profiles.Add(LlmPresets.Default());
        if (!Enum.IsDefined(Control.Mode)) Control.Mode = PilotMode.Off;
        if (!Enum.IsDefined(Route.Preset)) Route.Preset = RoutePreset.Balanced;
        if (!Enum.IsDefined(Characters.Policy)) Characters.Policy = CharacterPickPolicy.Random;
        foreach (var p in Profiles)
        {
            if (!Enum.IsDefined(p.Provider)) p.Provider = LlmProvider.OpenAiCompatible;
            p.ExtraHeaders ??= new();
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "default" : p.Name.Trim();
            p.BaseUrl = (p.BaseUrl ?? "").Trim();
            p.Model = (p.Model ?? "").Trim();
            p.ApiKey = (p.ApiKey ?? "").Trim();
            p.ReasoningEffort ??= "";
            p.CombatReasoningEffort ??= "";
            p.ExtraBodyJson ??= "";
            p.AnthropicVersion = string.IsNullOrWhiteSpace(p.AnthropicVersion) ? "2026-10-21" : p.AnthropicVersion.Trim();
            p.MaxTokens = Math.Clamp(p.MaxTokens, 256, 128000);
            p.TimeoutSeconds = Math.Clamp(p.TimeoutSeconds, 10, 900);
        }
        if (Profiles.All(p => p.Name != ActiveProfile)) ActiveProfile = Profiles[0].Name;
        Control.ActionDelayMs = Math.Clamp(Control.ActionDelayMs, 0, 10000);
        Control.MaxAutoRuns = Math.Max(0, Control.MaxAutoRuns);
        Route.CustomWeights ??= new();
        Route.PinnedPath ??= new();
        Route.PinnedPath.RemoveAll(string.IsNullOrWhiteSpace);
        Route.PinnedSeed ??= "";
        Route.Instruction ??= "";
        Characters.Allowed ??= new();
        Characters.Allowed.RemoveAll(string.IsNullOrWhiteSpace);
        Characters.Ascension = Math.Clamp(Characters.Ascension, -1, 20);
        Team.MaxAiMessagesPerMinute = Math.Clamp(Team.MaxAiMessagesPerMinute, 0, 30);
        Team.MessageLanguage ??= "auto";
        Strategy.Instruction ??= "";
        Strategy.HistoryLength = Math.Clamp(Strategy.HistoryLength, 0, 50);
        HttpApi.Port = Math.Clamp(HttpApi.Port, 1024, 65535);
        HttpApi.Token ??= "";
        Ui.Language ??= "auto";
        Ui.ToggleKey = string.IsNullOrWhiteSpace(Ui.ToggleKey) ? "F8" : Ui.ToggleKey;
        Ui.Scale = Math.Clamp(Ui.Scale, 0.5f, 3f);
        Ui.PanelWidth = Math.Clamp(Ui.PanelWidth, 320, 1600);
        Ui.PanelHeight = Math.Clamp(Ui.PanelHeight, 300, 1400);
    }
}
