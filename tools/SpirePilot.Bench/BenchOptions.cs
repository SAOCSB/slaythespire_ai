namespace SpirePilot.Bench;

/// <summary>Command-line options.</summary>
public sealed class BenchOptions
{
    public string? SettingsPath { get; set; }
    public string? Profile { get; set; }
    public bool KeepSettings { get; set; }
    public string? FixturesDir { get; set; }
    /// <summary>Run only fixtures whose id, file name or tag contains one of these.</summary>
    public List<string> Filters { get; } = new();
    public int Repeat { get; set; } = 1;
    public string? OutPath { get; set; }
    public bool DryRun { get; set; }
    public bool Validate { get; set; }
    public bool Help { get; set; }

    public const string Usage = """
    SpirePilot offline decision benchmark — replays fixture snapshots through the real decision engine and scores the model.

    Usage: dotnet run --project tools/SpirePilot.Bench -- [options]

      --settings <file>   SpirePilot settings.json, or a single profile JSON {"provider","baseUrl","model","apiKey"}
                          (apiKey may be "env:NAME"). Required unless --dry-run or --validate.
      --profile <name>    profile to use from settings.json (default: the active one)
      --keep-settings     also use the file's route / strategy / team settings (default: mod defaults, so runs
                          are comparable across machines; fixtures still apply their own overrides)
      --fixtures <dir>    fixture directory (default: tools/SpirePilot.Bench/fixtures)
      --filter <text>     only fixtures whose id, file name or tag contains <text>; repeat or comma-separate for several
      --repeat <n>        attempts per fixture (default 1); models are not deterministic
      --out <file.md>     also write a markdown report
      --dry-run           print the prompts without calling any API (no key needed); rule fixtures are still scored
      --validate          only check the fixture files
      -h, --help          this text

    Exit code: 0 all passed, 1 some fixture failed / invalid, 2 usage or configuration error.
    """;

    /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> with a readable message.</summary>
    public static BenchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new BenchOptions();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--") && eq > 2)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }
            string Value()
            {
                if (inline != null) return inline;
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--")) throw new ArgumentException($"{arg} needs a value");
                return args[++i];
            }

            switch (arg)
            {
                case "--settings": o.SettingsPath = Value(); break;
                case "--profile": o.Profile = Value(); break;
                case "--keep-settings": o.KeepSettings = true; break;
                case "--fixtures": o.FixturesDir = Value(); break;
                case "--filter":
                    o.Filters.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--repeat":
                    var raw = Value();
                    o.Repeat = int.TryParse(raw, out var n) && n is >= 1 and <= 100 ? n : throw new ArgumentException($"--repeat must be 1..100, not \"{raw}\"");
                    break;
                case "--out": o.OutPath = Value(); break;
                case "--dry-run": o.DryRun = true; break;
                case "--validate": o.Validate = true; break;
                case "-h" or "--help" or "/?": o.Help = true; break;
                default: throw new ArgumentException($"unknown option \"{arg}\"");
            }
        }
        if (!o.Help && !o.DryRun && !o.Validate && string.IsNullOrWhiteSpace(o.SettingsPath))
            throw new ArgumentException("--settings is required (or use --dry-run / --validate)");
        return o;
    }

    /// <summary>The fixture directory: the explicit one, else the repo's fixtures, else the copy next to the binary.</summary>
    public string ResolveFixturesDir()
    {
        if (!string.IsNullOrWhiteSpace(FixturesDir))
            return Directory.Exists(FixturesDir) ? Path.GetFullPath(FixturesDir) : throw new ArgumentException($"fixture directory \"{FixturesDir}\" does not exist");
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.CurrentDirectory, "tools", "SpirePilot.Bench", "fixtures"),
                     Path.Combine(Environment.CurrentDirectory, "fixtures"),
                     Path.Combine(AppContext.BaseDirectory, "fixtures"),
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new ArgumentException("no fixture directory found; pass --fixtures <dir>");
    }
}
