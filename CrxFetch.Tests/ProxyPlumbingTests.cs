using System.Net.Sockets;

namespace CrxFetch.Tests;

/// <summary>
/// Proves proxying is actually wired into the HTTP stack, rather than being silently ignored,
/// and does so deterministically with a local proxy instead of a public one.
/// </summary>
public sealed class ProxyPlumbingTests
{
    private const string SettledExtensionId = "aapbdbdomjkkjkaonfhkkikfgjllcleb";

    private static CrxDownloadOptions FastFail(string proxy) => new()
    {
        Proxy = proxy,
        Timeout = TimeSpan.FromSeconds(90),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks4")]
    [InlineData("socks4a")]
    [InlineData("socks5")]
    public async Task EverySchemeIsActuallyDialled(string scheme)
    {
        // Take a port nothing is listening on. If the scheme were ignored the request would
        // succeed over direct egress, so a transport failure is the proof that the handler
        // routed through the proxy we named.
        string deadEndpoint;
        await using (var proxy = new ConnectProxy())
        {
            deadEndpoint = $"{scheme}://127.0.0.1:{proxy.Port}";
        }

        using var downloader = new CrxDownloader(FastFail(deadEndpoint));

        var ex = await Assert.ThrowsAsync<CrxTransportException>(
            () => downloader.RetrieveAsync(SettledExtensionId, TestContext.Current.CancellationToken));

        Assert.Equal(CrxFailureReason.Transport, ex.Reason);
        Assert.Contains(deadEndpoint.Split("://")[1], ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedSchemeIsRejectedBeforeAnyRequest()
    {
        // The endpoint is rejected while the client is being built, so no request is attempted.
        var ex = Assert.Throws<ArgumentException>(() => new CrxDownloader(FastFail("ftp://127.0.0.1:21")));

        Assert.Contains("ftp://127.0.0.1:21", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Retrieve_And_Download_ThroughALocalProxy()
    {
        await using var proxy = new ConnectProxy();
        proxy.Start();

        CrxDownloadResult result;
        try
        {
            result = await CrxDownloader.FetchAsync(
                SettledExtensionId,
                FastFail(proxy.Endpoint),
                TestContext.Current.CancellationToken);
        }
        catch (CrxTransportException ex)
        {
            Assert.Skip($"the update service is unreachable through the local proxy: {ex.Message}");
            return;
        }
        catch (CrxNoPackageException ex)
        {
            Assert.Skip($"the service offered no package through the local proxy: {ex.Message}");
            return;
        }

        // The payload has to be right...
        Assert.Equal(SettledExtensionId, result.Info.ExtensionId);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
        Assert.Equal(CrxFormat.Crx3, result.Info.Format);

        // ...and the requests must have gone through the proxy we supplied.
        Assert.Contains(proxy.ConnectTargets, target => target.Contains("clients2.google.com", StringComparison.Ordinal));
        Assert.Contains(proxy.ConnectTargets, target => target.Contains("googleusercontent", StringComparison.Ordinal));
    }
}