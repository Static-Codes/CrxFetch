namespace CrxFetch.Tests;

public sealed class ProxyEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "http")]
    [InlineData("https://127.0.0.1:8443", "https")]
    [InlineData("socks4://127.0.0.1:1080", "socks4")]
    [InlineData("socks4a://127.0.0.1:1080", "socks4a")]
    [InlineData("socks5://127.0.0.1:1080", "socks5")]
    public void EverySupportedSchemeParses(string endpoint, string expectedScheme)
    {
        Assert.True(ProxyEndpoint.TryParse(endpoint, out var parsed), $"'{endpoint}' should parse");
        var proxy = Assert.IsType<System.Net.WebProxy>(parsed);

        Assert.Equal(new Uri(endpoint).Port, proxy.Address!.Port);
        Assert.Equal(expectedScheme, proxy.Address.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1:8080")]
    [InlineData("proxy.example.invalid:3128")]
    public void BareHostPortDefaultsToHttp(string endpoint)
    {
        Assert.True(ProxyEndpoint.TryParse(endpoint, out var parsed), $"'{endpoint}' should parse");
        var proxy = Assert.IsType<System.Net.WebProxy>(parsed);

        Assert.Equal("http", proxy.Address!.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("socks6://127.0.0.1:1080")]
    [InlineData("not a uri")]
    [InlineData("http://")]
    [InlineData("socks5://127.0.0.1")]
    public void UnusableEndpointIsRejected(string? endpoint)
    {
        Assert.False(ProxyEndpoint.TryParse(endpoint, out var proxy));
        Assert.Null(proxy);
    }

    [Fact]
    public void ParseThrowsOnUnusableEndpoint()
    {
        var ex = Assert.Throws<ArgumentException>(() => ProxyEndpoint.Parse("ftp://127.0.0.1:21"));
        Assert.Contains("socks5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentedSchemeIsListed()
    {
        Assert.Equal(["http", "https", "socks4", "socks4a", "socks5"], ProxyEndpoint.SupportedSchemes);
    }
}