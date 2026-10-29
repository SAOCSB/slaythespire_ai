using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

public class TeamWireTests
{
    [Fact]
    public void Roundtrips_a_message()
    {
        var m = new TeamMessage { Kind = TeamMessageKind.RequestPotion, From = 42, To = 7, FromName = "小明", Text = "能给我一瓶药吗？", Potion = "BLOOD_POTION" };
        var bytes = TeamWire.Encode(m);
        Assert.True(TeamWire.MatchesMagic(bytes));
        Assert.Equal(TeamWire.ParseOutcome.Ok, TeamWire.TryDecode(bytes, out var back));
        Assert.Equal(m.Id, back!.Id);
        Assert.Equal(TeamMessageKind.RequestPotion, back.Kind);
        Assert.Equal("能给我一瓶药吗？", back.Text);
        Assert.Equal("BLOOD_POTION", back.Potion);
        Assert.Equal(7ul, back.To);
    }

    [Fact]
    public void Foreign_packets_are_not_ours()
    {
        Assert.Equal(TeamWire.ParseOutcome.NotOurs, TeamWire.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6 }, out _));
        Assert.Equal(TeamWire.ParseOutcome.NotOurs, TeamWire.TryDecode(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void Corruption_is_rejected()
    {
        var bytes = TeamWire.Encode(new TeamMessage { Text = "hello" });
        bytes[TeamWire.HeaderSize + 2] ^= 0x55;
        Assert.Equal(TeamWire.ParseOutcome.Rejected, TeamWire.TryDecode(bytes, out _));
        Assert.Equal(TeamWire.ParseOutcome.Rejected, TeamWire.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _));
    }

    [Fact]
    public void Trailing_bytes_from_other_mods_are_ignored()
    {
        var bytes = TeamWire.Encode(new TeamMessage { Text = "with trailer" });
        var withTrailer = bytes.Concat(Enumerable.Repeat((byte)0xAB, 36)).ToArray();
        Assert.Equal(TeamWire.ParseOutcome.Ok, TeamWire.TryDecode(withTrailer, out var back));
        Assert.Equal("with trailer", back!.Text);
    }

    [Fact]
    public void Oversized_fields_are_clamped()
    {
        var bytes = TeamWire.Encode(new TeamMessage { Text = new string('x', 5000) });
        Assert.Equal(TeamWire.ParseOutcome.Ok, TeamWire.TryDecode(bytes, out var back));
        Assert.Equal(400, back!.Text.Length);
    }

    [Fact]
    public void Crc32_matches_reference_vector()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }
}

public class TeamChatHubTests
{
    private sealed class FakeTransport : ITeamTransport
    {
        public List<TeamMessage> Sent { get; } = new();
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyCollection<ulong> LinkedPeers { get; set; } = new ulong[] { 2 };
        public bool Send(TeamMessage message) { Sent.Add(message); return true; }
    }

    [Fact]
    public void Dedupes_and_ignores_self_echo()
    {
        var hub = new TeamChatHub { LocalNetId = 1 };
        var m = new TeamMessage { From = 2, Text = "hi" };
        Assert.True(hub.Receive(m));
        Assert.False(hub.Receive(m));
        Assert.False(hub.Receive(new TeamMessage { From = 1, Text = "me" }));
        Assert.Single(hub.PeekAiInbox());
    }

    [Fact]
    public void Only_messages_for_us_reach_the_inbox()
    {
        var hub = new TeamChatHub { LocalNetId = 1 };
        hub.Receive(new TeamMessage { From = 2, To = 3, Text = "for someone else" });
        hub.Receive(new TeamMessage { From = 2, To = 1, Text = "for me" });
        hub.Receive(new TeamMessage { From = 2, To = 0, Text = "for all" });
        hub.Receive(new TeamMessage { From = 2, To = 0, Kind = TeamMessageKind.Hello, Text = "hello" });
        var inbox = hub.PeekAiInbox();
        Assert.Equal(new[] { "for me", "for all" }, inbox.Select(i => i.Text));
        hub.MarkRead(inbox.Take(1).Select(i => i.Id));
        Assert.Single(hub.PeekAiInbox());
    }

    [Fact]
    public void Rate_limits_ai_messages_but_not_human_ones()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var transport = new FakeTransport();
        var hub = new TeamChatHub(() => now) { LocalNetId = 1, Transport = transport, MaxAiMessagesPerMinute = 2 };
        Assert.NotNull(hub.Send(new TeamMessage { Text = "1" }, fromAi: true));
        Assert.NotNull(hub.Send(new TeamMessage { Text = "2" }, fromAi: true));
        Assert.Null(hub.Send(new TeamMessage { Text = "3" }, fromAi: true));
        Assert.NotNull(hub.Send(new TeamMessage { Text = "human" }, fromAi: false));
        now = now.AddSeconds(61);
        Assert.NotNull(hub.Send(new TeamMessage { Text = "4" }, fromAi: true));
        Assert.Equal(4, transport.Sent.Count);
        Assert.All(transport.Sent, m => Assert.Equal(1ul, m.From));
    }

    [Fact]
    public void Undeliverable_messages_are_logged_locally()
    {
        var hub = new TeamChatHub { LocalNetId = 1, Transport = new FakeTransport { IsAvailable = false } };
        var entry = hub.Send(new TeamMessage { Text = "anyone?" }, fromAi: false);
        Assert.False(entry!.Delivered);
        Assert.Single(hub.Log);
    }
}
