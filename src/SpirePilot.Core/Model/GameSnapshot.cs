namespace SpirePilot.Core.Model;

/// <summary>
/// Which kind of decision the game is currently waiting for. The game layer maps the active
/// room / overlay / screen node onto one of these values.
/// </summary>
public enum ScreenKind
{
    Unknown,
    Loading,
    MainMenu,
    CharacterSelect,
    Map,
    Combat,
    /// <summary>In-combat prompt to pick cards from hand (discard / exhaust / upgrade / retain ...).</summary>
    HandSelect,
    CombatRewards,
    CardReward,
    Shop,
    Event,
    RestSite,
    Treasure,
    /// <summary>Full-screen card grid (upgrade / remove / transform / pick from deck or pile).</summary>
    CardGridSelect,
    /// <summary>Choose one of a few cards (discover-like), relics or other options.</summary>
    ChooseOne,
    GameOver,
    /// <summary>A modal popup / confirmation / tutorial that blocks the screen below it.</summary>
    Popup,
}

/// <summary>
/// A complete, game-independent picture of the current game state plus the list of legal actions.
/// Produced on the game thread by the mod; consumed by the agent, the UI and the optional HTTP API.
/// </summary>
public sealed class GameSnapshot
{
    /// <summary>Monotonic counter; increases every time a snapshot is built.</summary>
    public long Sequence { get; set; }
    public ScreenKind Screen { get; set; }
    /// <summary>Raw screen / node / room type name, for diagnostics.</summary>
    public string ScreenName { get; set; } = "";
    public bool InRun { get; set; }
    public bool IsMultiplayer { get; set; }
    public ulong LocalPlayerId { get; set; }
    /// <summary>True when the local player can act right now (it is their decision and the game is idle).</summary>
    public bool CanAct { get; set; }
    /// <summary>When <see cref="CanAct"/> is false: why (animations running, waiting for teammates, ...).</summary>
    public string? WaitReason { get; set; }

    public RunInfo? Run { get; set; }
    public List<PlayerInfo> Players { get; set; } = new();
    public CombatInfo? Combat { get; set; }
    public MapInfo? Map { get; set; }
    public RewardInfo? Rewards { get; set; }
    public ShopInfo? Shop { get; set; }
    public EventInfo? Event { get; set; }
    public RestSiteInfo? RestSite { get; set; }
    public SelectionInfo? Selection { get; set; }
    public CharacterSelectInfo? CharacterSelect { get; set; }
    /// <summary>Main menu: the saved run "Continue" would resume, when it can be read.</summary>
    public SavedRunInfo? SavedRun { get; set; }
    public GameOverInfo? GameOver { get; set; }

    /// <summary>Legal actions for the local player, already validated by the game layer.</summary>
    public List<ActionOption> Actions { get; set; } = new();
    /// <summary>Free-form extra context lines (popup text, warnings, unsupported-screen hints).</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>Keyword / term explanations (from the game's hover tips) for terms that appear in this snapshot.</summary>
    public Dictionary<string, string> Glossary { get; set; } = new();

    /// <summary>
    /// A copy whose menu and notes lists can be filtered without touching this snapshot (other readers — the UI, the
    /// HTTP API — may hold it). The actions themselves are shared.
    /// </summary>
    public GameSnapshot WithOwnMenu()
    {
        var copy = (GameSnapshot)MemberwiseClone();
        copy.Actions = new List<ActionOption>(Actions);
        copy.Notes = new List<string>(Notes);
        return copy;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public PlayerInfo? LocalPlayer => Players.FirstOrDefault(p => p.IsLocal);
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PlayerInfo> Teammates => Players.Where(p => !p.IsLocal);

    public ActionOption? FindAction(string id) =>
        Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class RunInfo
{
    public int Act { get; set; }
    public string? ActName { get; set; }
    /// <summary>Total floor number in the run (1-based).</summary>
    public int Floor { get; set; }
    public int AscensionLevel { get; set; }
    public string? Seed { get; set; }
    public string? BossName { get; set; }
    /// <summary>What the act boss does (its moves / powers in short), when the game exposes it.</summary>
    public string? BossDescription { get; set; }
    public bool IsDaily { get; set; }
    /// <summary>Elapsed in-run time in seconds, when known.</summary>
    public long? RunTimeSeconds { get; set; }
}

public sealed class PlayerInfo
{
    public ulong NetId { get; set; }
    public string Name { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public bool IsLocal { get; set; }
    /// <summary>True when this player announced (via team link) that an AI pilot controls it.</summary>
    public bool IsAiControlled { get; set; }
    /// <summary>True when this player's game has SpirePilot installed (team link available).</summary>
    public bool HasTeamLink { get; set; }
    public bool IsAlive { get; set; } = true;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public int Gold { get; set; }
    public int Energy { get; set; }
    public int MaxEnergy { get; set; }
    /// <summary>Secondary resource (e.g. the Regent's stars); null when the character has none.</summary>
    public int? Stars { get; set; }
    public int OrbSlots { get; set; }
    public List<OrbInfo> Orbs { get; set; } = new();
    public List<PowerInfo> Powers { get; set; } = new();
    public int PotionSlots { get; set; }
    public List<PotionInfo> Potions { get; set; } = new();
    public List<RelicInfo> Relics { get; set; } = new();
    /// <summary>The player's master deck. Filled for the local player, may be filled for teammates.</summary>
    public List<CardInfo> Deck { get; set; } = new();
    public int DeckCount { get; set; }
    /// <summary>Companion / summoned creatures that belong to this player (e.g. Osty).</summary>
    public List<CreatureInfo> Companions { get; set; } = new();
    /// <summary>In combat: whether this player already ended (or readied to end) their turn.</summary>
    public bool EndedTurn { get; set; }
    /// <summary>In combat: number of cards in hand (visible for teammates too).</summary>
    public int? HandCount { get; set; }
}

public sealed class CardInfo
{
    /// <summary>Stable key for this card instance within the current snapshot context (e.g. "h2" for hand slot 2).</summary>
    public string Key { get; set; } = "";
    /// <summary>Model id of the card (e.g. "STRIKE_IRONCLAD").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Upgraded { get; set; }
    /// <summary>Energy cost; -1 means X cost; null when not applicable (e.g. unplayable status/curse).</summary>
    public int? Cost { get; set; }
    /// <summary>Star cost (Regent cards), when non-zero.</summary>
    public int? StarCost { get; set; }
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    /// <summary>Target type name (None, Self, AnyEnemy, AllEnemies, RandomEnemy, AnyPlayer, AnyAlly, AllAllies, ...).</summary>
    public string Target { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Text of the upgraded card, for cards that can still be upgraded (Smith choices, upgrade prompts).</summary>
    public string? UpgradedDescription { get; set; }
    public List<string> Keywords { get; set; } = new();
    /// <summary>In combat: whether the card can be played right now.</summary>
    public bool? Playable { get; set; }
    /// <summary>CardModel.GainsBlock.</summary>
    public bool GainsBlock { get; set; }
    public string? UnplayableReason { get; set; }
}

public sealed class PowerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Amount { get; set; }
    public bool IsDebuff { get; set; }
    public string Description { get; set; } = "";
    /// <summary>For a power owned by one creature that threatens another (Sandpit): the threatened player's net id.</summary>
    public ulong? TargetNetId { get; set; }
}

public sealed class OrbInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class PotionInfo
{
    public int Slot { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Target { get; set; } = "";
    public string Rarity { get; set; } = "";
    /// <summary>Whether the potion can be used right now (combat-only potions outside combat are not).</summary>
    public bool UsableNow { get; set; }
}

public sealed class RelicInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int? Counter { get; set; }
}

public sealed class IntentInfo
{
    /// <summary>Intent type name (Attack, Defend, Buff, Debuff, Escape, Sleep, Stun, Unknown, ...).</summary>
    public string Type { get; set; } = "";
    /// <summary>Damage per hit after modifiers, for attack intents.</summary>
    public int? Damage { get; set; }
    public int? Hits { get; set; }
    public string? Description { get; set; }
    /// <summary>Which player this intent targets, when the game exposes it (co-op).</summary>
    public string? TargetName { get; set; }
}

public sealed class CreatureInfo
{
    /// <summary>Short key used by actions to reference this creature (e.g. "e0", "p1"). Local to this client.</summary>
    public string Key { get; set; } = "";
    /// <summary>The game's combat id: the same on every client, so team messages use it to name an enemy.</summary>
    public uint? CombatId { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; } = true;
    public List<PowerInfo> Powers { get; set; } = new();
    public List<IntentInfo> Intents { get; set; } = new();
}

public sealed class CombatInfo
{
    public int Round { get; set; }
    public bool IsPlayerTurn { get; set; }
    /// <summary>Normal / Elite / Boss, when known.</summary>
    public string? EncounterType { get; set; }
    public string? EncounterName { get; set; }
    public List<CardInfo> Hand { get; set; } = new();
    public int DrawPileCount { get; set; }
    public int DiscardPileCount { get; set; }
    public int ExhaustPileCount { get; set; }
    /// <summary>Draw pile contents sorted by name (the real order is hidden information).</summary>
    public List<CardInfo> DrawPile { get; set; } = new();
    public List<CardInfo> DiscardPile { get; set; } = new();
    public List<CardInfo> ExhaustPile { get; set; } = new();
    public List<CreatureInfo> Enemies { get; set; } = new();
}

public enum MapNodeType
{
    Unknown,
    Monster,
    Elite,
    RestSite,
    Shop,
    Treasure,
    Event,
    Boss,
    Ancient,
}

public sealed class MapNodeInfo
{
    /// <summary>"col,row" key.</summary>
    public string Key { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    public MapNodeType Type { get; set; }
    public string TypeName { get; set; } = "";
    public List<string> Children { get; set; } = new();
    public bool Visited { get; set; }
}

public sealed class MapInfo
{
    public List<MapNodeInfo> Nodes { get; set; } = new();
    /// <summary>Key of the node the party is on; null at the start of an act.</summary>
    public string? Current { get; set; }
    /// <summary>Keys of nodes the player may travel to next.</summary>
    public List<string> Reachable { get; set; } = new();
    /// <summary>Total rows in this act's map (boss row excluded).</summary>
    public int Rows { get; set; }
    /// <summary>Nodes teammates voted for / pinged, when known: player name -> node key.</summary>
    public Dictionary<string, string> TeammateVotes { get; set; } = new();

    public MapNodeInfo? Find(string key) => Nodes.FirstOrDefault(n => n.Key == key);
}

public sealed class RewardItemInfo
{
    public int Index { get; set; }
    /// <summary>Gold, Potion, Relic, Card, RemoveCard, SpecialCard, ...</summary>
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public int? Amount { get; set; }
}

public sealed class RewardInfo
{
    public List<RewardItemInfo> Items { get; set; } = new();
    /// <summary>When a card reward is open: the cards to choose from.</summary>
    public List<CardInfo> CardChoices { get; set; } = new();
    public bool CanSkipCards { get; set; }
}

public sealed class ShopItemInfo
{
    public int Index { get; set; }
    /// <summary>Card, Relic, Potion, CardRemoval.</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Price { get; set; }
    public bool OnSale { get; set; }
    public bool Affordable { get; set; }
    public CardInfo? Card { get; set; }
}

public sealed class ShopInfo
{
    public List<ShopItemInfo> Items { get; set; } = new();
    public int? RemovalPrice { get; set; }
    public bool RemovalAvailable { get; set; }
}

public sealed class EventOptionInfo
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public string? Description { get; set; }
    public bool Locked { get; set; }
    /// <summary>EventOption.IsProceed: the leave / continue option.</summary>
    public bool IsProceed { get; set; }
    /// <summary>EventOption.WillKillPlayer(local player): the game itself says this option is fatal.</summary>
    public bool WillKill { get; set; }
}

public sealed class EventInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<EventOptionInfo> Options { get; set; } = new();
}

public sealed class RestOptionInfo
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class RestSiteInfo
{
    public List<RestOptionInfo> Options { get; set; } = new();
}

public sealed class SelectionInfo
{
    /// <summary>What the selection is for (Upgrade, Remove, Transform, Discard, Exhaust, Choose, ...).</summary>
    public string Purpose { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; } = 1;
    public List<CardInfo> Candidates { get; set; } = new();
    /// <summary>Non-card options (relics, generic choices) for <see cref="ScreenKind.ChooseOne"/>.</summary>
    public List<string> OtherOptions { get; set; } = new();
    public List<string> SelectedKeys { get; set; } = new();
    public bool CanConfirm { get; set; }
    public bool CanCancel { get; set; }
}

public sealed class CharacterOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Locked { get; set; }
    public bool Selected { get; set; }
}

public sealed class CharacterSelectInfo
{
    public List<CharacterOption> Characters { get; set; } = new();
    public int Ascension { get; set; }
    public int MaxAscension { get; set; }
    /// <summary>In a multiplayer lobby: player name -> character id they picked.</summary>
    public Dictionary<string, string> LobbyPicks { get; set; } = new();
    public bool LocalReady { get; set; }
}

public sealed class SavedRunInfo
{
    /// <summary>Character model id of the local player in the saved run (e.g. "IRONCLAD").</summary>
    public string CharacterId { get; set; } = "";
    public string? CharacterName { get; set; }
    public int? Ascension { get; set; }
    public int? Floor { get; set; }
}

public sealed class GameOverInfo
{
    public bool Victory { get; set; }
    public int? Score { get; set; }
    public int? FloorReached { get; set; }
}
