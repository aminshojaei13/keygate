namespace Keygate.Lab;

/// <summary>
/// Tiny argument parser. Boolean flags are declared once; every other --name
/// consumes the next token as its value (or --name=value). Positionals are
/// accepted so both "install a" and "install --name a" work.
/// </summary>
public sealed class CliOptions
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "force", "offline", "all", "mock", "rotate-key", "deactivate-old",
        "token", "json", "help", "keep",
    };

    public string Command { get; }
    public List<string> Positionals { get; } = new();
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    private CliOptions(string command) => Command = command;

    public string? Value(string name) =>
        Values.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => Flags.Contains(name);

    public string? FirstPositional => Positionals.Count > 0 ? Positionals[0] : null;

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliOptions("help");

        var options = new CliOptions(args[0].TrimStart('-').ToLowerInvariant());

        if (args[0] is "-h" or "--help" or "help")
        {
            options.Flags.Add("help");
            return options;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "-h" or "--help")
            {
                options.Flags.Add("help");
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Positionals.Add(arg);
                continue;
            }

            var nameValue = arg[2..];
            var equals = nameValue.IndexOf('=');
            if (equals > 0)
            {
                var name = nameValue[..equals];
                options.Values[name] = nameValue[(equals + 1)..];
                continue;
            }

            if (BooleanFlags.Contains(nameValue))
            {
                options.Flags.Add(nameValue);
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options.Values[nameValue] = args[++i];
            }
            else
            {
                // A value-less option is almost always a typo; fail loudly
                // rather than silently activating a default.
                throw new LabException("INVALID_USAGE", $"--{nameValue} needs a value.");
            }
        }

        return options;
    }
}
