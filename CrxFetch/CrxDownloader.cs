using System.IO.Compression;
using System.Net;

namespace CrxFetch;

/// <summary>A validated CRX package together with how it was obtained.</summary>
/// <param name="Package">The raw CRX bytes.</param>
/// <param name="Info">What parsing the package revealed.</param>
/// <param name="PackageUri">The signed url the package came from.</param>
/// <param name="Attempts">The update-service requests that were tried.</param>
public sealed record CrxDownloadResult(
    byte[] Package,
    CrxInfo Info,
    Uri PackageUri,
    IReadOnlyList<GupAttempt> Attempts);

/// <summary>
/// Retrieves, downloads and validates Chrome Web Store extensions as raw CRX packages.
/// </summary>
public sealed class CrxDownloader : IDisposable
{
    private readonly CrxDownloadOptions _options;
    private readonly HttpClient _updateClient;
    private readonly HttpClient _downloadClient;
    private readonly ChromeUpdateService _updateService;
    private bool _disposed;

    /// <summary>Creates a downloader.</summary>
    /// <param name="options">Proxy and timeout configuration; defaults are used when null.</param>
    public CrxDownloader(CrxDownloadOptions? options = null)
    {
        _options = options ?? new CrxDownloadOptions();
        _updateClient = CrxHttp.CreateUpdateClient(_options);
        _downloadClient = CrxHttp.CreateDownloadClient(_options);
        _updateService = new ChromeUpdateService(_updateClient);
    }

    /// <summary>The options this downloader was created with.</summary>
    public CrxDownloadOptions Options => _options;

    /// <summary>
    /// Resolves a package url for an extension without downloading it. Useful for inspecting
    /// what the update service offers, and for confirming egress works before a full fetch.
    /// </summary>
    /// <param name="extensionId">A 32-character extension id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The resolution result, which records every attempt even on failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="extensionId"/> is not an extension id.</exception>
    /// <exception cref="CrxTransportException">The update service could not be reached.</exception>
    public async Task<GupResult> RetrieveAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return await _updateService
                .ResolvePackageAsync(extensionId, _options.ChromeVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CrxTransportException(
                $"Could not reach {ChromeUpdateService.CrxEndpoint}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Downloads and validates an extension as a raw CRX package.
    /// </summary>
    /// <param name="extensionId">A 32-character extension id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The validated package.</returns>
    /// <exception cref="ArgumentException"><paramref name="extensionId"/> is not an extension id.</exception>
    /// <exception cref="CrxTransportException">The service or CDN could not be reached.</exception>
    /// <exception cref="CrxNoPackageException">No package url was offered for the extension.</exception>
    /// <exception cref="CrxValidationException">
    /// The payload was not a CRX, was signed for a different id, or failed signature validation.
    /// </exception>
    public async Task<CrxDownloadResult> DownloadAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        var resolution = await RetrieveAsync(extensionId, cancellationToken).ConfigureAwait(false);

        if (resolution.PackageUri is null)
        {
            throw new CrxNoPackageException(extensionId, resolution.Attempts);
        }

        byte[] package;
        try
        {
            package = await FetchPackageAsync(resolution.PackageUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CrxTransportException($"Package download failed: {ex.Message}", ex);
        }

        var info = Validate(package, extensionId);

        return new CrxDownloadResult(package, info, resolution.PackageUri, resolution.Attempts);
    }

    /// <summary>
    /// One-shot convenience wrapper: parse an id or store url, download and validate.
    /// </summary>
    /// <param name="idOrUrl">A 32-character id or any Chrome Web Store URL containing one.</param>
    /// <param name="options">Proxy and timeout configuration; defaults are used when null.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The validated package.</returns>
    /// <exception cref="ArgumentException">No extension id could be found in the input.</exception>
    /// <exception cref="CrxTransportException">The service or CDN could not be reached.</exception>
    /// <exception cref="CrxNoPackageException">No package url was offered for the extension.</exception>
    /// <exception cref="CrxValidationException">The payload failed validation.</exception>
    public static async Task<CrxDownloadResult> FetchAsync(
        string idOrUrl,
        CrxDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!ExtensionId.TryParse(idOrUrl, out var extensionId))
        {
            throw new ArgumentException(
                $"Could not find a 32-character extension id in '{idOrUrl}'.", nameof(idOrUrl));
        }

        using var downloader = new CrxDownloader(options);
        return await downloader.DownloadAsync(extensionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a package straight from a hosted link, bypassing the update service.
    /// </summary>
    /// <param name="link">
    /// A url that serves the package, either directly or through one or more redirects.
    /// </param>
    /// <param name="expectedExtensionId">
    /// The id the package must belong to. Supply it when the link embeds an id that should be
    /// enforced; pass null to accept whatever the link serves and report the id derived from
    /// the package itself.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The validated package, whose <see cref="CrxDownloadResult.PackageUri"/> is the url the
    /// bytes finally came from after following any redirects.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="link"/> is not an absolute http or https url.</exception>
    /// <exception cref="CrxTransportException">The link was unreachable, redirected too often, or answered with an error.</exception>
    /// <exception cref="CrxValidationException">The payload was not a CRX, belonged to another id, or failed validation.</exception>
    public async Task<CrxDownloadResult> DownloadFromLinkAsync(
        Uri link,
        string? expectedExtensionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CrxLink.TryCreate(link.AbsoluteUri, out _))
        {
            throw new ArgumentException(
                $"'{link}' is not an absolute http or https url.", nameof(link));
        }

        var (package, finalUri) = await FetchHostedPackageAsync(link, cancellationToken).ConfigureAwait(false);

        if (package.Length == 0)
        {
            throw new CrxValidationException($"{finalUri} served an empty body.");
        }

        var info = Validate(
            package,
            expectedExtensionId ?? string.Empty,
            _options.RequireSignature);

        return new CrxDownloadResult(package, info, finalUri, []);
    }

    /// <summary>
    /// One-shot wrapper that accepts a hosted package link, an extension id, or a store URL and
    /// takes the right path for each.
    /// </summary>
    /// <param name="linkOrId">A hosted package link, an extension id, or a Chrome Web Store URL.</param>
    /// <param name="options">Proxy and timeout configuration; defaults are used when null.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The validated package.</returns>
    /// <exception cref="ArgumentException">The input was neither a link, an id nor a store URL.</exception>
    /// <exception cref="CrxTransportException">The service or link was unreachable, or redirected too often.</exception>
    /// <exception cref="CrxNoPackageException">No package url was offered for the extension.</exception>
    /// <exception cref="CrxValidationException">The payload failed validation.</exception>
    public static async Task<CrxDownloadResult> FetchLinkAsync(
        string linkOrId,
        CrxDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (CrxLink.TryCreate(linkOrId, out var link) && link is not null)
        {
            // A signed package url embeds the id in its filename, so use it to pin the identity
            // of what came back instead of trusting the request.
            var expected = CrxLink.TryGetExtensionId(link, out var embedded) ? embedded : null;

            using var direct = new CrxDownloader(options);
            return await direct
                .DownloadFromLinkAsync(link, expected, cancellationToken)
                .ConfigureAwait(false);
        }

        return await FetchAsync(linkOrId, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a hosted package, following redirects explicitly so the final url is known and
    /// the hop count is bounded.
    /// </summary>
    private async Task<(byte[] Package, Uri FinalUri)> FetchHostedPackageAsync(
        Uri link,
        CancellationToken cancellationToken)
    {
        const int MaxHops = 10;

        // The update client does not follow redirects, so each hop is inspected rather than
        // being resolved invisibly by the handler.
        var current = link;

        for (var hops = 0; hops <= MaxHops; hops++)
        {
            using var response = await _updateClient
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is not { } location)
                {
                    throw new CrxTransportException(
                        $"{current} answered {(int)response.StatusCode} with no Location header.",
                        new InvalidOperationException("missing Location"));
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CrxTransportException(
                    $"{current} answered {(int)response.StatusCode}.",
                    new InvalidOperationException(response.ReasonPhrase ?? "unexpected status"));
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > CrxDownloadOptions.MaxPackageBytes)
            {
                throw new CrxTransportException(
                    $"{current} declares {declared} bytes, above the "
                    + $"{CrxDownloadOptions.MaxPackageBytes} byte limit.",
                    new InvalidOperationException("package too large"));
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            return (bytes, current);
        }

        throw new CrxTransportException(
            $"Gave up after {MaxHops} redirects starting at {link}.",
            new InvalidOperationException("redirect loop"));
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    /// <summary>
    /// Parses and validates a CRX that has already been downloaded.
    /// </summary>
    /// <param name="package">Raw CRX bytes.</param>
    /// <param name="extensionId">
    /// The id the package is expected to belong to. An empty string skips the identity check,
    /// for links that do not say which extension they serve.
    /// </param>
    /// <param name="requireSignature">Whether an unverifiable signature is fatal.</param>
    /// <returns>What the package declares about itself.</returns>
    /// <exception cref="CrxValidationException">The payload is malformed, belongs to another id, or is unsigned.</exception>
    public static CrxInfo Validate(
        byte[] package,
        string extensionId,
        bool requireSignature = true)
    {
        ArgumentNullException.ThrowIfNull(package);

        CrxInfo info;
        try
        {
            info = CrxFile.Inspect(package);
        }
        catch (InvalidDataException ex)
        {
            throw new CrxValidationException($"Payload is not a valid CRX: {ex.Message}", ex);
        }

        if (extensionId.Length > 0 && !string.Equals(info.ExtensionId, extensionId, StringComparison.Ordinal))
        {
            throw new CrxValidationException(
                $"Id mismatch: asked for {extensionId} but the package is signed for {info.ExtensionId}. " +
                "The download was tampered with or the wrong artifact was served.");
        }

        if (requireSignature && !info.SignatureVerified)
        {
            throw new CrxValidationException(
                $"Signature check failed ({info.SignatureDetail}).");
        }

        return info;
    }

    /// <summary>Releases the underlying HTTP clients.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateClient.Dispose();
        _downloadClient.Dispose();
    }

    private async Task<byte[]> FetchPackageAsync(Uri packageUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        using var response = await _downloadClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > CrxDownloadOptions.MaxPackageBytes)
        {
            throw new HttpRequestException(
                $"Package at {packageUri} declares {response.Content.Headers.ContentLength} bytes, " +
                $"above the {CrxDownloadOptions.MaxPackageBytes} byte limit.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Extracts the ZIP archive embedded in a CRX package.</summary>
public static class CrxArchive
{
    /// <summary>
    /// Extracts the archive portion of a CRX into a directory, overwriting existing files.
    /// </summary>
    /// <param name="package">Raw CRX bytes.</param>
    /// <param name="info">The parse result describing where the archive starts.</param>
    /// <param name="destinationDirectory">Directory to extract into; created if missing.</param>
    public static void ExtractToDirectory(byte[] package, CrxInfo info, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        using var archive = new ZipArchive(
            new MemoryStream(package, info.ZipOffset, info.ZipLength, writable: false), ZipArchiveMode.Read);

        archive.ExtractToDirectory(destinationDirectory, overwriteFiles: true);
    }
}