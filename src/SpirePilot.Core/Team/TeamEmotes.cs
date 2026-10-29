using SpirePilot.Core.Text;

namespace SpirePilot.Core.Team;

/// <summary>
/// A vanilla reaction emote from a teammate without SpirePilot (the only way they can talk to the AI), as an inbox
/// message: thumbs up / down answer the AI's newest open request to that teammate or to everyone; "!", "?" and every
/// other emote become a short chat line, of which only "!" and "?" ask the AI for a reaction.
/// </summary>
public static class TeamEmotes
{
    /// <param name="reaction">The game's reaction type name ("ThumbUp", "Exclamation" ...).</param>
    /// <param name="outgoing">The AI's open requests, oldest first (<c>AgentMemory.OutgoingRequests</c>).</param>
    public static TeamMessage ToMessage(string reaction, ulong from, string fromName, ulong localId, IReadOnlyList<TeamMessage> outgoing,
        DateTimeOffset? now = null)
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
            Id = IdFor(reaction, from, now ?? DateTimeOffset.UtcNow),
            Kind = kind,
            From = from,
            FromName = fromName,
            To = answered != null ? localId : 0,
            ReplyTo = answered?.Id,
            Text = L10n.T("team.emote_signal", fromName.Length > 0 ? fromName : from.ToString(), Symbol(reaction)),
            Emote = reaction,
        };
    }

    /// <summary>
    /// An emote that asks the AI for a reaction ("!", "?"). Hearts, skulls and the like are context only: waking the
    /// AI (a paid call that also weighs undoing its end of turn) for each of them is not worth it.
    /// </summary>
    public static bool AsksForReaction(TeamMessage m) => m.Emote is "Exclamation" or "QuestionMark";

    /// <summary>
    /// The same emote from the same teammate within one 10-second window gets the same id (a hash, as short as a random
    /// id since the prompt shows it), so the hub's dedupe drops repeated taps.
    /// </summary>
    private static string IdFor(string reaction, ulong from, DateTimeOffset now)
    {
        ulong hash = 14695981039346656037;
        foreach (var c in $"{now.ToUnixTimeSeconds() / 10}|{from}|{reaction}") hash = (hash ^ c) * 1099511628211;
        return $"em{hash & 0xFFFFFFFFFF:x10}";
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
