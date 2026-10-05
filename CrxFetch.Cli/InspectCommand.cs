using System.ComponentModel;
using Spectre.Console.Cli;

namespace CrxFetch.Cli;

/// <summary>Arguments for the inspect command.</summary>
internal sealed class InspectSettings : CommandSettings
{
    [CommandArgument(0, "[<file.crx>]")]
    [Description("Path to a .crx file already on disk.")]
    public string? Path { get; init; }
}

/// <summary>
/// Verifies a .crx that is already on disk. Reads the container, checks its signatures and
/// reports what it finds, including where the ZIP archive starts.
/// </summary>
internal sealed class InspectCommand : AsyncCommand<InspectSettings>
{
    /// <summary>The command name.</summary>
    public const string Name = "inspect";

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        InspectSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            Output.Error("Pass the path to a .crx file to inspect.");
            return ExitCodes.Usage;
        }

        return await PackageReport.InspectAsync(settings.Path, cancellationToken);
    }
}