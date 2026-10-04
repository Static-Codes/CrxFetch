using System.Net;
using System.Text.RegularExpressions;

namespace CrxFetch;

/// <summary>One request shape that was tried against the update service, and what came back.</summary>
/// <param name="Description">Human readable name of the request shape.</param>
/// <param name="Status">HTTP status returned.</param>
/// <param name="Location">Package url from the <c>Location</c> header, when one was sent.</param>
/// <param name="PackageUrlFromXml">Package url extracted from an <c>update_2</c> response body.</param>
public sealed record GupAttempt(
    string Description,
    HttpStatusCode Status,
    Uri? Location,
    string? PackageUrlFromXml);

/// <summary>The outcome of resolving a package url, including every attempt made.</summary>
/// <param name="PackageUri">The package url, or null when none was offered.</param>
/// <param name="Attempts">Every request shape that was tried, in order.</param>
public sealed record GupResult(Uri? PackageUri, IReadOnlyList<GupAttempt> Attempts);

/// <summary>
/// Talks to Google's Update Protocol (GUP) endpoint for Chrome extensions, the same
/// endpoint Chromium's own component updater uses, and turns whatever GUP hands back
/// into a downloadable package url.
/// </summary>
/// <remarks>
/// The endpoint answers in two different shapes. A <c>response=redirect</c> request returns
/// the package url as a <c>Location</c> header, but the service now answers most such
/// requests with 204 No Content. A <c>response=update_2</c> request returns an XML
/// <c>gupdate</c> document whose <c>updatecheck</c> element carries a <c>codebase</c>
/// attribute holding the same kind of url. Both are handled.
/// </remarks>
/// <param name="http">The client to send requests with.</param>
public sealed partial class ChromeUpdateService(HttpClient http)
{
    /// <summary>The update service endpoint for Chrome extensions.</summary>
    public const string CrxEndpoint = "https://clients2.google.com/service/update2/crx";

    /// <summary>Chrome version reported to the update service unless overridden.</summary>
    public const string DefaultChromeVersion = "155.0.8059.26";

    [GeneratedRegex(@"""codebase""\s*=\s*""(?<url>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CodebaseAttribute();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.CultureInvariant)]
    private static partial Regex AnyUrl();

    /// <summary>Builds the Chrome user agent reported for a given version.</summary>
    /// <param name="chromeVersion">Version to advertise.</param>
    /// <returns>A Chrome-shaped user agent string.</returns>
    public static string UserAgentFor(string chromeVersion) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        $"Chrome/{chromeVersion} Safari/537.36";

    /// <summary>
    /// Resolves the package url for an extension, trying several client identities until one
    /// is served.
    /// </summary>
    /// <param name="extensionId">A 32-character extension id.</param>
    /// <param name="chromeVersion">Chrome version to report.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The resolution result. <see cref="GupResult.PackageUri"/> is null when no shape yielded a url.</returns>
    public async Task<GupResult> ResolvePackageAsync(
        string extensionId,
        string chromeVersion,
        CancellationToken cancellationToken = default)
    {
        // Ordered by how much client identity we present. GUP decides whether to serve a
        // package based on that identity, so the most Chrome-like request is tried first.
        (string Description, string Response, string Product, string Channel)[] shapes =
        [
            ("chrome/stable redirect", "redirect", "chrome", "stable"),
            ("chrome/stable update_2", "update_2", "chrome", "stable"),
            ("chromecrx redirect", "redirect", "chromecrx", "unknown"),
            ("chromium/stable update_2", "update_2", "chromium", "stable"),
        ];

        var attempts = new List<GupAttempt>(shapes.Length);

        foreach (var shape in shapes)
        {
            var requestUri = BuildRequestUri(extensionId, chromeVersion, shape.Response, shape.Product, shape.Channel);
            using var response = await http
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            Uri? location = null;
            if (response.Headers.Location is { } headerLocation &&
                Uri.TryCreate(requestUri, headerLocation, out var resolved))
            {
                location = resolved;
            }

            Uri? fromXml = null;
            if (response.StatusCode == HttpStatusCode.OK && shape.Response == "update_2")
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var candidate = ExtractPackageUrl(body);
                if (candidate is not null && Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
                {
                    fromXml = parsed;
                }
            }

            attempts.Add(new GupAttempt(shape.Description, response.StatusCode, location, fromXml?.ToString()));

            if (location is not null || fromXml is not null)
            {
                return new GupResult(location ?? fromXml, attempts);
            }
        }

        return new GupResult(null, attempts);
    }

    private static Uri BuildRequestUri(
        string extensionId,
        string chromeVersion,
        string responseType,
        string product,
        string channel)
    {
        // GUP's `x` blob is itself url-encoded; the id is the only field Chrome requires.
        var x = $"id={Uri.EscapeDataString(extensionId)}&installsource=ondemand&uc";

        var query = string.Join("&",
        [
            $"response={responseType}",
            "os=win",
            "arch=x86-64",
            "os_arch=x86-64",
            "nacl_arch=x86-64",
            $"prod={product}",
            $"prodchannel={channel}",
            $"prodversion={Uri.EscapeDataString(chromeVersion)}",
            "acceptformat=crx2,crx3",
            $"x={Uri.EscapeDataString(x)}",
            "hl=en",
            "lang=en-US",
        ]);

        return new Uri($"{CrxEndpoint}?{query}");
    }

    private static string? ExtractPackageUrl(string xml)
    {
        foreach (Match match in CodebaseAttribute().Matches(xml))
        {
            var candidate = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (LooksLikePackage(candidate))
            {
                return candidate;
            }
        }

        foreach (Match match in AnyUrl().Matches(xml))
        {
            var candidate = WebUtility.HtmlDecode(match.Value);
            if (LooksLikePackage(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikePackage(string url) =>
        url.EndsWith(".crx", StringComparison.OrdinalIgnoreCase)
        || url.Contains("edgedl", StringComparison.OrdinalIgnoreCase)
        || url.Contains("redirector", StringComparison.OrdinalIgnoreCase)
        || url.Contains("googleusercontent", StringComparison.OrdinalIgnoreCase);
}