using SpirePilot.Core.Team;

namespace SpirePilot.Core.Agent;

/// <summary>Where a decision came from.</summary>
public enum DecisionSource
{
    Llm,
    /// <summary>A remaining step of a multi-action plan the LLM returned earlier.</summary>
    Plan,
    /// <summary>Only one sensible option existed; no LLM call was made.</summary>
    Trivial,
    /// <summary>Rule-based fallback after LLM failure.</summary>
    Fallback,
    /// <summary>A hard rule from the user's settings (pinned route, character restriction ...).</summary>
    Rule,
}

/// <summary>A message the AI wants to send to teammates.</summary>
public sealed class TeamMessageIntent
{
    /// <summary>"all", a player name, or a net id.</summary>
    public string To { get; set; } = "all";
    public TeamMessageKind Kind { get; set; } = TeamMessageKind.Chat;
    public string Text { get; set; } = "";
    public string? Potion { get; set; }
    public string? Debuff { get; set; }
    public string? Target { get; set; }
    public string? ReplyTo { get; set; }
}

/// <summary>The agent's answer for one decision point.</summary>
public sealed class AgentDecision
{
    public DecisionSource Source { get; set; }
    /// <summary>Short reasoning shown in the UI log.</summary>
    public string Thought { get; set; } = "";
    /// <summary>Action ids from the snapshot menu; the first one is executed now.</summary>
    public List<string> ActionIds { get; set; } = new();
    /// <summary>Semantic keys of the remaining plan steps (after the first action).</summary>
    public List<string> PlanKeys { get; set; } = new();
    public List<TeamMessageIntent> Messages { get; set; } = new();
    /// <summary>Note the AI wants to remember for later decisions.</summary>
    public string? Memo { get; set; }
    /// <summary>Ids of inbox messages this decision has taken into account.</summary>
    public List<string> HandledInboxIds { get; set; } = new();
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public TimeSpan Elapsed { get; set; }

    public string? FirstActionId => ActionIds.Count > 0 ? ActionIds[0] : null;
}
