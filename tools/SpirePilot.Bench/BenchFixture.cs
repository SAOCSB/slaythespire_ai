using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench;

/// <summary>
/// One benchmark case: a game snapshot exactly as the mod's HTTP API (GET /state) serializes it, plus the context of
/// the decision (owner settings, live instruction, team inbox, memo, history) and what the answer must (not) be.
/// Envelope fields are snake_case; the snapshot itself uses the mod's camelCase JSON with string enums.
/// </summary>
public sealed class BenchFixture
{
    /// <summary>Unique name; defaults to the file name without extension.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>One line: why the expected answer is right. Shown in reports.</summary>
    [JsonPropertyName("rationale")] public string Rationale { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    /// <summary>"zh" or "en": the language the AI is told to write team messages in.</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "en";
    /// <summary>Partial PilotSettings merged over the base settings (route preset, pinned path, team options...). Profiles are never overridden.</summary>
    [JsonPropertyName("settings")] public JsonObject? Settings { get; set; }
    /// <summary>One-shot owner instruction, as typed into the overlay.</summary>
    [JsonPropertyName("live_instruction")] public string? LiveInstruction { get; set; }
    [JsonPropertyName("memo")] public string? Memo { get; set; }
    /// <summary>History lines as the pilot loop records them ("F6 R3: Play Strike → Nibbit (6 dmg)").</summary>
    [JsonPropertyName("history")] public List<string> History { get; set; } = new();
    /// <summary>Team messages delivered to the AI inbox before the decision.</summary>
    [JsonPropertyName("inbox")] public List<TeamMessage> Inbox { get; set; } = new();

    /// <summary>Where the decision must come from. Anything but Llm also requires that the model is never called.</summary>
    [JsonPropertyName("expect_source")] public DecisionSource ExpectSource { get; set; } = DecisionSource.Llm;
    /// <summary>Acceptable first actions: menu ids or semantic keys; a trailing '*' matches a key prefix. Empty = any.</summary>
    [JsonPropertyName("expect_any")] public List<string> ExpectAny { get; set; } = new();
    /// <summary>First actions that are mistakes (same syntax as <see cref="ExpectAny"/>).</summary>
    [JsonPropertyName("forbid")] public List<string> Forbid { get; set; } = new();
    /// <summary>At least one team message of one of these kinds ("accept", "decline", "offer_potion", ...).</summary>
    [JsonPropertyName("expect_message_kind")]
    [JsonConverter(typeof(StringOrListConverter))]
    public List<string> ExpectMessageKind { get; set; } = new();
    /// <summary>No team message may have one of these kinds.</summary>
    [JsonPropertyName("forbid_message_kind")]
    [JsonConverter(typeof(StringOrListConverter))]
    public List<string> ForbidMessageKind { get; set; } = new();
    /// <summary>Inbox message id the expected message must answer (reply_to).</summary>
    [JsonPropertyName("expect_reply_to")] public string? ExpectReplyTo { get; set; }
    [JsonPropertyName("max_messages")] public int? MaxMessages { get; set; }
    /// <summary>Maximum number of action ids in the answer (actions[0] plus the planned steps).</summary>
    [JsonPropertyName("max_plan_len")] public int? MaxPlanLength { get; set; }
    /// <summary>Maximum model calls; 1 = the first answer must be valid (no repair turn).</summary>
    [JsonPropertyName("max_llm_calls")] public int? MaxLlmCalls { get; set; }

    [JsonPropertyName("snapshot")] public JsonElement Snapshot { get; set; }

    /// <summary>File the fixture was loaded from.</summary>
    [JsonIgnore] public string Path { get; set; } = "";

    /// <summary>Decided by the engine's own rules: no LLM call needed (and none allowed).</summary>
    [JsonIgnore] public bool IsRule => ExpectSource != DecisionSource.Llm;

    /// <summary>A fresh snapshot instance (the engine may modify the one it decides on).</summary>
    public GameSnapshot CreateSnapshot() =>
        Snapshot.ValueKind == JsonValueKind.Object
            ? Snapshot.Deserialize<GameSnapshot>(FixtureLoader.JsonOptions) ?? throw new JsonException("snapshot is null")
            : throw new JsonException("\"snapshot\" must be an object");

    /// <summary>Case-insensitive match against id, file name and tags.</summary>
    public bool MatchesFilter(string filter) =>
        Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(Path).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "a3" matches the id; "play:BASH+0@E:NIBBIT#0" matches the semantic key; "potion:*" matches a key prefix.
    /// All comparisons ignore case.
    /// </summary>
    public static bool Matches(string pattern, ActionOption action)
    {
        pattern = pattern.Trim();
        if (pattern.EndsWith('*')) return action.SemanticKey.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(action.Id, pattern, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(action.SemanticKey, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a message kind as written in fixtures ("accept", "request_potion", "RequestPotion").</summary>
    public static bool TryParseKind(string raw, out TeamMessageKind kind) =>
        Enum.TryParse(raw.Replace("_", "").Replace("-", "").Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind);
}

/// <summary>Reads fixture files. Tolerant like the mod's settings reader: comments, trailing commas, any property case.</summary>
public static class FixtureLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new TeamMessageKindConverter(), new JsonStringEnumConverter() },
    };

    public static BenchFixture Load(string path)
    {
        var fixture = JsonSerializer.Deserialize<BenchFixture>(File.ReadAllText(path), JsonOptions)
                      ?? throw new JsonException("file is empty");
        fixture.Path = path;
        if (string.IsNullOrWhiteSpace(fixture.Id)) fixture.Id = System.IO.Path.GetFileNameWithoutExtension(path);
        return fixture;
    }

    /// <summary>Every *.json file in <paramref name="dir"/>, sorted by name.</summary>
    public static List<string> Files(string dir) =>
        Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();

    /// <summary>Loads every fixture; files that cannot be parsed are returned as errors instead of throwing.</summary>
    public static (List<BenchFixture> fixtures, List<(string path, string error)> errors) LoadAll(string dir)
    {
        var fixtures = new List<BenchFixture>();
        var errors = new List<(string, string)>();
        foreach (var file in Files(dir))
        {
            try { fixtures.Add(Load(file)); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or NotSupportedException)
            {
                errors.Add((file, ex.Message));
            }
        }
        return (fixtures, errors);
    }
}

/// <summary>Static checks that keep fixtures honest: a fixture whose expectation can never match is a silent false result.</summary>
public static class FixtureValidator
{
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase) { "zh", "en" };

    /// <summary>Problems with one fixture (empty when it is fine).</summary>
    public static List<string> Validate(BenchFixture f)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(f.Rationale)) problems.Add("rationale is empty");
        if (!Languages.Contains(f.Language)) problems.Add($"language must be zh or en, not \"{f.Language}\"");

        GameSnapshot s;
        try { s = f.CreateSnapshot(); }
        catch (JsonException ex)
        {
            problems.Add("snapshot: " + ex.Message);
            return problems;
        }

        if (s.Actions.Count == 0) problems.Add("snapshot has no actions");
        for (int i = 0; i < s.Actions.Count; i++)
        {
            var a = s.Actions[i];
            if (a.Id != "a" + i) problems.Add($"action #{i} has id \"{a.Id}\", expected \"a{i}\" (ids are a0..aN in order)");
            if (string.IsNullOrWhiteSpace(a.SemanticKey)) problems.Add($"{a.Id} has no semanticKey");
            if (string.IsNullOrWhiteSpace(a.Label)) problems.Add($"{a.Id} has no label");
            problems.AddRange(CheckConsistency(a, s));
        }
        if (s.Players.Count(p => p.IsLocal) != 1) problems.Add("snapshot needs exactly one player with isLocal: true");
        else if (s.LocalPlayer!.NetId != s.LocalPlayerId) problems.Add("localPlayerId does not match the local player's netId");

        foreach (var (field, patterns) in new[] { ("expect_any", f.ExpectAny), ("forbid", f.Forbid) })
            foreach (var p in patterns)
                if (!s.Actions.Any(a => BenchFixture.Matches(p, a))) problems.Add($"{field} \"{p}\" matches no action in the menu");
        foreach (var a in s.Actions)
            if (f.ExpectAny.Any(p => BenchFixture.Matches(p, a)) && f.Forbid.Any(p => BenchFixture.Matches(p, a)))
                problems.Add($"{a.Id} ({a.SemanticKey}) is both expected and forbidden");

        foreach (var k in f.ExpectMessageKind.Concat(f.ForbidMessageKind))
            if (!BenchFixture.TryParseKind(k, out _)) problems.Add($"unknown message kind \"{k}\"");
        bool checksMessages = f.ExpectMessageKind.Count > 0 || f.ForbidMessageKind.Count > 0 || f.ExpectReplyTo != null || f.MaxMessages is > 0;
        if (checksMessages && !s.IsMultiplayer) problems.Add("message expectations need isMultiplayer: true (the engine drops messages in single-player)");
        if (f.ExpectReplyTo != null && f.Inbox.All(m => m.Id != f.ExpectReplyTo)) problems.Add($"expect_reply_to \"{f.ExpectReplyTo}\" is not an inbox message id");

        foreach (var m in f.Inbox)
        {
            if (!s.IsMultiplayer) problems.Add($"inbox message {m.Id}: the engine ignores the inbox in single-player");
            if (m.From == s.LocalPlayerId) problems.Add($"inbox message {m.Id} comes from the local player (the hub drops self-echoes)");
            if (m.To != 0 && m.To != s.LocalPlayerId) problems.Add($"inbox message {m.Id} is addressed to someone else (to must be 0 or localPlayerId)");
            if (string.IsNullOrWhiteSpace(m.Text)) problems.Add($"inbox message {m.Id} has no text");
        }
        if (f.Inbox.Select(m => m.Id).Distinct().Count() != f.Inbox.Count) problems.Add("inbox message ids are not unique");

        bool checksSomething = f.ExpectAny.Count > 0 || f.Forbid.Count > 0 || checksMessages || f.MaxMessages != null ||
                               f.MaxPlanLength != null || f.MaxLlmCalls != null || f.IsRule;
        if (!checksSomething) problems.Add("fixture checks nothing (set expect_any, forbid or a message / plan / call limit)");
        if (f.IsRule && f.ExpectAny.Count == 0) problems.Add("rule fixtures need expect_any (the action the rule must pick)");

        if (f.Settings != null)
        {
            foreach (var (key, _) in f.Settings)
                if (key.Equals("profiles", StringComparison.OrdinalIgnoreCase) || key.Equals("activeProfile", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"settings may not override \"{key}\" (the benchmark always uses your profile)");
            try { BenchSettings.ForFixture(new PilotSettings(), f); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                problems.Add("settings: " + ex.Message);
            }
        }
        return problems;
    }

    /// <summary>Problems across fixtures (duplicate ids).</summary>
    public static List<string> ValidateSet(IEnumerable<BenchFixture> fixtures) =>
        fixtures.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"duplicate fixture id \"{g.Key}\" in {string.Join(", ", g.Select(f => System.IO.Path.GetFileName(f.Path)))}")
            .ToList();

    /// <summary>Catches authoring slips: an action whose args point at a card, potion, creature or node that does not exist.</summary>
    private static IEnumerable<string> CheckConsistency(ActionOption a, GameSnapshot s)
    {
        if (a.Arg(ActionArgs.Target) is { } target)
        {
            bool ok = target.Length > 1 && int.TryParse(target[1..], out var idx) && target[0] switch
            {
                'e' => s.Combat?.Enemies.Any(e => e.Key == target && e.IsAlive) == true,
                'p' => idx < s.Players.Count,
                'o' => s.Players.Any(p => p.Companions.Any(c => c.Key == target)),
                _ => false,
            };
            if (!ok) yield return $"{a.Id} targets \"{target}\", which is not a living enemy / player / companion";
        }
        switch (a.Kind)
        {
            case ActionKind.PlayCard:
            {
                var hand = s.Combat?.Hand ?? new List<CardInfo>();
                if (a.IntArg(ActionArgs.Index) is not { } i || i < 0 || i >= hand.Count)
                    yield return $"{a.Id} plays hand index {a.Arg(ActionArgs.Index) ?? "(none)"}, hand has {hand.Count} cards";
                else if (!a.SemanticKey.StartsWith("play:" + hand[i].Id, StringComparison.Ordinal))
                    yield return $"{a.Id} plays {hand[i].Id} but its key is \"{a.SemanticKey}\"";
                else if (hand[i].Playable == false)
                    yield return $"{a.Id} plays {hand[i].Id}, which is marked unplayable";
                break;
            }
            case ActionKind.UsePotion:
            {
                var potion = s.LocalPlayer?.Potions.FirstOrDefault(p => p.Slot == a.IntArg(ActionArgs.Slot));
                if (potion == null) yield return $"{a.Id} uses potion slot {a.Arg(ActionArgs.Slot) ?? "(none)"}, which is empty";
                else if (!a.SemanticKey.StartsWith("potion:" + potion.Id + "@", StringComparison.Ordinal))
                    yield return $"{a.Id} uses {potion.Id} but its key is \"{a.SemanticKey}\"";
                break;
            }
            case ActionKind.ChooseMapNode:
            {
                var node = a.Arg(ActionArgs.Node);
                if (node == null || s.Map == null || !s.Map.Reachable.Contains(node) || s.Map.Find(node) == null)
                    yield return $"{a.Id} travels to \"{node}\", which is not a reachable map node";
                else if (a.SemanticKey != "map:" + node)
                    yield return $"{a.Id} travels to {node} but its key is \"{a.SemanticKey}\"";
                break;
            }
            case ActionKind.PickRewardCard:
                if (s.Rewards?.CardChoices.Any(c => a.SemanticKey == "cardreward:" + c.Id) != true)
                    yield return $"{a.Id} key \"{a.SemanticKey}\" names no card in rewards.cardChoices";
                break;
            case ActionKind.ChooseRestOption:
                if (s.RestSite?.Options.Any(o => o.Enabled && a.SemanticKey == "rest:" + o.Id) != true)
                    yield return $"{a.Id} key \"{a.SemanticKey}\" names no enabled rest option";
                break;
            case ActionKind.ChooseEventOption:
                if (a.IntArg(ActionArgs.Index) is not { } ei || s.Event?.Options.Any(o => o.Index == ei && !o.Locked) != true)
                    yield return $"{a.Id} picks event option {a.Arg(ActionArgs.Index) ?? "(none)"}, which does not exist or is locked";
                break;
            case ActionKind.BuyItem:
                if (a.IntArg(ActionArgs.Index) is not { } si || s.Shop?.Items.FirstOrDefault(it => it.Index == si) is not { } item)
                    yield return $"{a.Id} buys shop item {a.Arg(ActionArgs.Index) ?? "(none)"}, which does not exist";
                else if (!item.Affordable)
                    yield return $"{a.Id} buys {item.Name}, which is not affordable (the mod never offers it)";
                break;
            case ActionKind.SelectCharacter:
                if (s.CharacterSelect?.Characters.Any(c => !c.Locked && c.Id == a.Arg(ActionArgs.Character)) != true)
                    yield return $"{a.Id} selects \"{a.Arg(ActionArgs.Character)}\", which is not an unlocked character";
                break;
        }
    }
}

/// <summary>Reads message kinds the way fixture authors write them: wire names ("request_potion") or enum names.</summary>
public sealed class TeamMessageKindConverter : JsonConverter<TeamMessageKind>
{
    public override TeamMessageKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(TeamMessageKind), n))
            return (TeamMessageKind)n;
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
        return BenchFixture.TryParseKind(raw, out var kind) ? kind : throw new JsonException($"unknown message kind \"{raw}\"");
    }

    public override void Write(Utf8JsonWriter writer, TeamMessageKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DecisionParser.KindToWire(value));
}

/// <summary>Accepts "accept" as well as ["accept", "decline"].</summary>
public sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return new List<string>();
            case JsonTokenType.String: return new List<string> { reader.GetString() ?? "" };
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    list.Add(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : throw new JsonException("expected a string"));
                return list;
            default: throw new JsonException("expected a string or an array of strings");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, typeof(List<string>));
}
