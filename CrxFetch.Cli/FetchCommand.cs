using System.ComponentModel;
using CrxFetch;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CrxFetch.Cli;

/// <summary>Arguments for the download command.</summary>
internal sealed class FetchSettings : CommandSettings
{
    [CommandArgument(0, "[extension-id|store-url]")]
    [Description("Extension id (a-p, 32 chars) or any Chrome Web Store URL containing one.")]
    public string? Target { get; init; }

    [CommandOption("--inspect <FILE>")]
    [Description("Verify a .crx already on disk and exit.")]
    public string? Inspect { get; init; }

    [CommandOption("-o|--out <FILE>")]
    [Description("Output path. Defaults to <id>.crx in the current directory.")]
    public string? Output { get; init; }

    [CommandOption("--proxy <ENDPOINT>")]
    [Description("Route traffic through http, https, socks4 or socks5.")]
    public string? Proxy { get; init; }

    [CommandOption("--chrome-version <VERSION>")]
    [Description("Version reported to the update service.")]
    public string? ChromeVersion { get; init; }

    [CommandOption("--zip")]
    [Description("Also extract the archive to <id>/.")]
    public bool ExtractZip { get; init; }

    [CommandOption("--no-verify")]
    [Description("Write the file even if the signature fails.")]
    public bool NoVerify { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Print every request the update service saw.")]
    public bool Verbose { get; init; }
}

/// <summary>
/// Downloads a Chrome Web Store extension as a raw .crx. Carries the behaviour the tool had
/// before it moved onto Spectre.Console.Cli, including its exit codes and messages.
/// </summary>
internal sealed class FetchCommand : AsyncCommand<FetchSettings>
{
    /// <summary>The command name legacy invocations are routed to.</summary>
    public const string Name = "fetch";

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        FetchSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Inspect is not null)
        {
            return await InspectAsync(settings.Inspect, cancellationToken);
        }

        if (!ExtensionId.TryParse(settings.Target, out var extensionId))
        {
            Output.Error($"Could not find a 32-character extension id in '{settings.Target}'.");
            Output.Error("Pass an id (a-p only) or a Chrome Web Store URL.");
            return ExitCodes.Usage;
        }

        var options = new CrxDownloadOptions
        {
            Proxy = settings.Proxy,
            ChromeVersion = settings.ChromeVersion ?? ChromeUpdateService.DefaultChromeVersion,
            RequireSignature = !settings.NoVerify,
        };

        using var downloader = new CrxDownloader(options);

        CrxDownloadResult result;
        try
        {
            result = await downloader.DownloadAsync(extensionId, cancellationToken);
        }
        catch (CrxNoPackageException ex)
        {
            ReportNoPackage(extensionId, ex);
            return ExitCodes.NoPackage;
        }
        catch (CrxValidationException ex)
        {
            Output.Error(ex.Message);
            return ExitCodes.BadPayload;
        }
        catch (CrxTransportException ex)
        {
            Output.Error(ex.Message);
            if (settings.Proxy is not null)
            {
                Output.Error($"  Traffic was routed through {settings.Proxy}.");
            }

            return ExitCodes.NoPackage;
        }

        if (settings.Verbose)
        {
            foreach (var attempt in result.Attempts)
            {
                Output.Error(
                    $"  {attempt.Description,-24} {(int)attempt.Status,-4} " +
                    $"{attempt.Location?.ToString() ?? attempt.PackageUrlFromXml ?? "-"}");
            }

            Output.Error($"  package: {result.PackageUri}");
        }

        var path = Path.GetFullPath(settings.Output ?? $"{extensionId}.crx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, result.Package, cancellationToken);

        var info = result.Info;
        Output.Fields(
            ("id", info.ExtensionId),
            ("format", $"CRX{(int)info.Format} ({info.ZipLength} bytes of zip payload)"),
            ("signature", info.SignatureDetail),
            ("bytes", result.Package.Length.ToString()),
            ("path", path));

        if (settings.ExtractZip)
        {
            var dir = Path.Combine(Path.GetDirectoryName(path)!, extensionId);
            CrxArchive.ExtractToDirectory(result.Package, info, dir);
            Output.Line($"extracted    {dir}");
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var crx = await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken);

        CrxInfo info;
        try
        {
            info = CrxFile.Inspect(crx);
        }
        catch (InvalidDataException ex)
        {
            Output.Error($"{path}: {ex.Message}");
            return ExitCodes.BadPayload;
        }

        Output.Fields(
            ("id", info.ExtensionId),
            ("format", $"CRX{(int)info.Format}"),
            ("signature", info.SignatureDetail),
            ("zip offset", info.ZipOffset.ToString()),
            ("zip length", info.ZipLength.ToString()));

        return info.SignatureVerified ? ExitCodes.Ok : ExitCodes.BadPayload;
    }

    private static void ReportNoPackage(string extensionId, CrxNoPackageException ex)
    {
        Output.Error($"No package url for {extensionId}.");
        Output.Error($"  attempts: {string.Join(", ", ex.Attempts.Select(a => $"{a.Description}={(int)a.Status}"))}");

        if (ex.DeclinedWithNoContent)
        {
            Output.Error(string.Empty);
            Output.Error("204 No Content is the service declining to serve a package: the body is");
            Output.Error("empty and no Location header is sent, so it is not a malformed request.");
            Output.Error("Two things move this needle. The reputation of the requesting network --");
            Output.Error("datacentre, VPN and cloud ranges are commonly refused. And how recently the");
            Output.Error("extension shipped: long-settled extensions keep being served while ones");
            Output.Error("that update often stop. Re-run with --proxy <host:port> to change egress.");
        }
    }
}

/// <summary>
/// Console output that keeps the original plain-text wording and the original stdout/stderr
/// split. Values are rendered as <see cref="Text"/> so nothing is ever read as markup, which
/// matters because ids, paths and signed urls can contain square brackets.
/// </summary>
internal static class Output
{
    /// <summary>
    /// Spectre wraps rendered content at the console width, which defaults to 80 columns when
    /// output is redirected. That would fold long signed urls across lines and corrupt the
    /// diagnostics, so the profile is widened past anything we emit.
    /// </summary>
    private const int RenderWidth = 4096;

    /// <summary>
    /// A Spectre console bound to standard error. Spectre only writes to stdout by default, and
    /// the original tool kept diagnostics on stderr, so the split is preserved explicitly.
    /// </summary>
    private static readonly IAnsiConsole ErrorConsole = Create(System.Console.Error);

    /// <summary>
    /// Widens the global console so nothing wraps. Must run before any rendering, including
    /// Spectre's own help output.
    /// </summary>
    public static void ConfigureForUnboundedLines() => AnsiConsole.Console.Profile.Width = RenderWidth;

    // Spectre has no WriteLine overload for IRenderable, so the newline is written separately
    // to keep one call out per line exactly as Console.WriteLine did.
    public static void Line(string text)
    {
        AnsiConsole.Write(new Text(text));
        AnsiConsole.WriteLine();
    }

    public static void Error(string text)
    {
        ErrorConsole.Write(new Text(text));
        ErrorConsole.WriteLine();
    }

    public static void Fields(params (string Field, string Value)[] fields)
    {
        var table = new Table()
            .NoBorder()
            .HideHeaders()
            .AddColumn(new TableColumn("field").NoWrap())
            .AddColumn("value");

        foreach (var (field, value) in fields)
        {
            table.AddRow(new Text(field), new Text(value));
        }

        AnsiConsole.Write(table);
    }

    private static IAnsiConsole Create(TextWriter writer) =>
        Configure(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
        }));

    private static IAnsiConsole Configure(IAnsiConsole console)
    {
        console.Profile.Width = RenderWidth;
        return console;
    }
}