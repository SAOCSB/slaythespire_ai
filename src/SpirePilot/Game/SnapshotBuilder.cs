using SpirePilot.Core.Model;
using SpirePilot.Game.Readers;
using SpirePilot.Infra;
using SpirePilot.Team;

namespace SpirePilot.Game;

/// <summary>Builds a <see cref="GameSnapshot"/> and its <see cref="ActionSink"/> in one pass on the main thread.</summary>
internal static class SnapshotBuilder
{
    public static (GameSnapshot snapshot, ActionSink sink) Build(long sequence)
    {
        var ctx = GameContext.Capture();
        var snap = new GameSnapshot
        {
            Sequence = sequence,
            InRun = ctx.InRun,
            IsMultiplayer = ctx.IsMultiplayer,
            LocalPlayerId = ctx.LocalNetId,
        };
        var sink = new ActionSink(sequence);
        Glossary.Begin(snap.Glossary);
        try
        {
            BuildInto(ctx, snap, sink);
        }
        finally
        {
            Glossary.End();
        }
        return (snap, sink);
    }

    private static void BuildInto(GameContext ctx, GameSnapshot snap, ActionSink sink)
    {
        Section(snap, "run", () => PlayerReader.FillRun(ctx, snap));
        Section(snap, "players", () => PlayerReader.FillPlayers(ctx, snap));
        if (ctx.Combat != null) Section(snap, "combat", () => CombatReader.FillState(ctx, snap));

        var probe = ScreenResolver.Resolve(ctx);
        snap.Screen = probe.Kind;
        snap.ScreenName = probe.Name;

        Section(snap, "screen " + probe.Kind, () =>
        {
            switch (probe.Kind)
            {
                case ScreenKind.Popup: PopupReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.MainMenu: MenuReader.ReadMainMenu(ctx, probe, snap, sink); break;
                case ScreenKind.CharacterSelect: MenuReader.ReadCharacterSelect(ctx, probe, snap, sink); break;
                case ScreenKind.GameOver: MenuReader.ReadGameOver(ctx, probe, snap, sink); break;
                case ScreenKind.Combat: CombatReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.HandSelect: HandSelectReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.CardReward: RewardsReader.ReadCardReward(ctx, probe, snap, sink); break;
                case ScreenKind.CombatRewards: RewardsReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.CardGridSelect:
                case ScreenKind.ChooseOne: SelectionReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.Map: MapReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.Shop: ShopReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.Event: EventReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.RestSite: RestSiteReader.Read(ctx, probe, snap, sink); break;
                case ScreenKind.Treasure: TreasureReader.Read(ctx, probe, snap, sink); break;
                default:
                    if (probe.Kind == ScreenKind.Unknown && probe.HumanOnlyReason == null)
                        snap.Notes.Add("Unsupported screen: " + probe.Name);
                    break;
            }
        });

        Section(snap, "team", () => TeamLink.Annotate(snap));

        var (canAct, reason) = SafeReadiness(ctx, probe, snap);
        snap.CanAct = canAct && sink.Count > 0;
        snap.WaitReason = canAct ? (sink.Count == 0 ? "no actions" : null) : reason;
        snap.Actions = sink.Options.ToList();
    }

    private static (bool, string?) SafeReadiness(GameContext ctx, ScreenProbe probe, GameSnapshot snap)
    {
        try { return Readiness.Check(ctx, probe, snap); }
        catch (Exception ex)
        {
            ModLog.Warn("readiness check failed: " + ex.Message);
            return (false, "readiness check failed");
        }
    }

    /// <summary>Runs one part of the build; a failure only loses that part (noted in the snapshot), never the whole snapshot.</summary>
    private static void Section(GameSnapshot snap, string name, Action read)
    {
        try { read(); }
        catch (Exception ex)
        {
            snap.Notes.Add($"({name} unreadable: {ex.GetType().Name})");
            ThrottledLog.Warn("section:" + name, $"snapshot section '{name}' failed: {ex}");
        }
    }
}

/// <summary>Logs a repeating warning at most once per 30 seconds per key.</summary>
internal static class ThrottledLog
{
    private static readonly Dictionary<string, DateTime> Last = new();

    public static void Warn(string key, string message)
    {
        lock (Last)
        {
            var now = DateTime.UtcNow;
            if (Last.TryGetValue(key, out var t) && now - t < TimeSpan.FromSeconds(30)) return;
            Last[key] = now;
        }
        ModLog.Warn(message);
    }
}
