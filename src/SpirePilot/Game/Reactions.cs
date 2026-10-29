using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using SpirePilot.Core.Team;
using SpirePilot.Infra;

namespace SpirePilot.Game;

/// <summary>
/// Vanilla co-op reaction emotes (the reaction wheel's icons), the only "message" a teammate without SpirePilot can
/// see. Sent through the game's own <see cref="ReactionSynchronizer"/> (flavor traffic: not part of the game state, so
/// it cannot desync). Same API in 0.107 and 0.111: <c>NGame.Instance.ReactionContainer</c> holds the synchronizer in
/// a private field (<c>_synchronizer</c>); RunManager has none.
/// </summary>
internal static class Reactions
{
    private static bool _warned;

    /// <summary>The emote closest to a team message kind (<see cref="TeamEmotes.ReactionFor"/>), or null when none fits.</summary>
    public static ReactionType? For(TeamMessageKind kind) => TeamEmotes.ReactionFor(kind) switch
    {
        "ThumbUp" => ReactionType.ThumbUp,
        "ThumbDown" => ReactionType.ThumbDown,
        "Exclamation" => ReactionType.Exclamation,
        "QuestionMark" => ReactionType.QuestionMark,
        _ => null,
    };

    /// <summary>
    /// Main thread. Sends the emote for <paramref name="kind"/> from above the local player's creature (the viewport
    /// centre when it is not on screen). True only when it was handed to the synchronizer, in a co-op run.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Send(TeamMessageKind kind)
    {
        if (For(kind) is not { } type) return false;
        var ctx = GameContext.Capture();
        if (!ctx.InRun || !ctx.IsMultiplayer) return false;
        var container = NGame.Instance?.ReactionContainer;
        if (!SceneUtil.Valid(container) || !container!.InMultiplayer) return false;
        var sync = Reflect.Get<ReactionSynchronizer>(container, "_synchronizer");
        if (sync == null)
        {
            if (!_warned) ModLog.Warn("reactions: the game's reaction synchronizer was not found; emotes are disabled");
            _warned = true;
            return false;
        }
        sync.SendLocalReaction(type, Position(ctx, container));
        return true;
    }

    /// <summary>Screen (viewport) position for the emote: the local player's creature, else the viewport centre.</summary>
    private static Vector2 Position(GameContext ctx, Control container)
    {
        try
        {
            var node = ctx.Combat != null ? ctx.Me?.Creature?.GetCreatureNode() : null;
            if (SceneUtil.VisibleInTree(node))
            {
                var box = SceneUtil.VisibleInTree(node!.Hitbox) ? node.Hitbox : (Control)node;
                return box.GetGlobalTransformWithCanvas() * (box.Size / 2);
            }
        }
        catch (Exception) { /* fall back to the centre */ }
        try { return container.GetViewport().GetVisibleRect().Size / 2; }
        catch (Exception) { return Vector2.Zero; }
    }
}
