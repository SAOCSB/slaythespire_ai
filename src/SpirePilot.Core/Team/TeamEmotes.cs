using SpirePilot.Core.Text;

namespace SpirePilot.Core.Team;

/// <summary>
/// A vanilla reaction emote from a teammate without SpirePilot (the only way they can talk to the AI), as an inbox
/// message: thumbs up / down answer the AI's newest open request to that teammate or to everyone; "!", "?" and every
/// other emote become a short chat line.
/// </summary>
public static class TeamEmotes
{
    /// <param name="reaction">The game's reaction type name ("ThumbUp", "Exclamation" ...).</param>
    /// <param name="outgoing">The AI's open requests, oldest first (<c>AgentMemory.OutgoingRequests</c>).</param>
    public static TeamMessage ToMessage(string reaction, ulong from, string fromName, ulong localId, IReadOnlyList<TeamMessage> outgoing)
    {
        var kind = reaction switch
        {
            "ThumbUp" => TeamMessageKind.Accept,
            "ThumbDown" => TeamMessageKind.Decline,
            _ => TeamMessageKind.Chat,
        };
        var answered = kind == TeamMessageKind.Chat ? null : outgoing.LastOrDefault(m => m.To == from || m.To == 0);
        return new TeamMessage
        {
            Kind = kind,
            From = from,
            FromName = fromName,
            To = answered != null ? localId : 0,
            ReplyTo = answered?.Id,
            Text = L10n.T("team.emote_signal", fromName.Length > 0 ? fromName : from.ToString(), Symbol(reaction)),
            IsEmote = true,
        };
    }

    private static string Symbol(string reaction) => reaction switch
    {
        "ThumbUp" => "👍",
        "ThumbDown" => "👎",
        "Exclamation" => "!",
        "QuestionMark" => "?",
        "Heart" => "❤",
        "Skull" => "💀",
        _ => reaction,
    };
}
