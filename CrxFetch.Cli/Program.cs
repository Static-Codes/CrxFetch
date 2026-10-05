using System.Reflection;
using CrxFetch.Cli;
using Spectre.Console.Cli;

return await Program.RunAsync(args);

internal static partial class Program
{
    /// <summary>
    /// Entry point. Returns the process exit code: 0 success, 64 usage, 65 bad payload,
    /// 69 no package or transport failure.
    /// </summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        Output.ConfigureForUnboundedLines();

        var app = BuildApp();

        // The original tool treated -h/--help (and no arguments at all) as "show this usage
        // text" rather than listing a command. Kept verbatim so the help output does not
        // change; Spectre's generated help is still reachable as `crxfetch fetch --help`.
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        var tokens = PrepareArguments(args, out var error);

        if (error is not null)
        {
            Output.Error(error);
            return ExitCodes.Usage;
        }

        try
        {
            return await app.RunAsync(tokens);
        }
        catch (CommandParseException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.Usage;
        }
    }

    /// <summary>
    /// Every option name Spectre will accept, mapped to whether it takes a value. Read straight
    /// off <see cref="FetchSettings"/> so this can never drift from the declared options.
    /// </summary>
    private static readonly Dictionary<string, bool> OptionTable = BuildOptionTable();

    private static Dictionary<string, bool> BuildOptionTable()
    {
        var table = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var property in typeof(FetchSettings).GetProperties())
        {
            if (property.GetCustomAttribute<CommandOptionAttribute>() is not { } attribute)
            {
                continue;
            }

            var takesValue = attribute.ValueName is not null && !attribute.ValueIsOptional;
            foreach (var name in attribute.ShortNames.Concat(attribute.LongNames))
            {
                table[Qualify(name)] = takesValue;
            }
        }

        return table;
    }

    // The attribute reports bare names, so short and long forms need qualifying here.
    private static string Qualify(string name) =>
        name.StartsWith('-') ? name : name.Length == 1 ? $"-{name}" : $"--{name}";

    /// <summary>Every command the tool exposes.</summary>
    private static readonly string[] KnownCommands =
        [FetchCommand.Name, FetchCommand.LinkAlias, InspectCommand.Name];

    /// <summary>
    /// Builds the argument vector for Spectre, preserving three things the original
    /// hand-rolled parser allowed and Spectre's stricter parser does not:
    /// an unrecognised option is rejected with the original message, an option missing its
    /// value is reported and then dropped rather than failing the whole run, and a second
    /// positional overrides the first instead of erroring. Short forms need no special handling
    /// because the options declare them.
    /// </summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="error">Set when an unrecognised option was found.</param>
    /// <returns>Tokens to hand to Spectre, led by the command name.</returns>
    private static List<string> PrepareArguments(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var optionTokens = new List<string>(args.Count);
        var positionals = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (!token.StartsWith('-'))
            {
                positionals.Add(token);
                continue;
            }

            // Spectre handles these itself.
            if (token is "-h" or "--help" or "--version")
            {
                optionTokens.Add(token);
                continue;
            }

            if (!OptionTable.TryGetValue(token, out var takesValue))
            {
                error = $"Unknown option: {token}";
                break;
            }

            if (!takesValue)
            {
                optionTokens.Add(token);
                continue;
            }

            if (i + 1 < args.Count)
            {
                optionTokens.Add(token);
                optionTokens.Add(args[++i]);
            }
            else
            {
                Output.Error($"{token} needs a value.");
            }
        }

        // A leading command name selects the command. Anything else is the target, and the
        // command defaults to `fetch`, which is how the tool behaved before it had commands.
        var named = positionals.Count > 0 && KnownCommands.Contains(positionals[0], StringComparer.Ordinal);
        var command = named ? positionals[0] : FetchCommand.Name;

        var target = named ? positionals.Skip(1).LastOrDefault() : positionals.LastOrDefault();

        var tokens = new List<string>(args.Count + 2) { command };
        tokens.AddRange(optionTokens);

        if (target is not null)
        {
            tokens.Add(target);
        }

        return tokens;
    }

    private static CommandApp BuildApp()
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.SetApplicationName("crxfetch");
            config.SetApplicationVersion(
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");

            // Let failures reach RunAsync so exit codes stay ours rather than Spectre's.
            config.PropagateExceptions();

            config.AddCommand<FetchCommand>(FetchCommand.Name)
                .WithAlias(FetchCommand.LinkAlias)
                .WithDescription(
                    "Download a .crx and verify it. Accepts an extension id, a Chrome Web Store "
                    + "URL, or a URL that hosts the package directly. A Web Store URL is resolved "
                    + "through Google's update service; any other URL is fetched as-is, following "
                    + "redirects. Either way the signature is checked and the extension id is "
                    + "re-derived from the embedded public key, so a substituted payload is rejected.")
                .WithExample("fetch", "cjpalhdlnbpafiamejdnhcphjbkeiagm")
                .WithExample("inspect", "ublock.crx")
                .WithExample(
                    "fetch",
                    "--proxy", "socks5://127.0.0.1:1080",
                    "--zip",
                    "https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")
                .WithExample(
                    "fetch",
                    "https://clients2.googleusercontent.com/crx/blobs/AZPVhcSooyIJq6Qj....crx");

            config.AddCommand<InspectCommand>(InspectCommand.Name)
                .WithDescription(
                    "Verify a .crx already on disk: parse the container, check its signatures and "
                    + "report the extension id, format and archive bounds. Exits 0 when the "
                    + "signature verifies.")
                .WithExample("inspect", "ublock.crx");
        });

        return app;
    }

    private static void PrintUsage() =>
        Output.Line(
            """
            crxfetch - download a Chrome Web Store extension as a raw .crx, no developer mode.

            usage:
              crxfetch fetch <extension-id|store-url|extension-link> [options]
              crxfetch inspect <file.crx>

            The fetch target is routed automatically. A Chrome Web Store URL is resolved through
            Google's update service; a bare id is resolved the same way; any other http(s) URL is
            treated as hosting the package already and fetched directly, following redirects.

            options:
              -o, --out <file>            output path (default <id>.crx in the current directory)
                  --proxy <host:port>     route traffic through http, https, socks4 or socks5
                  --chrome-version <ver>  version reported to the update service
                  --zip                    also extract the archive to <id>/
                  --no-verify              write the file even if the signature fails
              -v, --verbose                print every request the update service saw

            The download is verified: the archive signature is checked and the extension id is
            re-derived from the embedded public key, so a substituted payload is rejected.
            """);
}