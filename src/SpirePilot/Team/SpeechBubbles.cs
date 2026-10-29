using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using SpirePilot.Core.Team;
using SpirePilot.Game;
using SpirePilot.Infra;

namespace SpirePilot.Team;

/// <summary>Shows chat text as the game's speech bubble above a player's character (local visual only).</summary>
internal static class SpeechBubbles
{
    private const int MaxChars = 140;
    private const double MinSeconds = 2.5;
    private const double MaxSeconds = 8.0;

    /// <summary>One bubble per player (net id); a new message replaces the previous one.</summary>
    private static readonly Dictionary<ulong, NSpeechBubbleVfx> Active = new();

    /// <summary>Main thread. Does nothing when the player's creature is not on screen.</summary>
    public static void Show(ulong playerNetId, string text)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.Post(() => Show(playerNetId, text));
            return;
        }
        try
        {
            ShowCore(playerNetId, text);
        }
        catch (Exception ex)
        {
            ThrottledLog.Warn("speech.bubble", "speech bubble failed: " + ex.Message);
        }
    }

    /// <summary>Shows incoming teammate messages above the sender (when enabled in the settings).</summary>
    internal static void Attach(TeamChatHub hub) => hub.EntryAdded += OnEntryAdded;

    private static void OnEntryAdded(ChatEntry entry)
    {
        try
        {
            // Received messages only: system notes are Incoming but not Delivered and have no sender.
            if (!entry.Incoming || !entry.Delivered) return;
            var m = entry.Message;
            // The game already shows a teammate's emote itself.
            if (m.From == 0 || m.Kind == TeamMessageKind.Hello || m.IsEmote || string.IsNullOrWhiteSpace(m.Text)) return;
            if (!PilotRuntime.Settings.Team.SpeechBubbles) return;
            ulong from = m.From;
            string text = m.Text;
            if (MainThread.IsMainThread) Show(from, text);
            else MainThread.Post(() => Show(from, text));
        }
        catch (Exception)
        {
            // visual only
        }
    }

    /// <summary>
    /// Main thread. Whether a teammate's message from <paramref name="playerNetId"/> shows as a bubble right now: the
    /// setting is on, a fight runs and the sender is on screen. Otherwise only a toast can show it.
    /// </summary>
    public static bool WillShow(ulong playerNetId)
    {
        try { return PilotRuntime.Settings.Team.SpeechBubbles && Anchor(playerNetId) != null; }
        catch (Exception) { return false; }
    }

    /// <summary>The player's creature and the combat room to put its bubble in, or null when it is not on screen.</summary>
    private static (Creature Creature, NCombatRoom Room)? Anchor(ulong playerNetId)
    {
        // Creature nodes only exist in combat.
        if (!FightRunning()) return null;
        var creature = CombatManager.Instance?.DebugOnlyGetState()?.GetPlayer(playerNetId)?.Creature;
        if (creature == null) return null;
        var room = NCombatRoom.Instance;
        if (!SceneUtil.Alive(room) || !SceneUtil.Alive(room!.GetCreatureNode(creature))) return null;
        // Vanilla adds creature dialogue VFX to the combat VFX container (as NCardPlay does for thought bubbles).
        return SceneUtil.Alive(room.CombatVfxContainer) ? (creature, room) : null;
    }

    private static void ShowCore(ulong playerNetId, string text)
    {
        var clean = Prepare(text);
        if (clean.Length == 0) return;
        if (Anchor(playerNetId) is not { } anchor) return;
        var (creature, room) = anchor;
        var container = room.CombatVfxContainer;

        Dismiss(playerNetId);
        var seconds = Math.Clamp(MinSeconds + clean.Length * 0.05, MinSeconds, MaxSeconds);
        var bubble = NSpeechBubbleVfx.Create(Escape(clean), creature, seconds, VfxColor.White);
        if (bubble == null) return;
        GodotTreeExtensions.AddChildSafely(container, bubble);
        Active[playerNetId] = bubble;
    }

    /// <summary>Main thread. Whether a fight is running, i.e. whether bubbles can show at all.</summary>
    public static bool FightRunning()
    {
        try { return CombatManager.Instance is { IsInProgress: true }; }
        catch (Exception) { return false; }
    }

    private static void Dismiss(ulong playerNetId)
    {
        if (!Active.Remove(playerNetId, out var old) || !SceneUtil.Valid(old) || old.IsQueuedForDeletion()) return;
        _ = FadeOut(old);
    }

    private static async Task FadeOut(NSpeechBubbleVfx bubble)
    {
        try { await bubble.AnimOut(); }
        catch (Exception) { /* freed meanwhile */ }
        // AnimOut presumably frees the bubble itself (unverified); make sure it goes away either way.
        try
        {
            if (SceneUtil.Valid(bubble) && !bubble.IsQueuedForDeletion()) bubble.QueueFree();
        }
        catch (Exception) { /* already gone */ }
    }

    /// <summary>One line, trimmed, length-capped.</summary>
    private static string Prepare(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (s.Length <= MaxChars) return s;
        int cut = MaxChars - 1;
        if (char.IsHighSurrogate(s[cut - 1])) cut--; // never split a surrogate pair (emoji)
        return s[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The bubble's label is a BBCode rich text label: teammate / LLM text must never inject tags. "[lb]" renders a
    /// literal "["; a lone "]" is plain text.
    /// </summary>
    private static string Escape(string s) => s.Replace("[", "[lb]");
}
