using SpirePilot.Core.Model;

namespace SpirePilot.Core.Team;

/// <summary>What became of one outgoing team message.</summary>
public sealed class Delivery
{
    /// <summary>The message as sent: the log entry's copy when text went out, else the stamped original.</summary>
    public TeamMessage Message { get; init; } = new();
    /// <summary>Log entry of the text; null when no text was sent (nobody addressed reads it, or the AI budget refused).</summary>
    public ChatEntry? Entry { get; init; }
    /// <summary>Co-op, and nobody addressed runs SpirePilot: no text was sent.</summary>
    public bool NoReader { get; init; }
    /// <summary>The AI's budget or dedupe refused the text; nothing was shown, not even an emote.</summary>
    public bool RateLimited { get; init; }
    /// <summary>The vanilla emote shown to the addressed teammates without SpirePilot ("ThumbUp" ...), or null.</summary>
    public string? Emote { get; init; }
    /// <summary>Someone addressed needed the kind's emote, but the AI's budget or dedupe refused it.</summary>
    public bool EmoteLimited { get; init; }
    /// <summary>Someone saw it: the text reached a reader, or the emote was shown.</summary>
    public bool Reached => Entry is { Delivered: true } || Emote != null;
}

/// <summary>
/// The one rule for sending a team message, used by the AI, the Team tab and POST /chat: teammates who run SpirePilot
/// get the text; the others cannot read it and get the kind's vanilla emote instead, also when the message goes to
/// everyone and only some have the mod. No text is sent when nobody addressed can read it.
/// </summary>
public static class TeamDelivery
{
    /// <summary>How many teammates a message to <paramref name="to"/> (0 = everyone) addresses, and how many of them read text.</summary>
    /// <param name="reads">Who runs SpirePilot by the live roster; default: the snapshot's <see cref="PlayerInfo.HasTeamLink"/>.</param>
    public static (int Addressed, int Readers) Reach(GameSnapshot snap, ulong to, Func<ulong, bool>? reads = null)
    {
        var addressed = snap.Teammates.Where(p => to == 0 || p.NetId == to).ToList();
        return (addressed.Count, addressed.Count(p => reads?.Invoke(p.NetId) ?? p.HasTeamLink));
    }

    /// <summary>
    /// A reply goes to the asker; an answer to a question for everyone goes to everyone, so other helpers see it is
    /// taken care of. A potion offer or question in answer to someone is for them alone: sent to everyone it would wait
    /// for answers from teammates it was never meant for.
    /// </summary>
    public static ulong ReplyRecipient(TeamMessage asked, TeamMessageKind kind) =>
        asked.To == 0 && kind is not (TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion) ? 0 : asked.From;

    /// <summary>
    /// The kind an answer to <paramref name="asked"/> goes out as: a potion offer or question in answer to a potion
    /// request is its "yes" (accept), which closes the request and makes it our promise. Sent as an offer, the asker's
    /// AI would read it as a counter-request it need not answer while we waited for its yes.
    /// </summary>
    public static TeamMessageKind AnswerKind(TeamMessage? asked, TeamMessageKind kind) =>
        asked is { Kind: TeamMessageKind.RequestPotion } && kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion
            ? TeamMessageKind.Accept
            : kind;

    /// <param name="snap">A recent capture for the co-op flag and the roster; null or solo: text only.</param>
    /// <param name="signal">Shows the kind's vanilla emote (<c>IGameBridge.TrySignal</c>); false when it could not.</param>
    /// <param name="reads">
    /// Who runs SpirePilot by the live roster (0 = anyone); default: the snapshot's <see cref="PlayerInfo.HasTeamLink"/>.
    /// It alone decides when the capture lists nobody addressed (a lobby, a run in transition).
    /// </param>
    public static Delivery Send(TeamChatHub hub, GameSnapshot? snap, TeamMessage msg, bool fromAi, Func<TeamMessageKind, bool> signal,
        Func<ulong, bool>? reads = null)
    {
        if (snap is not { IsMultiplayer: true })
        {
            var solo = hub.Send(msg, fromAi);
            return new Delivery { Message = solo?.Message ?? msg, Entry = solo, RateLimited = solo == null };
        }
        var (addressed, readers) = Reach(snap, msg.To, reads);
        if (addressed == 0 && reads?.Invoke(msg.To) == true) readers = 1;
        ChatEntry? entry = null;
        if (readers > 0)
        {
            entry = hub.Send(msg, fromAi);
            if (entry == null) return new Delivery { Message = msg, RateLimited = true };
        }
        string? emote = null;
        bool limited = false;
        if (addressed > readers && TeamEmotes.ReactionFor(msg.Kind) is { } reaction)
        {
            // A message that also went out as text has paid for its emote: only the repeat check applies.
            if (fromAi && !hub.TryUseAiBudget($"emote|{msg.Kind}|{msg.To}", textPaid: entry != null)) limited = true;
            else if (TrySignal(signal, msg.Kind)) emote = reaction;
        }
        return new Delivery
        {
            Message = entry?.Message ?? Stamped(hub, msg, fromAi),
            Entry = entry,
            NoReader = readers == 0,
            Emote = emote,
            EmoteLimited = limited,
        };
    }

    /// <summary>A message that never went through the hub, stamped like one that did (it may be remembered as a request).</summary>
    private static TeamMessage Stamped(TeamChatHub hub, TeamMessage msg, bool fromAi)
    {
        msg.From = hub.LocalNetId;
        if (string.IsNullOrEmpty(msg.FromName)) msg.FromName = hub.LocalName;
        msg.FromAi = fromAi;
        msg.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return msg.Sanitized();
    }

    private static bool TrySignal(Func<TeamMessageKind, bool> signal, TeamMessageKind kind)
    {
        try { return signal(kind); }
        catch (Exception) { return false; }
    }
}
