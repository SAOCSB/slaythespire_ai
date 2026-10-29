using SpirePilot.Core.Config;

namespace SpirePilot.Core.Tests;

/// <summary>The game version guard: how each shipped build fits a game version, which build to suggest, the override.</summary>
public class RuntimeRound2Tests
{
    [Theory]
    [InlineData("v0.107.1", GameCompatibility.Ok, GameCompatibility.Incompatible)]
    [InlineData("v0.108.0", GameCompatibility.Incompatible, GameCompatibility.Incompatible)]
    [InlineData("v0.110.1", GameCompatibility.Incompatible, GameCompatibility.Incompatible)]
    [InlineData("v0.111.0", GameCompatibility.Incompatible, GameCompatibility.Ok)]
    [InlineData("v0.111.3", GameCompatibility.Incompatible, GameCompatibility.Ok)]
    [InlineData("v0.112.0", GameCompatibility.Incompatible, GameCompatibility.Untested)]
    [InlineData("v1.0.0", GameCompatibility.Incompatible, GameCompatibility.Untested)]
    public void Each_build_fits_the_game_version(string game, GameCompatibility build107, GameCompatibility build111)
    {
        Assert.Equal(build107, GameVersion.Compatibility(game, "0.107"));
        Assert.Equal(build111, GameVersion.Compatibility(game, "0.111"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dev")]
    public void An_unreadable_version_is_not_judged(string? game)
    {
        Assert.Null(GameVersion.Compatibility(game, "0.111"));
        Assert.Null(GameVersion.BuildFor(game, out _));
    }

    [Theory]
    [InlineData("v0.107.1", "0.107", false)]
    [InlineData("v0.108.0", null, false)]
    [InlineData("v0.110.1", null, false)]
    [InlineData("v0.111.0", "0.111", false)]
    [InlineData("v0.111.3", "0.111", false)]
    [InlineData("v0.112.0", "0.111", true)]
    [InlineData("v1.0.0", "0.111", true)]
    public void Only_shipped_builds_are_suggested(string game, string? expected, bool expectUntested)
    {
        var build = GameVersion.BuildFor(game, out bool untested);
        Assert.Equal(expected, build);
        Assert.Equal(expectUntested, untested);
        if (build != null) Assert.Contains(build, GameVersion.ShippedBranches);
    }

    [Fact]
    public void A_build_newer_than_every_shipped_one_runs_untested_on_newer_games()
    {
        // A local build for a branch that has no release yet is the newest there is.
        Assert.Equal(GameCompatibility.Untested, GameVersion.Compatibility("v0.113.0", "0.112"));
        Assert.Equal(GameCompatibility.Ok, GameVersion.Compatibility("v0.112.4", "0.112"));
        Assert.Equal(GameCompatibility.Incompatible, GameVersion.Compatibility("v0.111.0", "0.112"));
    }

    [Fact]
    public void Run_anyway_is_off_by_default_and_saved()
    {
        Assert.False(new PilotSettings().Control.IgnoreGameVersion);
        var path = Path.Combine(Path.GetTempPath(), $"spirepilot-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var s = new PilotSettings();
            s.Control.Mode = PilotMode.FullAuto;
            s.Control.IgnoreGameVersion = true;
            store.Save(s);
            Assert.Contains("\"ignoreGameVersion\": true", File.ReadAllText(path));
            var loaded = new SettingsStore(path).Load();
            Assert.True(loaded.Control.IgnoreGameVersion);
            Assert.Equal(PilotMode.FullAuto, loaded.Control.Mode);
        }
        finally { File.Delete(path); }
    }
}
