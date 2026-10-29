using System.Text.Json.Serialization;

namespace SpirePilot.Core.Team;

/// <summary>
/// Kinds of team messages. Every message also carries human-readable <see cref="TeamMessage.Text"/>, so a
/// human teammate can read any kind; the structured fields let an AI teammate act on it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TeamMessageKind>))]
public enum TeamMessageKind
{
    /// <summary>Free-form chat.</summary>
    Chat,
    /// <summary>"Could someone give me a potion?" (optionally a specific potion id/kind).</summary>
    RequestPotion,
    /// <summary>"I can give you this potion" (Potion = id/name).</summary>
    OfferPotion,
    /// <summary>"Does anyone need a potion?"</summary>
    AskNeedPotion,
    /// <summary>"Please apply this debuff (Debuff) to that enemy (Target)."</summary>
    RequestDebuff,
    /// <summary>"Please focus this enemy (Target)."</summary>
    RequestFocus,
    /// <summary>"Please protect / block for me."</summary>
    RequestBlock,
    /// <summary>Route suggestion (Target = map node key).</summary>
    SuggestRoute,
    /// <summary>Positive answer to a request (ReplyTo = request id).</summary>
    Accept,
    /// <summary>Negative answer to a request (ReplyTo = request id).</summary>
    Decline,
    /// <summary>Announces capabilities: sent when the team link comes up (Text = mod version, IsAi = pilot active).</summary>
    Hello,
}

/// <summary>One message on the team channel.</summary>
public sealed class TeamMessage
{
    /// <summary>Random id (dedupe / replies).</summary>
    public string Id { get; set; } = NewId();
    public TeamMessageKind Kind { get; set; }
    /// <summary>Net id of the sender. Filled / verified by the transport, never trusted from the payload alone.</summary>
    public ulong From { get; set; }
    /// <summary>Net id of the addressee; 0 = everyone.</summary>
    public ulong To { get; set; }
    /// <summary>Display name of the sender (informational).</summary>
    public string FromName { get; set; } = "";
    /// <summary>Human-readable text (always present).</summary>
    public string Text { get; set; } = "";
    /// <summary>True when the local AI pilot wrote the message (as opposed to the human player).</summary>
    public bool FromAi { get; set; }
    /// <summary>Potion id or name for potion messages.</summary>
    public string? Potion { get; set; }
    /// <summary>Debuff / power id or name for <see cref="TeamMessageKind.RequestDebuff"/>.</summary>
    public string? Debuff { get; set; }
    /// <summary>Target reference: enemy name / key, or map node key.</summary>
    public string? Target { get; set; }
    /// <summary>Id of the message this one answers.</summary>
    public string? ReplyTo { get; set; }
    /// <summary>Unix time in milliseconds (sender clock).</summary>
    public long Time { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public bool IsRequest => Kind is TeamMessageKind.RequestPotion or TeamMessageKind.AskNeedPotion or
        TeamMessageKind.RequestDebuff or TeamMessageKind.RequestFocus or TeamMessageKind.RequestBlock or
        TeamMessageKind.OfferPotion;

    /// <summary>Clamp field sizes so a malformed or hostile message cannot blow up the UI or the prompt.</summary>
    public TeamMessage Sanitized()
    {
        // Peers are untrusted: one line per field, no control characters, bounded length.
        static string? Cut(string? s, int max)
        {
            if (s == null) return null;
            if (s.Length > max) s = s[..max];
            return string.Create(s.Length, s, static (span, src) =>
            {
                for (int i = 0; i < src.Length; i++) span[i] = char.IsControl(src[i]) ? ' ' : src[i];
            });
        }
        static string? IdOf(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var clean = new string(s.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_')).Take(32).ToArray());
            return clean.Length > 0 ? clean : null;
        }
        return new TeamMessage
        {
            Id = IdOf(Id) ?? NewId(),
            Kind = Enum.IsDefined(Kind) ? Kind : TeamMessageKind.Chat,
            From = From,
            To = To,
            FromName = Cut(FromName, 48) ?? "",
            Text = Cut(Text, 400) ?? "",
            FromAi = FromAi,
            Potion = Cut(Potion, 64),
            Debuff = Cut(Debuff, 64),
            Target = Cut(Target, 64),
            ReplyTo = IdOf(ReplyTo),
            Time = Time,
        };
    }
}
