using System.Globalization;

namespace SpirePilot.Core.Config;

/// <summary>
/// Game branches are not API compatible: a build only runs on the major.minor game version it was compiled against
/// (0.107.x, 0.111.x ...).
/// </summary>
public static class GameVersion
{
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

    /// <summary>"0.111" from "v0.111.0": the build a user needs for this game version.</summary>
    public static string? Branch(string? version) => MajorMinor(version) is { } v ? $"{v.Major}.{v.Minor}" : null;
}
