using System.Text.RegularExpressions;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>
/// Co-op findings of the third review round: a "yes" stays a visible promise and waits for a capture that can act on
/// it, an offer in answer to a potion request is the yes (and an AI's offer to another AI waits for none), the
/// player's answer is the only one, requests end with a fight the AI did not play, another player's question is what a
/// thumbs up answers, an emote the game could not show is reported, and the preview leaves out what a reply-only call
/// leaves out.
/// </summary>
public class CoopRound3Tests
{
    private sealed class Client : ILlmClient
    {
        public Func<LlmRequest, int, string> Answer { get; set; } = (_, _) => "{\"thought\":\"t\",\"actions\":[\"a0\"]}";
        public List<LlmRequest> Requests { get; } = new();
        public string Describe => "test";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            int n;
            lock (Requests)
            {
                Requests.Add(request);
                n = Requests.Count;
            }
            return Task.FromResult(new LlmResponse { Text = Answer(request, n) });
        }
        public string Prompt(int i) { lock (Requests) return Requests[i].Messages[0].Content; }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private sealed class Bridge : IGameBridge
    {
        private readonly object _lock = new();
        private GameSnapshot? _current;
        private long _seq = 100;
        public List<ActionOption> Executed { get; } = new();
        public void Set(GameSnapshot? s) { lock (_lock) _current = s; }
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_current != null) _current.Sequence = ++_seq;
                return Task.FromResult(_current);
            }
        }
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct)
        {
            lock (_lock) Executed.Add(action);
            return Task.FromResult(ActionResult.Ok());
        }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
        public bool TrySignal(TeamMessageKind kind) => true;
    }

    private sealed class Transport : ITeamTransport
    {
        public List<TeamMessage> Sent { get; } = new();
        public bool IsAvailable => true;
        public IReadOnlyCollection<ulong> LinkedPeers => new ulong[] { 2 };
        public bool Send(TeamMessage message) { lock (Sent) Sent.Add(message); return true; }
    }

    private static PilotSettings Settings()
    {
        var s = new PilotSettings();
        s.Profiles[0].ApiKey = "k";
        s.Control.Mode = PilotMode.FullAuto;
        s.Control.ActionDelayMs = 0;
        s.Normalize();
        return s;
    }

    private static PlayerInfo Bob(bool ai = false) => new() { NetId = 2, Name = "Bob", Hp = 30, MaxHp = 70, HasTeamLink = true, IsAiControlled = ai };
    private static PlayerInfo Cara() => new() { NetId = 3, Name = "Cara", Hp = 30, MaxHp = 70, HasTeamLink = false };

    private static GameSnapshot Coop(params PlayerInfo[] mates)
    {
        var s = TestData.Combat();
        s.IsMultiplayer = true;
        s.Players.AddRange(mates);
        return s;
    }

    private static GameSnapshot Waiting(params PlayerInfo[] mates)
    {
        var s = Coop(mates);
        s.CanAct = false;
        s.WaitReason = "enemy turn";
        s.Actions.Clear();
        return s;
    }

    private static GameSnapshot UndoOnly(params PlayerInfo[] mates)
    {
        var s = Coop(mates);
        s.WaitReason = "waiting for teammates";
        s.Actions.Clear();
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.UndoEndTurn, Label = "Undo end turn", SemanticKey = "undo_end_turn" });
        return s;
    }

    /// <summary>The map after a fight, with two moves (the model picks: no route is pinned).</summary>
    private static GameSnapshot Map(params PlayerInfo[] mates)
    {
        var s = new GameSnapshot { Screen = ScreenKind.Map, InRun = true, CanAct = true, IsMultiplayer = true, LocalPlayerId = 1, Map = TestData.Map(), Run = new RunInfo { Act = 1, Floor = 4 } };
        s.Players.Add(new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 50, MaxHp = 80 });
        s.Players.AddRange(mates);
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Label = "0,0", SemanticKey = "map:0,0", Args = { [ActionArgs.Node] = "0,0" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.ChooseMapNode, Label = "1,0", SemanticKey = "map:1,0", Args = { [ActionArgs.Node] = "1,0" } });
        return s;
    }

    private static TeamMessage Request(string id, TeamMessageKind kind, ulong to, string? potion = null) =>
        new() { Id = id, Kind = kind, To = to, Potion = potion, Text = "q " + id, Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    // ---------------------------------------------------------------- a "yes" (C0, C1)

    [Fact]
    public async Task A_yes_read_by_a_decision_that_was_not_carried_out_stays_a_promise()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client();
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        engine.Memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 2, "BLOCK_POTION"));
        hub.Receive(new TeamMessage { Id = "yes1", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.Accept, ReplyTo = "off", Text = "yes please" });
        await engine.DecideAsync(Coop(Bob()), null, CancellationToken.None);
        Assert.Contains("(yes to your offer — act on it): yes please", client.Prompt(0));

        // Advice, a state change before acting, a fallback: nothing marked the yes read, but its offer is closed.
        var preview = engine.PreviewPrompt(Coop(Bob()), null).User;
        await engine.DecideAsync(Coop(Bob()), null, CancellationToken.None);
        var prompt = client.Prompt(1);
        Assert.Equal(preview, prompt);
        Assert.Contains("accepted earlier (check HISTORY whether you already did it): [yes1] Bob | request_potion potion=BLOCK_POTION: yes please", prompt);
        Assert.Single(Regex.Matches(prompt, @"\[yes1\]"));
        Assert.DoesNotContain("(no reply needed): yes please", prompt);
    }

    [Fact]
    public async Task A_yes_waits_for_a_capture_that_can_act_on_it()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        engine.Memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 3, "BLOCK_POTION"), signalled: true);
        hub.Receive(TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, engine.Memory.LastSignalTo(3)));

        // Enemy turn: nothing to answer and nothing the AI can do, so no paid call, and nothing read or closed.
        Assert.False(engine.HasActionableInbox(Waiting(Cara())));
        Assert.Null(await engine.ReplyOnlyAsync(Waiting(Cara()), "enemy turn", CancellationToken.None));
        Assert.Empty(client.Requests);
        Assert.Single(engine.Memory.OutgoingRequests);
        Assert.Empty(engine.Memory.OpenRequests);
        // With only "undo end turn" left the AI can take its turn back and give the potion.
        Assert.True(engine.HasActionableInbox(UndoOnly(Cara())));

        client.Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[\"a0\"]}";
        await engine.DecideAsync(Coop(Cara()), null, CancellationToken.None);
        Assert.Contains("(yes to your offer — act on it)", client.Prompt(0));
    }

    [Fact]
    public async Task A_yes_stays_for_the_AIs_turn_when_another_message_wakes_it()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        engine.Memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 3, "BLOCK_POTION"), signalled: true);
        var thumb = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, engine.Memory.LastSignalTo(3));
        hub.Receive(thumb);
        hub.Receive(new TeamMessage { Id = "rq", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "cover me" });

        // Enemy turn: Bob's request gets its answer; Cara's yes is left for the AI's turn, unread, its offer open.
        var waiting = Waiting(Bob(), Cara());
        var preview = engine.PreviewPrompt(waiting, null).User;
        var reply = await engine.ReplyOnlyAsync(waiting, DecisionEngine.ReplyOnlyReason(waiting)!, CancellationToken.None);
        Assert.Equal(preview, client.Prompt(0));
        Assert.DoesNotContain(thumb.Id, client.Prompt(0));
        Assert.Equal(new[] { "rq" }, reply!.HandledInboxIds);
        engine.MarkHandled(reply.HandledInboxIds);
        Assert.Single(engine.Memory.OutgoingRequests);
        Assert.Empty(engine.Memory.OpenRequests);

        client.Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[\"a0\"]}";
        await engine.DecideAsync(Coop(Bob(), Cara()), null, CancellationToken.None);
        Assert.Contains($"[{thumb.Id}] Cara → you | accept reply_to=off (answers your offer_potion) (yes to your offer — act on it)", client.Prompt(1));
    }

    [Fact]
    public async Task A_reply_only_call_the_breaker_skips_reads_nothing()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, _) => throw new InvalidOperationException("endpoint down") };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        hub.Receive(new TeamMessage { Id = "rq", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "cover me" });
        for (int i = 0; i < 3; i++)
            Assert.Null(await engine.ReplyOnlyAsync(Waiting(Bob(), Cara()), "enemy turn", CancellationToken.None));
        int calls = client.Requests.Count;

        // The breaker is open now: Cara's yes to our offer arrives, and the skipped call must not swallow it.
        engine.Memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 3, "BLOCK_POTION"), signalled: true);
        hub.Receive(TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, engine.Memory.LastSignalTo(3)));
        Assert.Null(await engine.ReplyOnlyAsync(Waiting(Bob(), Cara()), "enemy turn", CancellationToken.None));
        Assert.Equal(calls, client.Requests.Count);
        Assert.Single(engine.Memory.OutgoingRequests);
        Assert.Empty(engine.Memory.OpenRequests);
        Assert.Contains("(yes to your offer — act on it)", engine.PreviewPrompt(Coop(Bob(), Cara()), null).User);
    }

    // ---------------------------------------------------------------- an offer that answers a potion request (C2)

    [Fact]
    public async Task An_offer_in_answer_to_a_potion_request_goes_out_as_the_yes()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        var client = new Client
        {
            Answer = (_, n) => n == 1
                ? "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"offer_potion\",\"potion\":\"BLOCK_POTION\",\"text\":\"want my Block Potion?\",\"reply_to\":\"rp\"}]}"
                : "{\"thought\":\"t\",\"actions\":[\"a0\"]}",
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10), TransientWait = TimeSpan.Zero };
        bridge.Set(Coop(Bob(ai: true)));
        hub.Receive(new TeamMessage
        {
            Id = "rp", From = 2, To = 1, FromName = "Bob", FromAi = true, Kind = TeamMessageKind.RequestPotion, Potion = "FIRE_POTION",
            Text = "potion please",
        });
        loop.Start();
        await Until(() => bridge.Executed.Count > 0);
        loop.Stop();

        TeamMessage sent;
        lock (transport.Sent) sent = Assert.Single(transport.Sent);
        Assert.Equal(TeamMessageKind.Accept, sent.Kind);
        Assert.Equal("rp", sent.ReplyTo);
        Assert.Equal("BLOCK_POTION", sent.Potion);
        // A promise the AI keeps, not an offer waiting for a yes Bob's AI is told it need not give: of the potion it
        // offered, not the one Bob asked for.
        var promise = Assert.Single(engine.Memory.OpenRequests);
        Assert.Equal(("rp", "BLOCK_POTION"), (promise.Id, promise.Potion));
        Assert.Empty(engine.Memory.OutgoingRequests);
        // Only an answer to a potion request is a yes: an offer in answer to anything else stays an offer.
        Assert.Equal(TeamMessageKind.OfferPotion,
            TeamDelivery.AnswerKind(new TeamMessage { Kind = TeamMessageKind.RequestBlock }, TeamMessageKind.OfferPotion));
        Assert.Equal(TeamMessageKind.OfferPotion, TeamDelivery.AnswerKind(null, TeamMessageKind.OfferPotion));
    }

    [Fact]
    public async Task An_AI_offer_in_answer_to_another_AI_waits_for_no_yes()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        var client = new Client
        {
            Answer = (_, n) => n == 1
                ? "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"offer_potion\",\"potion\":\"BLOCK_POTION\",\"text\":\"cannot block you, want my Block Potion?\",\"reply_to\":\"rb\"}]}"
                : "{\"thought\":\"t\",\"actions\":[\"a0\"]}",
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10), TransientWait = TimeSpan.Zero };
        bridge.Set(Coop(Bob(ai: true)));
        hub.Receive(new TeamMessage { Id = "rb", From = 2, To = 1, FromName = "Bob", FromAi = true, Kind = TeamMessageKind.RequestBlock, Text = "cover me" });
        loop.Start();
        await Until(() => bridge.Executed.Count > 0);
        loop.Stop();

        // Bob's AI takes an AI's offer with reply_to for a counter-request it need not answer: no yes will come.
        lock (transport.Sent) Assert.Equal(TeamMessageKind.OfferPotion, Assert.Single(transport.Sent).Kind);
        Assert.Empty(engine.Memory.OutgoingRequests);
        Assert.False(engine.Memory.HasOpenOutgoing(TeamMessageKind.OfferPotion, 2));

        // Bob playing by hand can answer it, and so can Bob's AI when the owner wrote it: then it waits for the yes.
        hub.Receive(new TeamMessage { Id = "rb2", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "cover me" });
        loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.OfferPotion, To = 2, ReplyTo = "rb2", Potion = "BLOCK_POTION", Text = "potion?" }, Coop(Bob()), fromAi: true);
        hub.Receive(new TeamMessage { Id = "rb3", From = 2, To = 1, FromName = "Bob", FromAi = true, Kind = TeamMessageKind.RequestBlock, Text = "cover me" });
        loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.OfferPotion, To = 2, ReplyTo = "rb3", Potion = "BLOCK_POTION", Text = "potion!" }, Coop(Bob(ai: true)), fromAi: false);
        Assert.Equal(2, engine.Memory.OutgoingRequests.Count);
    }

    // ---------------------------------------------------------------- one answer per question (C3)

    [Fact]
    public async Task An_answer_the_player_gave_while_the_AI_thought_is_the_only_one()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        PilotLoop? loop = null;
        var client = new Client
        {
            Answer = (_, n) =>
            {
                if (n > 1) return "{\"thought\":\"t\",\"actions\":[\"a0\"]}";
                // POST /chat answers the same request while the model thinks.
                loop!.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.Decline, To = 2, ReplyTo = "rq", Text = "no, sorry" }, Coop(Bob()), fromAi: true);
                return "{\"thought\":\"t\",\"actions\":[\"a2\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"accept\",\"text\":\"sure, blocking\",\"reply_to\":\"rq\"}]}";
            },
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10), TransientWait = TimeSpan.Zero };
        bridge.Set(Coop(Bob()));
        hub.Receive(new TeamMessage { Id = "rq", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "block me?" });
        loop.Start();
        await Until(() => bridge.Executed.Count > 0);
        loop.Stop();

        Assert.Equal("a2", bridge.Executed[0].Id);
        lock (transport.Sent)
            Assert.Equal(TeamMessageKind.Decline, Assert.Single(transport.Sent, m => m.ReplyTo == "rq").Kind);
        Assert.Contains("(reply not sent: that message was already answered for you) sure, blocking", engine.Memory.History);
        Assert.Empty(engine.Memory.OpenRequests);
    }

    [Fact]
    public async Task A_later_word_on_an_answer_the_player_gave_still_goes_out()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        var client = new Client
        {
            Answer = (_, n) => n == 1
                ? "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"decline\",\"text\":\"sorry, I drank it myself\",\"reply_to\":\"rp\"}]}"
                : "{\"thought\":\"t\",\"actions\":[\"a0\"]}",
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10), TransientWait = TimeSpan.Zero };
        hub.Receive(new TeamMessage { Id = "rp", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Potion = "BLOCK_POTION", Text = "potion please" });
        // The player said yes through POST /chat before the AI's decision: the AI takes it back.
        Assert.True(loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.Accept, To = 2, ReplyTo = "rp", Text = "sure" }, Coop(Bob()), fromAi: true).Reached);
        bridge.Set(Coop(Bob()));
        loop.Start();
        await Until(() => bridge.Executed.Count > 0);
        loop.Stop();

        Assert.Contains("accepted earlier", client.Prompt(0));
        lock (transport.Sent)
            Assert.Equal(new[] { TeamMessageKind.Accept, TeamMessageKind.Decline }, transport.Sent.Where(m => m.ReplyTo == "rp").Select(m => m.Kind));
    }

    // ---------------------------------------------------------------- a fight the AI did not play (C4)

    [Fact]
    public async Task Requests_made_in_a_fight_the_AI_did_not_play_end_with_it()
    {
        var settings = Settings();
        settings.Control.Mode = PilotMode.OutOfCombatOnly;
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = new Transport() };
        var client = new Client();
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(new Bridge(), engine, hub, () => settings, new NullLog());
        // The owner plays the fight and talks from the Team tab: a request of their own, and a yes to Bob's.
        var fight = Coop(Bob());
        hub.Receive(new TeamMessage { Id = "rd", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestDebuff, Debuff = "Vulnerable", Text = "vuln it" });
        Assert.True(loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.RequestBlock, To = 2, Text = "block for me" }, fight, fromAi: false).Reached);
        Assert.True(loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.Accept, To = 2, ReplyTo = "rd", Text = "on it" }, fight, fromAi: false).Reached);
        Assert.Single(engine.Memory.OutgoingRequests);
        Assert.Single(engine.Memory.OpenRequests);

        // The AI's first decision after that fight: both ended with it, in the preview as in the prompt.
        var map = Map(Bob());
        var preview = engine.PreviewPrompt(map, null).User;
        Assert.DoesNotContain("[YOUR OPEN REQUESTS]", preview);
        Assert.DoesNotContain("accepted earlier", preview);
        await engine.DecideAsync(map, null, CancellationToken.None);
        Assert.Equal(preview, client.Prompt(0));
        Assert.Empty(engine.Memory.OpenRequests);
        Assert.False(engine.Memory.HasOpenOutgoing(TeamMessageKind.RequestBlock, 2));
    }

    [Fact]
    public void A_potion_promised_on_the_map_still_waits_for_the_next_fight()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = new Transport() };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        var loop = new PilotLoop(new Bridge(), engine, hub, () => settings, new NullLog());
        hub.Receive(new TeamMessage { Id = "rp", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "a potion next fight?" });
        Assert.True(loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.Accept, To = 2, ReplyTo = "rp", Text = "sure" }, Map(Bob()), fromAi: false).Reached);
        Assert.Contains("accepted earlier", engine.PreviewPrompt(Map(Bob()), null).User);
        Assert.Contains("accepted earlier", engine.PreviewPrompt(Coop(Bob()), null).User);
    }

    // ---------------------------------------------------------------- another player's question (C5)

    [Fact]
    public void A_thumbs_up_after_another_players_question_answers_nothing_of_ours()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        var memory = engine.Memory;
        memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 0, "BLOCK_POTION"), signalled: true);
        // Every client sees every emote (TeamLink.HandleReaction): a linked player's heart asks nothing.
        memory.SeeEmote(2, "Heart", linked: true);
        Assert.Equal(("off", true), memory.SeeEmote(3, "ThumbUp", linked: false));
        // Bob's AI shows Cara a "?": her thumbs up after it is for Bob.
        memory.SeeEmote(2, "QuestionMark", linked: true);
        var thumb = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, memory.SeeEmote(3, "ThumbUp", linked: false));
        Assert.Null(thumb.ReplyTo);
        hub.Receive(thumb);
        var party = Coop(Bob(ai: true), Cara());
        Assert.False(engine.HasActionableInbox(party));
        Assert.DoesNotContain("act on it", engine.PreviewPrompt(party, null).User);
    }

    [Fact]
    public void An_emote_the_game_could_not_show_is_reported()
    {
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = new Transport() };
        var party = Coop(Bob(), Cara());
        var sent = TeamDelivery.Send(hub, party, new TeamMessage { Kind = TeamMessageKind.RequestBlock, Text = "cover me" }, fromAi: false, _ => false);
        Assert.True(sent.Entry is { Delivered: true });
        Assert.Null(sent.Emote);
        Assert.True(sent.EmoteFailed);
        Assert.False(sent.EmoteLimited);
        sent = TeamDelivery.Send(hub, party, new TeamMessage { Kind = TeamMessageKind.RequestBlock, Text = "cover me now" }, fromAi: false, _ => true);
        Assert.Equal(("Exclamation", false), (sent.Emote, sent.EmoteFailed));
    }

    // ---------------------------------------------------------------- the preview is the prompt (C6)

    [Fact]
    public async Task The_preview_carries_the_instruction_only_where_the_call_does()
    {
        const string instruction = "use the block potion on Bob";
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        hub.Receive(new TeamMessage { Id = "rq", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "cover me" });

        // Enemy turn: a reply-only call, which never carries the owner's instruction.
        var waiting = Waiting(Bob());
        var preview = engine.PreviewPrompt(waiting, instruction).User;
        Assert.DoesNotContain("[OWNER INSTRUCTION]", preview);
        Assert.NotNull(await engine.ReplyOnlyAsync(waiting, DecisionEngine.ReplyOnlyReason(waiting)!, CancellationToken.None));
        Assert.Equal(preview, client.Prompt(0));

        // Only "undo end turn" left: a decision, which does.
        var undo = UndoOnly(Bob());
        preview = engine.PreviewPrompt(undo, instruction).User;
        Assert.Contains("[OWNER INSTRUCTION]", preview);
        await engine.DecideAsync(undo.WithOwnMenu(), instruction, CancellationToken.None, DecisionEngine.ReplyOnlyReason(undo));
        Assert.Equal(preview, client.Prompt(1));
    }
}
