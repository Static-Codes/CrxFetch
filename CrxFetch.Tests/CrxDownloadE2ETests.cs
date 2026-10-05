using System.Net;

namespace CrxFetch.Tests;

/// <summary>
/// End-to-end suites talk to the live update service. They run sequentially so they do not
/// fight over proxies, and they skip themselves when the environment supplies no endpoint.
/// </summary>
[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollection;

[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class CrxDownloadE2ETests
{
    /// <summary> Google Translate was published in 2010 and will likely never stopped being served. </summary>
    private static readonly string SettledExtensionId = "aapbdbdomjkkjkaonfhkkikfgjllcleb";

    private static readonly string SettledStoreUrl = $"https://chromewebstore.google.com/detail/google-translate/{SettledExtensionId}";

    private static CrxDownloadOptions OptionsFor(string? proxy) => new()
    {
        Proxy = proxy,
        Timeout = TimeSpan.FromSeconds(90),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    };

    private static Task<CrxDownloadResult> FetchAsync(string idOrUrl, string? proxy) =>
        CrxDownloader.FetchAsync(idOrUrl, OptionsFor(proxy), TestContext.Current.CancellationToken);

    private static string RequireProxy(string scheme)
    {
        var proxy = ProxyPool.For(scheme);
        if (proxy is null)
        {
            Assert.Skip($"No {scheme} proxy configured ({ProxyPool.Describe()}).");
        }

        return proxy;
    }

    private static void AssertLooksLikeAPackage(Uri packageUri)
    {
        Assert.True(
            packageUri.Scheme is "https" or "http",
            $"package url should be http(s), got {packageUri}");

        Assert.Contains(
            "google",
            packageUri.Host,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retrieve_WithoutProxy_ReachesTheServiceAndEitherResolvesOrDeclines()
    {
        using var downloader = new CrxDownloader(OptionsFor(proxy: null));

        GupResult result;
        try
        {
            result = await downloader.RetrieveAsync(SettledExtensionId, TestContext.Current.CancellationToken);
        }
        catch (CrxTransportException ex)
        {
            // Direct egress being unreachable is a legitimate outcome, not a library fault.
            Assert.Skip($"direct egress to the update service is unavailable: {ex.Message}");
            return;
        }

        Assert.NotEmpty(result.Attempts);

        if (result.PackageUri is null)
        {
            // The documented refusal: 204 with an empty body and no Location header.
            Assert.True(
                result.Attempts.Any(a => a.Status == HttpStatusCode.NoContent),
                $"expected a package url or a 204 refusal, saw: {Describe(result)}");

            return;
        }

        AssertLooksLikeAPackage(result.PackageUri);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks4")]
    [InlineData("socks5")]
    public async Task Retrieve_WithProxy_ResolvesAPackageUrl(string scheme)
    {
        var proxy = RequireProxy(scheme);
        using var downloader = new CrxDownloader(OptionsFor(proxy));

        GupResult result;
        try
        {
            result = await downloader.RetrieveAsync(SettledExtensionId, TestContext.Current.CancellationToken);
        }
        catch (CrxTransportException ex)
        {
            Assert.Skip($"{scheme} proxy {proxy} is not usable: {ex.Message}");
            return;
        }

        if (result.PackageUri is null)
        {
            // The proxy reached Google but the service declined to serve from that egress.
            Assert.Skip($"{scheme} proxy {proxy} reached the service but got no package: {Describe(result)}");
            return;
        }

        AssertLooksLikeAPackage(result.PackageUri);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks4")]
    [InlineData("socks5")]
    public async Task Download_And_Inspect_WithProxy_ProducesAVerifiedPackage(string scheme)
    {
        var proxy = RequireProxy(scheme);

        CrxDownloadResult result;
        try
        {
            result = await FetchAsync(SettledExtensionId, proxy);
        }
        catch (CrxException ex)
        {
            // A free proxy can be flaky or die mid-flight; that is not a library defect.
            Assert.Skip($"{scheme} proxy {proxy} did not complete the fetch: {ex.Message}");
            return;
        }

        AssertVerified(result);
    }

    [Fact]
    public async Task Download_And_Inspect_WithoutProxy_ProducesAVerifiedPackageOrDeclines()
    {
        CrxDownloadResult result;
        try
        {
            result = await FetchAsync(SettledExtensionId, proxy: null);
        }
        catch (CrxNoPackageException ex)
        {
            Assert.True(
                ex.DeclinedWithNoContent || ex.Attempts.Count > 0,
                "a refusal should still record what was attempted");

            Assert.Skip($"direct egress was declined by the service: {ex.Message}");
            return;
        }
        catch (CrxTransportException ex)
        {
            Assert.Skip($"direct egress is unavailable: {ex.Message}");
            return;
        }

        AssertVerified(result);
    }

    [Fact]
    public async Task StoreUrlAndBareIdProduceTheSamePackage()
    {
        var proxy = ProxyPool.For("socks5") ?? ProxyPool.For("http") ?? ProxyPool.For("socks4");

        if (proxy is null) { Assert.Skip(ProxyPool.Describe()); }

        CrxDownloadResult fromId;
        CrxDownloadResult fromUrl;
        try
        {
            fromId = await FetchAsync(SettledExtensionId, proxy);
            fromUrl = await FetchAsync(SettledStoreUrl, proxy);
        }
        catch (CrxException ex)
        {
            Assert.Skip($"proxy {proxy} did not complete both fetches: {ex.Message}");
            return;
        }

        Assert.Equal(fromId.Info.ExtensionId, fromUrl.Info.ExtensionId);
        Assert.Equal(fromId.Package, fromUrl.Package);
    }

    [Fact]
    public async Task PayloadIsIdenticalAcrossEveryAvailableEgress()
    {
        var schemes = ProxyPool.ExercisedSchemes
            .Where(s => s != "none" && ProxyPool.For(s) is not null)
            .ToList();

        if (schemes.Count < 2)
        {
            Assert.Skip($"Need proxies for at least two schemes to compare egress; {ProxyPool.Describe()}.");
        }

        CrxDownloadResult reference;
        try
        {
            reference = await FetchAsync(SettledExtensionId, ProxyPool.For(schemes[0]));
        }
        catch (CrxException ex)
        {
            Assert.Skip($"no usable baseline proxy: {ex.Message}");
            return;
        }

        var compared = 0;

        foreach (var scheme in schemes.Skip(1))
        {
            CrxDownloadResult viaScheme;

            try { viaScheme = await FetchAsync(SettledExtensionId, ProxyPool.For(scheme)); }

            // If a proxy is non responsive, it is of no use, therefore its discarded immediately
            catch (CrxException) { continue; }

            compared++;

            Assert.Equal(reference.Package.Length, viaScheme.Package.Length);

            Assert.Equal(reference.Package, viaScheme.Package);
        }

        Assert.True(compared > 0, $"could not compare a second egress; {ProxyPool.Describe()}");
    }

    [Fact]
    public async Task ExtractedArchiveContainsTheManifest()
    {
        var proxy = ProxyPool.For("socks5") ?? ProxyPool.For("socks4") ?? ProxyPool.For("http");
        if (proxy is null)
        {
            Assert.Skip(ProxyPool.Describe());
        }

        CrxDownloadResult result;
        try
        {
            result = await FetchAsync(SettledExtensionId, proxy);
        }
        catch (CrxException ex)
        {
            Assert.Skip($"proxy {proxy} did not complete the fetch: {ex.Message}");
            return;
        }

        var destination = Path.Combine(Path.GetTempPath(), $"crxfetch-e2e-{Guid.NewGuid():N}");

        try
        {
            CrxArchive.ExtractToDirectory(result.Package, result.Info, destination);

            var manifest = Path.Combine(destination, "manifest.json");
            Assert.True(File.Exists(manifest), "manifest.json should be in the extracted archive");
            Assert.Contains("manifest_version", await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    private static void AssertVerified(CrxDownloadResult result)
    {
        Assert.Equal(SettledExtensionId, result.Info.ExtensionId);
        Assert.Equal(CrxFormat.Crx3, result.Info.Format);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
        Assert.True(result.Package.Length > 0);
        Assert.True(
            result.Info.ZipOffset > 0 && result.Info.ZipLength > 0,
            $"unexpected archive bounds: offset {result.Info.ZipOffset}, length {result.Info.ZipLength}"
        );
        AssertLooksLikeAPackage(result.PackageUri);
    }

    private static string Describe(GupResult result) =>
        string.Join(", ", result.Attempts.Select(a => $"{a.Description}={(int)a.Status}"));
}