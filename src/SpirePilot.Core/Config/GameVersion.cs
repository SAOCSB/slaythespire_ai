using System.Globalization;

namespace SpirePilot.Core.Config;

/// <summary>How a SpirePilot build fits the running game.</summary>
public enum GameCompatibility
{
    /// <summary>The game's major.minor is the one the build was compiled for.</summary>
    Ok,
    /// <summary>The game is newer than the newest build: nothing better exists yet, so the build runs with a warning.</summary>
    Untested,
    /// <summary>The build is for another branch: its game code would misread screens, so the AI stays off.</summary>
    Incompatible,
}

/// <summary>
/// Game branches are not API compatible: a build is made for one major.minor game version (0.107.x, 0.111.x ...).
/// </summary>
public static class GameVersion
{
    /// <summary>The game branches a SpirePilot build ships for, oldest first.</summary>
    public static readonly IReadOnlyList<string> ShippedBranches = new[] { "0.107", "0.111" };

    /// <summary>"v0.107.1", "0.111.0-beta" or "0.111" → (0, 111); null when it is not a version.</summary>
    public static (int Major, int Minor)? MajorMinor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var s = version.Trim();
        if (s.Length > 1 && s[0] is 'v' or 'V') s = s[1..];
        var parts = s.Split('.');
        if (parts.Length < 2) return null;
        var minor = new string(parts[1].TakeWhile(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ma) ||
            !int.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out var mi))
            return null;
        return (ma, mi);
    }

    /// <summary>Whether the game's major.minor is the one the build was compiled for; null when either is unreadable.</summary>
    public static bool? Matches(string? gameVersion, string compiledFor) =>
        MajorMinor(gameVersion) is { } game && MajorMinor(compiledFor) is { } built ? game == built : null;

    /// <summary>
    /// How a build for <paramref name="compiledFor"/> fits the game; null when either version is unreadable. An older
    /// game lacks what the build calls. A newer game is untested on the newest build, but incompatible with an older
    /// one: that branch got its successor because the game moved on.
    /// </summary>
    public static GameCompatibility? Compatibility(string? gameVersion, string compiledFor)
    {
        if (MajorMinor(gameVersion) is not { } game || MajorMinor(compiledFor) is not { } built) return null;
        int order = Compare(game, built);
        if (order == 0) return GameCompatibility.Ok;
        if (order < 0) return GameCompatibility.Incompatible;
        bool newerBuildExists = ShippedBranches.Any(b => MajorMinor(b) is { } shipped && Compare(shipped, built) > 0);
        return newerBuildExists ? GameCompatibility.Incompatible : GameCompatibility.Untested;
    }

    /// <summary>
    /// The shipped build to install for this game: the one made for its branch, else the newest one when it would run
    /// untested (<paramref name="untested"/>); null when no shipped build runs on it.
    /// </summary>
    public static string? BuildFor(string? gameVersion, out bool untested)
    {
        untested = false;
        if (ShippedBranches.FirstOrDefault(b => Matches(gameVersion, b) == true) is { } exact) return exact;
        var newest = ShippedBranches[^1];
        if (Compatibility(gameVersion, newest) != GameCompatibility.Untested) return null;
        untested = true;
        return newest;
    }

    /// <summary>"0.111" from "v0.111.0": the branch a build would have to be made for.</summary>
    public static string? Branch(string? version) => MajorMinor(version) is { } v ? $"{v.Major}.{v.Minor}" : null;

    /// <summary>
    /// What "run anyway" stores for a build on a game ("game 0.111, build 0.107"): one value per pairing, so it never
    /// carries over to another game branch or build. Null when either version is unreadable.
    /// </summary>
    public static string? OverrideKey(string? gameVersion, string compiledFor) =>
        Branch(gameVersion) is { } game && Branch(compiledFor) is { } built ? $"game {game}, build {built}" : null;

    private static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor);
}
