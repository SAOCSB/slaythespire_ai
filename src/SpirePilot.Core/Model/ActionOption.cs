namespace SpirePilot.Core.Model;

/// <summary>Every kind of action the pilot can perform. The game layer decides which are legal.</summary>
public enum ActionKind
{
    // Combat
    PlayCard,
    EndTurn,
    UsePotion,
    DiscardPotion,

    // Selections (hand select, card grid, choose-one)
    SelectCard,
    DeselectCard,
    ConfirmSelection,
    CancelSelection,
    ChooseOption,

    // Map
    ChooseMapNode,

    // Rewards
    ClaimReward,
    PickRewardCard,
    SkipCardReward,

    // Shop
    BuyItem,
    BuyCardRemoval,

    // Event / rest site / treasure
    ChooseEventOption,
    ChooseRestOption,
    OpenChest,

    // Flow
    Proceed,
    DismissPopup,

    // Main menu / character select / game over
    ContinueRun,
    StartNewRun,
    SelectCharacter,
    SetAscension,
    Embark,
    ReturnToMenu,
}

/// <summary>
/// One legal action in a snapshot. <see cref="Args"/> carries the parameters the game-side executor needs
/// (indices, target keys). <see cref="SemanticKey"/> identifies "the same" action across snapshots so a
/// multi-step plan can be re-validated after the state changes (hand indices shift after each play).
/// </summary>
public sealed class ActionOption
{
    /// <summary>Short id unique within the snapshot ("a0", "a1", ...). What the LLM answers with.</summary>
    public string Id { get; set; } = "";
    public ActionKind Kind { get; set; }
    /// <summary>Human-readable, localized description shown to the LLM and in the UI.</summary>
    public string Label { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
    public string SemanticKey { get; set; } = "";

    public string? Arg(string name) => Args.TryGetValue(name, out var v) ? v : null;

    public int? IntArg(string name) =>
        Args.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : null;

    public override string ToString() => $"{Id} {Kind} {Label}";
}

/// <summary>Well-known argument names used in <see cref="ActionOption.Args"/>.</summary>
public static class ActionArgs
{
    /// <summary>Index into the hand / candidate list / option list.</summary>
    public const string Index = "index";
    /// <summary>Creature key of the target ("e0" enemy, "p1" player, ...).</summary>
    public const string Target = "target";
    /// <summary>Potion slot index.</summary>
    public const string Slot = "slot";
    /// <summary>Map node key "col,row".</summary>
    public const string Node = "node";
    /// <summary>Character id for character select.</summary>
    public const string Character = "character";
    /// <summary>Ascension level for character select.</summary>
    public const string Ascension = "ascension";
    /// <summary>Card instance key ("h3", "g12", ...).</summary>
    public const string Card = "card";
}

/// <summary>Result of executing one action.</summary>
public sealed class ActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    public static ActionResult Ok(string message = "") => new() { Success = true, Message = message };
    public static ActionResult Fail(string message) => new() { Success = false, Message = message };
}
