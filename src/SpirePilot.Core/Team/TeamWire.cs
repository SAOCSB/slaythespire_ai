using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SpirePilot.Core.Team;

/// <summary>
/// Binary envelope for team messages sent over the game's own multiplayer transport as raw packets.
///
/// Layout (little endian):
///   [0..4]   magic 0xF7 'S' 'P' 'L' 'T'   — never a valid vanilla/RitsuLib packet start, so receivers can demux cheaply
///   [5]      wire version (1)
///   [6]      payload type (1 = TeamMessage JSON)
///   [7..10]  payload length N (uint32)
///   [11..]   payload (UTF-8 JSON, N bytes)
///   [..+4]   CRC-32 of bytes [5 .. 11+N)
/// </summary>
public static class TeamWire
{
    public static readonly byte[] Magic = { 0xF7, (byte)'S', (byte)'P', (byte)'L', (byte)'T' };
    public const byte Version = 1;
    public const byte PayloadTeamMessage = 1;
    public const int HeaderSize = 11;
    public const int MaxPayloadBytes = 16 * 1024;

    public enum ParseOutcome
    {
        Ok,
        /// <summary>Not one of our packets: let the game handle it.</summary>
        NotOurs,
        /// <summary>Our magic but broken / unsupported: drop it (never feed it to the game).</summary>
        Rejected,
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool MatchesMagic(ReadOnlySpan<byte> packet) =>
        packet.Length >= Magic.Length && packet[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encode(TeamMessage message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message.Sanitized(), Json);
        if (payload.Length > MaxPayloadBytes) throw new ArgumentException("Team message too large.");
        var packet = new byte[HeaderSize + payload.Length + 4];
        Magic.CopyTo(packet, 0);
        packet[5] = Version;
        packet[6] = PayloadTeamMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), (uint)payload.Length);
        payload.CopyTo(packet, HeaderSize);
        uint crc = Crc32.Compute(packet.AsSpan(5, HeaderSize - 5 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + payload.Length, 4), crc);
        return packet;
    }

    public static ParseOutcome TryDecode(ReadOnlySpan<byte> packet, out TeamMessage? message)
    {
        message = null;
        if (!MatchesMagic(packet)) return ParseOutcome.NotOurs;
        if (packet.Length < HeaderSize + 4) return ParseOutcome.Rejected;
        if (packet[5] != Version || packet[6] != PayloadTeamMessage) return ParseOutcome.Rejected;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(7, 4));
        if (length > MaxPayloadBytes || packet.Length != HeaderSize + (int)length + 4) return ParseOutcome.Rejected;
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + (int)length, 4));
        if (Crc32.Compute(packet.Slice(5, HeaderSize - 5 + (int)length)) != expected) return ParseOutcome.Rejected;
        try
        {
            message = JsonSerializer.Deserialize<TeamMessage>(packet.Slice(HeaderSize, (int)length), Json)?.Sanitized();
        }
        catch (JsonException)
        {
            return ParseOutcome.Rejected;
        }
        return message == null ? ParseOutcome.Rejected : ParseOutcome.Ok;
    }

    /// <summary>Encodes a string as compact JSON for logs.</summary>
    public static string Describe(TeamMessage m) => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(m, Json));
}

/// <summary>Standard CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
