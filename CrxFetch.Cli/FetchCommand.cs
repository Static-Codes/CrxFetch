using System.ComponentModel;
using CrxFetch;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CrxFetch.Cli;

/// <summary>Options shared by the download commands.</summary>
internal abstract class DownloadSettings : CommandSettings
{
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

/// <summary>Arguments for the download command.</summary>
internal sealed class FetchSettings : DownloadSettings
{
    [CommandArgument(0, "[extension-id|store-url|extension-link]")]
    [Description(
        "Extension id (a-p, 32 chars), a Chrome Web Store URL, or a URL that hosts the package "
        + "directly. A Web Store URL is resolved through the update service; any other URL is "
        + "fetched as-is, following redirects.")]
    public string? Target { get; init; }
}

/// <summary>
/// Downloads a CRX as a raw package, verified. The target may be an extension id, a Chrome Web
/// Store URL, or a url that hosts the package directly, and each is routed the right way.
/// </summary>
internal sealed class FetchCommand : AsyncCommand<FetchSettings>
{
    /// <summary>The command name.</summary>
    public const string Name = "fetch";

    /// <summary>
    /// Accepted as an alias, because it says what the command does when handed a package url.
    /// </summary>
    public const string LinkAlias = "extension-link";

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        FetchSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            Output.Error("Pass an extension id, a Chrome Web Store URL, or a hosted package URL.");
            return ExitCodes.Usage;
        }

        var options = new CrxDownloadOptions
        {
            Proxy = settings.Proxy,
            ChromeVersion = settings.ChromeVersion ?? ChromeUpdateService.DefaultChromeVersion,
            RequireSignature = !settings.NoVerify,
        };

        // A Chrome Web Store URL addresses an extension, so it goes through the update service.
        // Any other absolute http(s) URL is treated as hosting the package already, whether it
        // serves the bytes directly or redirects to something that does.
        var hostedLink = CrxLink.TryCreate(settings.Target, out var link) && link is not null;
        var extensionId = hostedLink || !ExtensionId.TryParse(settings.Target, out var parsed)
            ? null
            : parsed;

        if (!hostedLink && extensionId is null)
        {
            Output.Error(
                $"'{settings.Target}' is not an extension id, a Chrome Web Store URL, or a URL "
                + "that hosts a .crx package.");
            return ExitCodes.Usage;
        }

        using var downloader = new CrxDownloader(options);

        CrxDownloadResult result;
        try
        {
            if (link is not null)
            {
                // Pin the id when the link names one, so a link serving the wrong package fails.
                var expected = CrxLink.TryGetExtensionId(link, out var embedded) ? embedded : null;
                result = await downloader.DownloadFromLinkAsync(link, expected, cancellationToken);
            }
            else
            {
                result = await downloader.DownloadAsync(extensionId!, cancellationToken);
            }
        }
        catch (CrxNoPackageException ex)
        {
            PackageReport.ReportNoPackage(extensionId ?? settings.Target!, ex);
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

        if (settings.Verbose && result.Attempts.Count > 0)
        {
            foreach (var attempt in result.Attempts)
            {
                Output.Error(
                    $"  {attempt.Description,-24} {(int)attempt.Status,-4} " +
                    $"{attempt.Location?.ToString() ?? attempt.PackageUrlFromXml ?? "-"}");
            }

            Output.Error($"  package: {result.PackageUri}");
        }

        await PackageReport.WriteAsync(result, settings.Output, settings.ExtractZip, cancellationToken);

        return ExitCodes.Ok;
    }
}
