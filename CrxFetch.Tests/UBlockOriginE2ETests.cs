using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace CrxFetch.Tests;

/// <summary>
/// Covers uBlock Origin, the extension people usually reach for.
/// </summary>
/// <remarks>
/// It is worth pinning down explicitly because it behaves unlike the vintage extensions the
/// other end-to-end suites use: it is not on Google's extension-service allowlist, so the
/// update service answers 204 No Content (or "noupdate" with no codebase) and no egress can
/// produce a package for it. This test therefore asserts whichever of those outcomes actually
/// occurs, and holds the payload to the same bar as any other extension the moment the service
/// does start serving it.
/// </remarks>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class UBlockOriginE2ETests
{
    private const string UBlockId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    private const string StoreUrl =
        "https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm";

    private static CrxDownloadOptions OptionsFor(string? proxy) => new()
    {
        Proxy = proxy,
        Timeout = TimeSpan.FromSeconds(90),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    };

    [Fact]
    public async Task Download_IsValidated_WhenTheServiceServesIt()
    {
        var proxy = ProxyPool.For("socks5") ?? ProxyPool.For("socks4") ?? ProxyPool.For("http");

        CrxDownloadResult result;
        try
        {
            result = await CrxDownloader.FetchAsync(
                UBlockId, OptionsFor(proxy), TestContext.Current.CancellationToken);
        }
        catch (CrxNoPackageException ex)
        {
            AssertRefusal(ex);
            return;
        }
        catch (CrxTransportException ex)
        {
            Assert.Skip($"the update service is unreachable: {ex.Message}");
            return;
        }

        // Reachable one day. Prove the bytes really are uBlock Origin rather than trusting
        // the id we asked for: the id is re-derived from the embedded public key.
        Assert.Equal(UBlockId, result.Info.ExtensionId);
        Assert.Equal(CrxFormat.Crx3, result.Info.Format);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
        Assert.True(result.Info.ZipOffset > 0 && result.Info.ZipLength > 0);
        Assert.Equal(
            result.Package.Length - result.Info.ZipOffset, result.Info.ZipLength);
        Assert.True(
            result.PackageUri.Scheme is "https" or "http",
            $"package url should be http(s), got {result.PackageUri}");

        await AssertManifestAsync(result);
    }

    [Fact]
    public async Task StoreUrlAndBareIdAddressTheSameExtension()
    {
        var proxy = ProxyPool.For("socks5") ?? ProxyPool.For("socks4") ?? ProxyPool.For("http");

        using var downloader = new CrxDownloader(OptionsFor(proxy));

        GupResult fromId;
        GupResult fromUrl;
        try
        {
            fromId = await downloader.RetrieveAsync(UBlockId, TestContext.Current.CancellationToken);
            fromUrl = await downloader.RetrieveAsync(StoreUrl, TestContext.Current.CancellationToken);
        }
        catch (CrxTransportException ex)
        {
            Assert.Skip($"the update service is unreachable: {ex.Message}");
            return;
        }

        // Both must reach the same conclusion about the same app. When it is served, both
        // must agree on the package url; when it is refused, both must be refused.
        if (fromId.PackageUri is null || fromUrl.PackageUri is null)
        {
            Assert.Null(fromId.PackageUri);
            Assert.Null(fromUrl.PackageUri);
            return;
        }

        Assert.Equal(fromId.PackageUri, fromUrl.PackageUri);
    }

    private static void AssertRefusal(CrxNoPackageException ex)
    {
        Assert.NotEmpty(ex.Attempts);

        // A refusal is a deliberate answer, not a malformed request: either 204 with an empty
        // body, or 200 whose XML carries no codebase. Anything else would be a real fault.
        Assert.True(
            ex.Attempts.Any(a => a.Status is HttpStatusCode.NoContent or HttpStatusCode.OK),
            $"expected a refusal (204, or 200 with no package), saw: {Describe(ex.Attempts)}");

        Assert.True(
            ex.Attempts.All(a => a.Location is null && a.PackageUrlFromXml is null),
            "a refusal must not carry a package url");

        Assert.Skip(
            "uBlock Origin is outside the update service's extension allowlist, so no package is "
            + $"offered for it. Attempts: {Describe(ex.Attempts)}");
    }

    private static async Task AssertManifestAsync(CrxDownloadResult result)
    {
        using var archive = new ZipArchive(
            new MemoryStream(result.Package, result.Info.ZipOffset, result.Info.ZipLength, writable: false),
            ZipArchiveMode.Read);

        var manifest = archive.GetEntry("manifest.json");
        Assert.True(manifest is not null, "the archive should contain manifest.json");

        using var reader = new StreamReader(manifest!.Open());
        using var document = JsonDocument.Parse(
            await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var root = document.RootElement;
        Assert.True(root.TryGetProperty("manifest_version", out _), "manifest_version is required");
        Assert.True(root.TryGetProperty("version", out _), "version is required");
        Assert.False(root.TryGetProperty("key", out _), "a store package should not be a legacy keyed extension");
    }

    private static string Describe(IReadOnlyList<GupAttempt> attempts) =>
        string.Join(", ", attempts.Select(a => $"{a.Description}={(int)a.Status}"));
}