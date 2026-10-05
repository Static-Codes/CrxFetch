using CrxFetch;

namespace CrxFetch.Cli;

/// <summary>
/// Writes a validated package to disk and reports it. Shared by the download commands so they
/// present results identically.
/// </summary>
internal static class PackageReport
{
    /// <summary>
    /// Saves the package, prints what it turned out to be, and optionally extracts it.
    /// </summary>
    /// <param name="result">The validated package.</param>
    /// <param name="output">Requested output path, or null for the default.</param>
    /// <param name="extractZip">Whether to extract the archive alongside the package.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The id the package belongs to.</returns>
    public static async Task<string> WriteAsync(
        CrxDownloadResult result,
        string? output,
        bool extractZip,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var id = result.Info.ExtensionId;
        var path = Path.GetFullPath(output ?? $"{id}.crx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, result.Package, cancellationToken);

        var info = result.Info;
        Output.Fields(
            ("id", id),
            ("format", $"CRX{(int)info.Format} ({info.ZipLength} bytes of zip payload)"),
            ("signature", info.SignatureDetail),
            ("bytes", result.Package.Length.ToString()),
            ("path", path));

        if (extractZip)
        {
            var dir = Path.Combine(Path.GetDirectoryName(path)!, id);
            CrxArchive.ExtractToDirectory(result.Package, info, dir);
            Output.Line($"extracted    {dir}");
        }

        return id;
    }

    /// <summary>
    /// Verifies a package already on disk and reports it.
    /// </summary>
    /// <param name="path">Path to a .crx file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The exit code: 0 when the signature verifies, otherwise 65.</returns>
    public static async Task<int> InspectAsync(string path, CancellationToken cancellationToken)
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

    /// <summary>
    /// Explains that the service declined to serve a package, including what it did with each
    /// request.
    /// </summary>
    /// <param name="subject">What was being fetched, for the message.</param>
    /// <param name="ex">The failure.</param>
    public static void ReportNoPackage(string subject, CrxNoPackageException ex)
    {
        Output.Error($"No package url for {subject}.");
        Output.Error($"  attempts: {Describe(ex.Attempts)}");

        if (ex.DeclinedWithNoContent)
        {
            Output.Error(string.Empty);
            Output.Error("204 No Content is the service declining to serve a package: the body is");
            Output.Error("empty and no Location header is sent, so it is not a malformed request.");
            Output.Error("Extensions outside Google's extension allowlist are refused from every");
            Output.Error("egress; those marked _esbAllowlist=\"false\" in the update XML never get a");
            Output.Error("codebase. Re-run with --proxy <host:port> to change egress, or hand");
            Output.Error("`fetch` a hosted package url directly to bypass the service.");
        }
    }

    /// <summary>Describes each request that was attempted.</summary>
    /// <param name="attempts">The attempts to describe.</param>
    /// <returns>A comma separated summary.</returns>
    public static string Describe(IReadOnlyList<GupAttempt> attempts) =>
        string.Join(", ", attempts.Select(a => $"{a.Description}={(int)a.Status}"));
}