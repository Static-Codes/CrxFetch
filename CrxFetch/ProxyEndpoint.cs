using System.Net;

namespace CrxFetch;

/// <summary>
/// Parses proxy endpoints into <see cref="IWebProxy"/> instances.
/// </summary>
/// <remarks>
/// Supports the schemes <see cref="Http"/>, <see cref="Https"/>, <see cref="Socks4"/>,
/// <see cref="Socks4A"/> and <see cref="Socks5"/>. A bare <c>host:port</c> is treated as
/// <see cref="Http"/>.
/// </remarks>
public static class ProxyEndpoint
{
    /// <summary>Plain HTTP proxy, the default when no scheme is given.</summary>
    public const string Http = "http";

    /// <summary>HTTPS proxy: TLS is spoken to the proxy itself.</summary>
    public const string Https = "https";

    /// <summary>SOCKS4 proxy.</summary>
    public const string Socks4 = "socks4";

    /// <summary>SOCKS4a proxy, where hostname lookups are done by the proxy.</summary>
    public const string Socks4A = "socks4a";

    /// <summary>SOCKS5 proxy.</summary>
    public const string Socks5 = "socks5";

    /// <summary>Every proxy scheme this library accepts.</summary>
    public static IReadOnlyList<string> SupportedSchemes { get; } = [Http, Https, Socks4, Socks4A, Socks5];

    private static bool NotSupportedScheme(string scheme) => !SupportedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);

    // A scheme Uri does not know has Port == -1 when none was given, not 0.
    private static bool HostOrPortInvalid(int length, int port) => length == 0 || port <= 0;

    /// <summary>
    /// Attempts to parse a proxy endpoint. Returns <see langword="false"/> for null, empty,
    /// malformed or unsupported values, in which case <paramref name="proxy"/> is null.
    /// </summary>
    /// <param name="value">An endpoint such as <c>socks5://127.0.0.1:1080</c> or <c>127.0.0.1:8080</c>.</param>
    /// <param name="proxy">The parsed proxy on success; otherwise null.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a usable endpoint.</returns>
    public static bool TryParse(string? value, out IWebProxy? proxy)
    {
        proxy = null;

        if (string.IsNullOrWhiteSpace(value)) { return false; }

        var text = value.Trim();

        // Adding the seperator if not present.
        if (!text.Contains("://", StringComparison.Ordinal)) { text = $"{Http}://{text}"; }

        // Validating the provided Uri endpoint is schematically valid.
        if (
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            NotSupportedScheme(uri.Scheme) ||
            HostOrPortInvalid(uri.Host.Length, uri.Port)
        )
        {
            return false;
        }

        proxy = new WebProxy(uri);
        return true;
    }

    /// <summary>
    /// Parses a proxy endpoint, throwing when it cannot be used.
    /// </summary>
    /// <param name="value">An endpoint such as <c>socks4://127.0.0.1:1080</c>.</param>
    /// <returns>The parsed proxy.</returns>
    /// <exception cref="ArgumentException">The value is malformed or uses an unsupported scheme.</exception>
    public static IWebProxy Parse(string value)
    {
        if (!TryParse(value, out var proxy) || proxy is null)
        {
            throw new ArgumentException(
                $"Cannot parse proxy '{value}'. Use host:port or one of: {string.Join(", ", SupportedSchemes)}.",
                nameof(value));
        }

        return proxy;
    }
}