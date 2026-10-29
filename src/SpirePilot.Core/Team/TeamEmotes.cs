using SpirePilot.Core.Text;

namespace SpirePilot.Core.Team;

/// <summary>
/// Vanilla reaction emotes, both ways. Out: the emote a teammate without SpirePilot sees instead of a message. In: a
/// teammate's emote as an inbox message: thumbs up / down answer the newest request that teammate saw (shown as an
/// emote, or read as text by a linked teammate); "!", "?" and every other emote become a short chat line, of which only
/// "!" and "?" ask the AI for a reaction.
/// </summary>
public static class TeamEmotes
{
    /// <summary>
    /// The game's reaction (<c>ReactionType</c> name) closest to a message kind, or null when none fits (chat, route
    /// suggestions): requests "!", asking or offering a potion "?", accept / decline thumbs up / down.
    /// </summary>
    public static string? ReactionFor(TeamMessageKind kind) => kind switch
    {
        TeamMessageKind.Accept => "ThumbUp",
        TeamMessageKind.Decline => "ThumbDown",
        TeamMessageKind.RequestPotion or TeamMessageKind.RequestBlock or TeamMessageKind.RequestDebuff or
            TeamMessageKind.RequestFocus => "Exclamation",
        TeamMessageKind.AskNeedPotion or TeamMessageKind.OfferPotion => "QuestionMark",
        _ => null,
    };

    /// <param name="reaction">The game's reaction type name ("ThumbUp", "Exclamation" ...).</param>
    /// <param name="asked">
    /// The newest request this player showed <paramref name="from"/> (or everyone) as an emote, and whether it is still
    /// open (<c>AgentMemory.LastSignalTo</c>; for a linked teammate the newest open one it read, <c>LastOpenTo</c>). A
    /// thumbs up answers it only while it is open: once it is closed the emote answers nothing we know of, and never an
    /// older request the teammate may not have seen.
    /// </param>
    /// <param name="fromLinkedPeer">
    /// The sender runs SpirePilot and someone in the party does not: the sender can answer us in text, so its emote is
    /// most likely meant for that teammate, and is context only.
    /// </param>
    public static TeamMessage ToMessage(string reaction, ulong from, string fromName, ulong localId, (string Id, bool Open)? asked,
        bool fromLinkedPeer = false, DateTimeOffset? now = null)
    {
        var kind = fromLinkedPeer ? TeamMessageKind.Chat : reaction switch
        {
            "ThumbUp" => TeamMessageKind.Accept,
            "ThumbDown" => TeamMessageKind.Decline,
            _ => TeamMessageKind.Chat,
        };
        var answers = kind != TeamMessageKind.Chat && asked is { Open: true } a ? a.Id : null;
        return new TeamMessage
        {
            Id = IdFor(reaction, from, now ?? DateTimeOffset.UtcNow, fromLinkedPeer ? null : asked?.Id),
            Kind = kind,
            From = from,
            FromName = fromName,
            To = answers != null ? localId : 0,
            ReplyTo = answers,
            Text = L10n.T("team.emote_signal", fromName.Length > 0 ? fromName : from.ToString(), Symbol(reaction)),
            Emote = reaction,
            EmoteForOthers = fromLinkedPeer,
        };
    }

    /// <summary>
    /// An emote that asks the AI for a reaction ("!", "?" from a teammate without the mod). Hearts, skulls and the like
    /// are context only: waking the AI (a paid call that also weighs undoing its end of turn) for each of them is not
    /// worth it.
    /// </summary>
    public static bool AsksForReaction(TeamMessage m) => Asks(m.Emote) && !m.EmoteForOthers;

    /// <summary>The reaction asks something ("!", "?"), unlike an answer or a feeling.</summary>
    public static bool Asks(string? reaction) => reaction is "Exclamation" or "QuestionMark";

    /// <summary>
    /// The same emote from the same teammate within one 10-second window, after the same question, gets the same id (a
    /// hash, as short as a random id since the prompt shows it), so the hub's dedupe drops repeated taps; a tap after a
    /// new question is a new answer.
    /// </summary>
    private static string IdFor(string reaction, ulong from, DateTimeOffset now, string? asked)
    {
        ulong hash = 14695981039346656037;
        foreach (var c in $"{now.ToUnixTimeSeconds() / 10}|{from}|{reaction}|{asked}") hash = (hash ^ c) * 1099511628211;
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
