namespace CrxFetch;

/// <summary>Configuration for retrieving and downloading a CRX.</summary>
public sealed record CrxDownloadOptions
{
    /// <summary>Largest package accepted, guarding against a hostile or broken response.</summary>
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Proxy endpoint to route traffic through, for example <c>socks5://127.0.0.1:1080</c>.
    /// Null uses the system proxy configuration, if any.
    /// </summary>
    public string? Proxy { get; init; }

    /// <summary>Chrome version reported to the update service.</summary>
    public string ChromeVersion { get; init; } = ChromeUpdateService.DefaultChromeVersion;

    /// <summary>Overall per-request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>TCP connect timeout, so an unreachable proxy fails quickly.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When true (the default) a package whose developer-key signature does not verify is
    /// rejected instead of returned.
    /// </summary>
    public bool RequireSignature { get; init; } = true;
}