using System.Net;

namespace CrxFetch;

/// <summary>
/// Builds the HTTP clients used to talk to Google's update service and to the signed CDN
/// host that serves the package.
/// </summary>
/// <remarks>
/// Two clients are needed because their redirect behaviour has to differ: the update service
/// returns the package url as a <c>Location</c> header that we need to read, while the CDN url
/// we then fetch may redirect onwards and has to be followed.
/// </remarks>
public static class CrxHttp
{
    /// <summary>
    /// Creates a client for the update service. Redirects are not followed so that the
    /// <c>Location</c> header holding the signed package url stays visible.
    /// </summary>
    /// <param name="options">Proxy and timeout configuration.</param>
    /// <returns>A client configured with Chrome-like request headers.</returns>
    public static HttpClient CreateUpdateClient(CrxDownloadOptions options) =>
        Create(options, allowAutoRedirect: false);

    /// <summary>
    /// Creates a client for downloading a package. Redirects are followed, because the signed
    /// CDN url can itself redirect.
    /// </summary>
    /// <param name="options">Proxy and timeout configuration.</param>
    /// <returns>A client configured with Chrome-like request headers.</returns>
    public static HttpClient CreateDownloadClient(CrxDownloadOptions options) =>
        Create(options, allowAutoRedirect: true);

    private static HttpClient Create(CrxDownloadOptions options, bool allowAutoRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = options.ConnectTimeout,
        };

        // A null Proxy leaves the handler on its default behaviour, which is to honour any
        // system proxy configuration from the environment.
        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = ProxyEndpoint.Parse(options.Proxy);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler) { Timeout = options.Timeout };

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", ChromeUpdateService.UserAgentFor(options.ChromeVersion));
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://chromewebstore.google.com/");

        return http;
    }
}