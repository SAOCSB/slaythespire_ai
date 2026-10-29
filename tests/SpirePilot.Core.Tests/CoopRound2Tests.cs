using System.Text.RegularExpressions;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>
/// Co-op regressions from the post-merge review: one delivery rule for every sender, emotes that answer only what the
/// teammate saw, promises that are really ours, owner rules for plan steps, and a prompt preview that is the prompt.
/// </summary>
public class CoopRound2Tests
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
        public List<TeamMessageKind> Signals { get; } = new();
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
        public bool TrySignal(TeamMessageKind kind) { lock (_lock) Signals.Add(kind); return true; }
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

    // ---------------------------------------------------------------- owner rules (F3)

    [Fact]
    public async Task A_teammate_potion_planned_before_giving_was_switched_off_is_never_taken()
    {
        var settings = Settings();
        var client = new Client { Answer = (_, n) => n == 1 ? "{\"thought\":\"t\",\"actions\":[\"a2\",\"a5\"]}" : "{\"thought\":\"t\",\"actions\":[\"a3\"]}" };
        var engine = new DecisionEngine(() => settings, new TeamChatHub { LocalNetId = 1 }, new NullLog(), _ => client);
        GameSnapshot Snap(int hand)
        {
            var s = Coop(Bob());
            while (s.Combat!.Hand.Count > hand) s.Combat.Hand.RemoveAt(0);
            s.Actions.Add(new ActionOption { Id = "a5", Kind = ActionKind.UsePotion, Label = "Use potion Block Potion → Bob", SemanticKey = "potion:BLOCK_POTION@P:2" });
            return s;
        }
        var first = await engine.DecideAsync(Snap(3), null, CancellationToken.None);
        Assert.Equal("a2", first!.FirstActionId);
        Assert.True(engine.Memory.HasPlan);

        settings.Team.AllowGivingPotions = false;
        var next = await engine.DecideAsync(Snap(2), null, CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, next!.Source);
        Assert.Equal("a3", next.FirstActionId);
        Assert.Contains(engine.Memory.History, h => h.StartsWith("(plan stopped"));
    }

    [Fact]
    public async Task Giving_switched_off_while_the_model_thinks_stops_the_potion()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var hub = new TeamChatHub { LocalNetId = 1, Transport = new Transport() };
        var client = new Client
        {
            Answer = (_, n) =>
            {
                if (n == 1) settings.Team.AllowGivingPotions = false; // the owner flips the switch mid-call
                return n == 1 ? "{\"thought\":\"t\",\"actions\":[\"a5\"]}" : "{\"thought\":\"t\",\"actions\":[\"a3\"]}";
            },
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        var s = Coop(Bob());
        s.Actions.Add(new ActionOption { Id = "a5", Kind = ActionKind.UsePotion, Label = "Use potion Block Potion → Bob", SemanticKey = "potion:BLOCK_POTION@P:2" });
        bridge.Set(s);
        loop.Start();
        await Until(() => bridge.Executed.Count > 0);
        loop.Stop();
        Assert.DoesNotContain(bridge.Executed, a => a.Kind == ActionKind.UsePotion);
        Assert.Equal(ActionKind.EndTurn, bridge.Executed[0].Kind);
    }

    // ---------------------------------------------------------------- the human's fight (F4)

    [Fact]
    public async Task Requests_from_a_fight_the_human_played_are_not_answered_after_it()
    {
        var settings = Settings();
        settings.Control.Mode = PilotMode.OutOfCombatOnly;
        var bridge = new Bridge();
        var hub = new TeamChatHub { LocalNetId = 1, Transport = new Transport() };
        var client = new Client();
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        bridge.Set(Coop(Bob()));
        loop.Start();
        await Until(() => loop.Status.State == PilotState.HumanTurn);
        hub.Receive(new TeamMessage { From = 2, To = 0, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "block me" });
        hub.Receive(new TeamMessage { From = 2, To = 0, FromName = "Bob", Kind = TeamMessageKind.SuggestRoute, Text = "left next", Target = "0,1" });
        await Task.Delay(100);

        var rewards = new GameSnapshot { Screen = ScreenKind.CombatRewards, InRun = true, CanAct = true, IsMultiplayer = true, LocalPlayerId = 1, Run = new RunInfo { Act = 1, Floor = 3 } };
        rewards.Players.Add(new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 50, MaxHp = 80 });
        rewards.Players.Add(Bob());
        rewards.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.Proceed, Label = "Proceed", SemanticKey = "proceed" });
        bridge.Set(rewards);
        await Until(() => bridge.Executed.Count > 0 || client.Requests.Count > 0);
        loop.Stop();

        var prompt = client.Requests.Count > 0 ? client.Prompt(0) : "";
        Assert.DoesNotContain("block me", prompt);
        Assert.DoesNotContain(hub.PeekAiInbox(), m => m.Kind == TeamMessageKind.RequestBlock);
    }

    // ---------------------------------------------------------------- emotes (F5, F7, F8, F14, F18)

    [Theory]
    [InlineData("Exclamation")]
    [InlineData("ThumbUp")]
    public void A_linked_teammates_emote_is_context_for_us(string reaction)
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        engine.Memory.TrackOutgoing(Request("ask", TeamMessageKind.AskNeedPotion, 0), signalled: true);
        var m = TeamEmotes.ToMessage(reaction, 2, "Bob", 1, engine.Memory.LastSignalTo(2), fromLinkedPeer: true);
        Assert.Equal(TeamMessageKind.Chat, m.Kind);
        Assert.Null(m.ReplyTo);
        Assert.Equal(0ul, m.To);
        Assert.False(DecisionEngine.IsActionable(m));
        hub.Receive(m);
        Assert.False(engine.HasActionableInbox(Waiting(Bob(), Cara())));
        Assert.Contains("| chat (no reply needed)", engine.PreviewPrompt(Coop(Bob(), Cara()), null).User);
    }

    [Fact]
    public async Task A_repeated_bang_is_answered_once_and_a_bare_one_keeps_the_turn_plan()
    {
        var settings = Settings();
        var now = DateTime.UtcNow;
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, n) => n == 1 ? "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\"]}" : "{\"thought\":\"t\",\"actions\":[]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client, () => now);

        await engine.DecideAsync(Coop(Cara()), null, CancellationToken.None);
        Assert.True(engine.Memory.HasPlan);
        var bang = TeamEmotes.ToMessage("Exclamation", 3, "Cara", 1, null);
        hub.Receive(bang);
        var afterDefend = Coop(Cara());
        afterDefend.Combat!.Hand.RemoveAt(1);
        var step = await engine.DecideAsync(afterDefend, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Plan, step!.Source);
        Assert.Single(hub.PeekAiInbox()); // still unread: the next model call sees it

        var waiting = Waiting(Cara());
        Assert.True(engine.HasActionableInbox(waiting));
        var reply = await engine.ReplyOnlyAsync(waiting, "enemy turn", CancellationToken.None);
        hub.MarkRead(reply!.HandledInboxIds);
        hub.Receive(TeamEmotes.ToMessage("Exclamation", 3, "Cara", 1, null, now: DateTimeOffset.UtcNow.AddSeconds(20)));
        Assert.False(engine.HasActionableInbox(waiting)); // tapped again soon after: context, no paid call
        now = now.AddSeconds(60);
        Assert.True(engine.HasActionableInbox(waiting));
    }

    [Fact]
    public void Thumbs_ups_to_two_questions_in_one_window_are_two_answers_and_a_double_tap_is_one()
    {
        var memory = new AgentMemory();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_001);
        memory.TrackOutgoing(Request("req1", TeamMessageKind.RequestBlock, 3), signalled: true);
        var first = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, memory.LastSignalTo(3), now: t0);
        Assert.True(hub.Receive(first));
        Assert.Equal("req1", first.ReplyTo);

        memory.CloseOutgoing("req1"); // the AI read the answer before the second tap of a double tap
        Assert.False(hub.Receive(TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, memory.LastSignalTo(3), now: t0.AddSeconds(1))));

        memory.TrackOutgoing(Request("req2", TeamMessageKind.OfferPotion, 3, "BLOCK_POTION"), signalled: true);
        var second = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, memory.LastSignalTo(3), now: t0.AddSeconds(5));
        Assert.True(hub.Receive(second));
        Assert.Equal("req2", second.ReplyTo);
    }

    [Fact]
    public void A_thumbs_up_never_answers_a_request_the_teammate_did_not_see()
    {
        var memory = new AgentMemory();
        memory.TrackOutgoing(Request("block", TeamMessageKind.RequestBlock, 0), signalled: true);
        memory.TrackOutgoing(Request("ask", TeamMessageKind.AskNeedPotion, 0), signalled: true);
        memory.CloseOutgoing("ask"); // Bob answered it in text
        memory.TrackOutgoing(Request("route", TeamMessageKind.SuggestRoute, 0)); // text only: Cara never saw it
        var up = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, memory.LastSignalTo(3));
        Assert.Null(up.ReplyTo);
        Assert.Equal(0ul, up.To);
        Assert.NotNull(memory.FindOutgoing("block"));
    }

    // ---------------------------------------------------------------- what a message commits us to (F6, F9, F12)

    [Fact]
    public async Task A_thumbs_up_to_an_offer_answering_a_bang_is_a_yes_with_its_potion()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client
        {
            Answer = (r, n) => n == 1
                ? "{\"thought\":\"t\",\"actions\":[],\"messages\":[{\"to\":\"Cara\",\"type\":\"offer_potion\",\"potion\":\"BLOCK_POTION\",\"text\":\"block potion?\",\"reply_to\":\"" +
                  Regex.Match(r.Messages[0].Content, @"\[(em[0-9a-f]{10})\] Cara").Groups[1].Value + "\"}]}"
                : "{\"thought\":\"t\",\"actions\":[]}",
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        var waiting = Waiting(Cara());
        bridge.Set(waiting);
        hub.Receive(TeamEmotes.ToMessage("Exclamation", 3, "Cara", 1, null));
        loop.Start();
        await Until(() => bridge.Signals.Count == 1);
        Assert.Equal(TeamMessageKind.OfferPotion, bridge.Signals[0]);

        var up = TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, engine.Memory.LastSignalTo(3));
        Assert.NotNull(up.ReplyTo);
        hub.Receive(up);
        await Until(() => engine.Memory.OpenRequests.Count == 1);
        loop.Stop();
        var promise = engine.Memory.OpenRequests[0];
        Assert.Equal(TeamMessageKind.RequestPotion, promise.Kind);
        Assert.Equal(3ul, promise.From);
        Assert.Equal("BLOCK_POTION", promise.Potion);
        Assert.Contains("block potion?", promise.Text);
        Assert.Contains(engine.Memory.History, h => h.StartsWith("(teammate cannot read messages; showed an emote)"));
    }

    [Fact]
    public async Task Saying_yes_to_a_teammates_potion_offer_is_not_a_promise_of_our_own()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, Transport = transport };
        var client = new Client { Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[],\"messages\":[{\"to\":\"Bob\",\"type\":\"accept\",\"text\":\"yes please\",\"reply_to\":\"off1\"}]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        bridge.Set(Waiting(Bob(ai: true)));
        hub.Receive(new TeamMessage { Id = "off1", From = 2, To = 0, FromName = "Bob", FromAi = true, Kind = TeamMessageKind.OfferPotion, Potion = "BLOCK_POTION", Text = "my Block Potion?" });
        loop.Start();
        await Until(() => transport.Sent.Count == 1);
        loop.Stop();
        Assert.Empty(engine.Memory.OpenRequests);
        Assert.Contains(engine.Memory.History, h => h.Contains("potion offer"));
    }

    // ---------------------------------------------------------------- several answers (F11)

    [Fact]
    public async Task Every_yes_to_a_potion_question_for_everyone_is_its_own_promise()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client();
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        engine.Memory.TrackOutgoing(Request("ask", TeamMessageKind.AskNeedPotion, 0), signalled: true);
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.Accept, ReplyTo = "ask", Text = "me!" });
        await engine.DecideAsync(Coop(Bob(), Cara()), null, CancellationToken.None);
        hub.MarkRead(hub.PeekAiInbox().Select(m => m.Id));
        Assert.NotNull(engine.Memory.FindOutgoing("ask")); // Cara has not answered yet

        hub.Receive(TeamEmotes.ToMessage("ThumbUp", 3, "Cara", 1, engine.Memory.LastSignalTo(3)));
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.Accept, ReplyTo = "ask", Text = "me! (again)" });
        await engine.DecideAsync(Coop(Bob(), Cara()), null, CancellationToken.None);
        Assert.Equal(new ulong[] { 2, 3 }, engine.Memory.OpenRequests.Select(r => r.From).OrderBy(x => x));
        Assert.Null(engine.Memory.FindOutgoing("ask")); // everyone answered
    }

    // ---------------------------------------------------------------- owner's switches on replies (F13)

    [Fact]
    public async Task No_potion_offer_or_promise_as_a_reply_when_giving_is_off()
    {
        var settings = Settings();
        settings.Team.AllowGivingPotions = false;
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client
        {
            Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[" +
                               "{\"to\":\"Bob\",\"type\":\"accept\",\"text\":\"sure\",\"reply_to\":\"rp1\"}," +
                               "{\"to\":\"Bob\",\"type\":\"offer_potion\",\"text\":\"take mine\",\"reply_to\":\"rp1\"}," +
                               "{\"to\":\"Bob\",\"type\":\"decline\",\"text\":\"can't, sorry\",\"reply_to\":\"rp1\"}]}",
        };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        hub.Receive(new TeamMessage { Id = "rp1", From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "potion?" });
        var d = await engine.DecideAsync(Coop(Bob()), null, CancellationToken.None);
        Assert.Equal(TeamMessageKind.Decline, Assert.Single(d!.Messages).Kind);
    }

    // ---------------------------------------------------------------- the preview is the prompt (F16, F23)

    [Fact]
    public async Task The_prompt_preview_is_the_prompt_the_engine_sends()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var client = new Client { Answer = (_, _) => "{\"thought\":\"t\",\"actions\":[]}" };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        engine.Memory.TrackOutgoing(Request("ask", TeamMessageKind.AskNeedPotion, 2));
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.Accept, ReplyTo = "ask", Text = "yes please" });
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestBlock, Text = "cover me" });
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestFocus, Text = "old news" });
        hub.PeekAiInbox()[^1].Time = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeMilliseconds();
        var undoOnly = Coop(Bob());
        undoOnly.Actions.Clear();
        undoOnly.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.UndoEndTurn, Label = "Undo end turn", SemanticKey = "undo_end_turn" });

        var preview = engine.PreviewPrompt(undoOnly, null).User;
        Assert.Equal(preview, engine.PreviewPrompt(undoOnly, null).User);
        Assert.Equal(3, hub.PeekAiInbox().Count);
        Assert.Contains("[NO ACTION NEEDED]", preview);
        Assert.Contains("(answers your ask_need_potion)", preview);
        Assert.DoesNotContain("[YOUR OPEN REQUESTS]", preview);
        Assert.DoesNotContain("old news", preview);

        await engine.DecideAsync(undoOnly.WithOwnMenu(), null, CancellationToken.None, DecisionEngine.ReplyOnlyReason(undoOnly));
        Assert.Equal(preview, client.Prompt(0));
    }

    // ---------------------------------------------------------------- one delivery rule (F10, F17, F33)

    [Fact]
    public void A_message_to_everyone_in_a_mixed_team_is_text_for_readers_and_an_emote_for_the_others()
    {
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, Transport = transport, MaxAiMessagesPerMinute = 4 };
        var signals = new List<TeamMessageKind>();
        bool Signal(TeamMessageKind k) { signals.Add(k); return true; }
        var mixed = Coop(Bob(), Cara());

        var chat = TeamDelivery.Send(hub, mixed, new TeamMessage { Kind = TeamMessageKind.Chat, Text = "focus the cultist" }, fromAi: true, Signal);
        Assert.True(chat.Entry!.Delivered);
        Assert.Null(chat.Emote);
        var ask = TeamDelivery.Send(hub, mixed, new TeamMessage { Kind = TeamMessageKind.RequestBlock, Text = "cover me" }, fromAi: true, Signal);
        Assert.True(ask.Entry!.Delivered);
        Assert.Equal("Exclamation", ask.Emote);
        Assert.Equal(new[] { TeamMessageKind.RequestBlock }, signals);
        // Each message counted once: two more fit in the budget of four.
        Assert.False(TeamDelivery.Send(hub, mixed, new TeamMessage { Kind = TeamMessageKind.Chat, Text = "one" }, fromAi: true, Signal).RateLimited);
        Assert.False(TeamDelivery.Send(hub, mixed, new TeamMessage { Kind = TeamMessageKind.Chat, Text = "two" }, fromAi: true, Signal).RateLimited);
        Assert.True(TeamDelivery.Send(hub, mixed, new TeamMessage { Kind = TeamMessageKind.Chat, Text = "three" }, fromAi: true, Signal).RateLimited);

        var onlyCara = TeamDelivery.Send(new TeamChatHub { LocalNetId = 1 }, mixed, new TeamMessage { Kind = TeamMessageKind.Accept, To = 3, Text = "ok" }, fromAi: false, Signal);
        Assert.True(onlyCara.NoReader);
        Assert.Null(onlyCara.Entry);
        Assert.Equal("ThumbUp", onlyCara.Emote);
        Assert.Equal(1ul, onlyCara.Message.From);
        Assert.Equal(4, transport.Sent.Count);
    }

    [Fact]
    public void An_emote_the_AI_just_showed_is_not_shown_again_and_says_so()
    {
        var hub = new TeamChatHub { LocalNetId = 1 };
        var cara = Coop(Cara());
        var first = TeamDelivery.Send(hub, cara, new TeamMessage { Kind = TeamMessageKind.RequestBlock, To = 3, Text = "cover me" }, fromAi: true, _ => true);
        Assert.Equal("Exclamation", first.Emote);
        var again = TeamDelivery.Send(hub, cara, new TeamMessage { Kind = TeamMessageKind.RequestBlock, To = 3, Text = "cover me!!" }, fromAi: true, _ => true);
        Assert.True(again.NoReader);
        Assert.True(again.EmoteLimited);
        Assert.False(again.Reached);
    }

    // ---------------------------------------------------------------- the owner's messages (F32, F0)

    [Fact]
    public async Task A_potion_offer_the_owner_sends_is_one_the_AI_keeps()
    {
        var settings = Settings();
        var bridge = new Bridge();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = new Transport() };
        var client = new Client();
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog());
        var snap = Coop(Bob(ai: true));

        var sent = loop.SendForPlayer(new TeamMessage { Kind = TeamMessageKind.OfferPotion, To = 2, Potion = "BLOCK_POTION", Text = "want my Block Potion?" }, snap, fromAi: false);
        Assert.True(sent.Reached);
        var offer = Assert.Single(engine.Memory.OutgoingRequests);
        Assert.Contains(engine.Memory.History, h => h.StartsWith("(owner → Bob) offer_potion"));

        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", FromAi = true, Kind = TeamMessageKind.Accept, ReplyTo = offer.Id, Text = "yes" });
        Assert.True(engine.HasActionableInbox(Waiting(Bob(ai: true))));
        await engine.DecideAsync(snap.WithOwnMenu(), null, CancellationToken.None);
        Assert.Contains("(yes to your offer — act on it)", client.Prompt(0));
        var promise = Assert.Single(engine.Memory.OpenRequests);
        Assert.Equal("BLOCK_POTION", promise.Potion);
    }

    [Fact]
    public void A_yes_outside_a_fight_says_the_potion_waits_for_the_next_one()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        engine.Memory.TrackOutgoing(Request("off", TeamMessageKind.OfferPotion, 2, "FRUIT_JUICE"));
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.Accept, ReplyTo = "off", Text = "yes" });
        var map = new GameSnapshot { Screen = ScreenKind.Map, InRun = true, CanAct = true, IsMultiplayer = true, LocalPlayerId = 1, Map = TestData.Map() };
        map.Players.Add(new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 50, MaxHp = 80 });
        map.Players.Add(Bob());
        Assert.Contains("potions reach teammates only in a fight", engine.PreviewPrompt(map, null).User);
    }
}
