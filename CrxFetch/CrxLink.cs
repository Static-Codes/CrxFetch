using System.Text.RegularExpressions;

namespace CrxFetch;

/// <summary>
/// Recognises links that host a CRX package directly, so a download can skip the update
/// service entirely.
/// </summary>
/// <remarks>
/// Two shapes are supported. A <em>direct</em> link serves the bytes straight from a CDN, for
/// example the signed blob URL the update service hands back. A <em>redirect</em> link answers
/// 3xx and points at the package, which is what the update service's own
/// <c>response=redirect</c> endpoint produces. Both are handled by
/// <see cref="CrxDownloader.DownloadFromLinkAsync"/>.
/// </remarks>
public static partial class CrxLink
{
    /// <summary>
    /// Hosts whose URLs describe an extension rather than hosting a package, so they belong to
    /// the update-service path instead of the direct-link path.
    /// </summary>
    private static readonly string[] WebStoreHosts =
    [
        "chromewebstore.google.com",
        "chrome.google.com",
    ];

    /// <summary>
    /// Attempts to read an input as a hosted package link.
    /// </summary>
    /// <param name="input">An absolute http or https URL.</param>
    /// <param name="link">The parsed link on success; otherwise null.</param>
    /// <returns>
    /// <see langword="true"/> when the input is an absolute http(s) URL that is not a Chrome Web
    /// Store page. Chrome Web Store URLs address an extension, so they resolve through the
    /// update service rather than being fetched directly.
    /// </returns>
    public static bool TryCreate(string? input, out Uri? link)
    {
        link = null;

        if (string.IsNullOrWhiteSpace(input) ||
            !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            IsWebStoreHost(uri.Host))
        {
            return false;
        }

        link = uri;
        return true;
    }

    /// <summary>
    /// Reads an extension id out of a link, when the link embeds one.
    /// </summary>
    /// <param name="link">A hosted package link.</param>
    /// <param name="extensionId">The 32-character id found, or an empty string.</param>
    /// <returns>
    /// <see langword="true"/> when the link carries an id. Signed package urls end in a
    /// filename that embeds the uppercase id, so the match is case-insensitive.
    /// </returns>
    public static bool TryGetExtensionId(Uri link, out string extensionId)
    {
        ArgumentNullException.ThrowIfNull(link);
        return TryGetExtensionId(link.AbsoluteUri, out extensionId);
    }

    /// <summary>
    /// Reads an extension id out of a link, when the link embeds one.
    /// </summary>
    /// <param name="link">A hosted package link.</param>
    /// <param name="extensionId">The 32-character id found, or an empty string.</param>
    /// <returns><see langword="true"/> when the link carries an id.</returns>
    public static bool TryGetExtensionId(string? link, out string extensionId)
    {
        extensionId = string.Empty;

        if (string.IsNullOrWhiteSpace(link))
        {
            return false;
        }

        // Percent-decode before matching. A redirect url carries its id as "%3D<id>%26", and
        // both the "D" and the "6" fall inside the a-p range, so an encoded id can never be
        // delimited without decoding first.
        var decoded = Uri.UnescapeDataString(link);

        // The last run wins: a signed blob url is an opaque token followed by the id, and the
        // token itself can contain long letter runs that must not be mistaken for it.
        Match? found = null;
        foreach (Match match in EmbeddedUpperOrLowerId().Matches(decoded))
        {
            found = match;
        }

        if (found is null)
        {
            return false;
        }

        extensionId = found.Value.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// True when the url path looks like it names a package file.
    /// </summary>
    /// <param name="link">A hosted package link.</param>
    /// <returns><see langword="true"/> when the path ends in <c>.crx</c>.</returns>
    public static bool LooksLikeCrxPath(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.AbsolutePath.EndsWith(".crx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebStoreHost(string host) =>
        WebStoreHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase));

    // The guard classes are spelled out rather than relying on IgnoreCase, which would widen
    // them to [A-Za-z0-9] and let the "D" of a percent-encoded "=" block a legitimate match.
    [GeneratedRegex(@"(?<![a-pA-P0-9])[a-pA-P]{32}(?![a-pA-P0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUpperOrLowerId();
}