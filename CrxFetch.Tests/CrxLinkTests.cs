namespace CrxFetch.Tests;

public sealed class CrxLinkTests
{
    private const string UBlockBlob =
        "https://clients2.googleusercontent.com/crx/blobs/AZPVhcSooyIJq6QjSGE4i_qz3ygNartW-f8I_mI-bUiHfciI6cwXu" +
        "jPio-4m4fNRL7i_Ft0YPJKOKBcbFgCXKAk2Wedvugc7ZamxRtAFtv4Q4zg77EV3vqQoY7upRSwhMAxlKa5VRRNn5O4YB" +
        "Nnr-sDAVSJIQVjEgE/AAPBDBDOMJKKJKAONFHKKIKFGJLLCLEB_2_0_17_0.crx";

    [Theory]
    [InlineData(UBlockBlob)]
    [InlineData("https://clients2.google.com/service/update2/crx?response=redirect&x=id%3Daapbdbdomjkkjkaonfhkkikfgjllcleb%26uc")]
    [InlineData("https://edgedl.me.gvt1.com/edge/release2/component/abc.crx")]
    [InlineData("http://example.invalid/direct.crx")]
    public void HostedUrlsAreAcceptedAsLinks(string url)
    {
        Assert.True(CrxLink.TryCreate(url, out var link));
        Assert.NotNull(link);
    }

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chrome.google.com/webstore/detail/x/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    public void StoreUrlsAreNotTreatedAsLinks(string url)
    {
        // These describe an extension, so they resolve through the update service instead.
        Assert.False(CrxLink.TryCreate(url, out var link));
        Assert.Null(link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("not a url")]
    [InlineData("ftp://example.invalid/x.crx")]
    [InlineData("/relative/path.crx")]
    public void UnusableInputIsRejected(string? input)
    {
        Assert.False(CrxLink.TryCreate(input, out var link));
        Assert.Null(link);
    }

    [Fact]
    public void UppercaseIdIsRecoveredFromASignedUrl()
    {
        // Signed blob urls end in a filename carrying the id in uppercase.
        Assert.True(CrxLink.TryGetExtensionId(UBlockBlob, out var id));
        Assert.Equal("aapbdbdomjkkjkaonfhkkikfgjllcleb", id);
    }

    [Fact]
    public void LowercaseIdIsRecoveredFromARedirectUrl()
    {
        const string url =
            "https://clients2.google.com/service/update2/crx?response=redirect&x=id%3Dcjpalhdlnbpafiamejdnhcphjbkeiagm%26uc";

        Assert.True(CrxLink.TryGetExtensionId(url, out var id));
        Assert.Equal("cjpalhdlnbpafiamejdnhcphjbkeiagm", id);
    }

    [Theory]
    [InlineData("https://example.invalid/anything")]
    [InlineData("https://example.invalid/x")]
    [InlineData(null)]
    public void UrlWithoutAnIdYieldsNothing(string? url)
    {
        Assert.False(CrxLink.TryGetExtensionId(url, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Theory]
    [InlineData("https://example.invalid/a/b.crx", true)]
    [InlineData("https://example.invalid/a/b.CRX", true)]
    [InlineData("https://example.invalid/a/b.crx?token=abc", true)]
    [InlineData("https://example.invalid/a/b.zip", false)]
    [InlineData("https://example.invalid/crx", false)]
    public void CrxPathIsRecognisedRegardlessOfQueryString(string url, bool expected)
    {
        Assert.Equal(expected, CrxLink.LooksLikeCrxPath(new Uri(url)));
    }
}

public sealed class HostedLinkDownloadTests : IDisposable
{
    private readonly LocalCrxServer _server = new();

    public void Dispose() => _server.Dispose();

    private static CrxDownloadOptions Fast() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Timeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task DirectUrlIsDownloadedAndValidated()
    {
        using var downloader = new CrxDownloader(Fast());

        var result = await downloader.DownloadFromLinkAsync(
            _server.Direct(), LocalCrxServer.ExtensionId, TestContext.Current.CancellationToken);

        Assert.Equal(LocalCrxServer.ExtensionId, result.Info.ExtensionId);
        Assert.Equal(CrxFormat.Crx3, result.Info.Format);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
        Assert.Equal(_server.Direct(), result.PackageUri);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public async Task RedirectUrlIsFollowedToThePackage()
    {
        using var downloader = new CrxDownloader(Fast());

        var result = await downloader.DownloadFromLinkAsync(
            _server.Redirect(), LocalCrxServer.ExtensionId, TestContext.Current.CancellationToken);

        // The reported uri is where the bytes actually came from, not where the walk started.
        Assert.Equal(_server.Direct(), result.PackageUri);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
    }

    [Fact]
    public async Task RelativeAndMultiHopRedirectsAreFollowed()
    {
        using var downloader = new CrxDownloader(Fast());

        var result = await downloader.DownloadFromLinkAsync(
            _server.MultiHop(), LocalCrxServer.ExtensionId, TestContext.Current.CancellationToken);

        Assert.Equal(_server.Direct(), result.PackageUri);
        Assert.True(result.Info.SignatureVerified, result.Info.SignatureDetail);
    }

    [Fact]
    public async Task MissingLinkIsReportedAsATransportFailure()
    {
        using var downloader = new CrxDownloader(Fast());

        var ex = await Assert.ThrowsAsync<CrxTransportException>(
            () => downloader.DownloadFromLinkAsync(_server.Missing(), null, TestContext.Current.CancellationToken));

        Assert.Equal(CrxFailureReason.Transport, ex.Reason);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectWithoutLocationIsReported()
    {
        using var downloader = new CrxDownloader(Fast());

        var ex = await Assert.ThrowsAsync<CrxTransportException>(
            () => downloader.DownloadFromLinkAsync(
                _server.RedirectWithoutLocation(), null, TestContext.Current.CancellationToken));

        Assert.Contains("no Location header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectLoopIsBounded()
    {
        using var downloader = new CrxDownloader(Fast());

        var ex = await Assert.ThrowsAsync<CrxTransportException>(
            () => downloader.DownloadFromLinkAsync(_server.Loop(), null, TestContext.Current.CancellationToken));

        Assert.Contains("redirects", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadThatIsNotACrxIsRejected()
    {
        using var downloader = new CrxDownloader(Fast());

        var ex = await Assert.ThrowsAsync<CrxValidationException>(
            () => downloader.DownloadFromLinkAsync(_server.NotACrx(), null, TestContext.Current.CancellationToken));

        Assert.Equal(CrxFailureReason.Validation, ex.Reason);
    }

    [Fact]
    public async Task PackageForTheWrongExtensionIsRejected()
    {
        using var downloader = new CrxDownloader(Fast());

        var ex = await Assert.ThrowsAsync<CrxValidationException>(
            () => downloader.DownloadFromLinkAsync(
                _server.Direct(), "cjpalhdlnbpafiamejdnhcphjbkeiagm", TestContext.Current.CancellationToken));

        Assert.Contains("Id mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyBodyIsRejected()
    {
        using var downloader = new CrxDownloader(Fast());

        await Assert.ThrowsAsync<CrxValidationException>(
            () => downloader.DownloadFromLinkAsync(
                _server.Url("empty.crx"), null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ftp://example.invalid/x.crx")]
    [InlineData("https://chromewebstore.google.com/detail/x/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    public async Task NonLinkInputIsRejectedBeforeAnyRequest(string url)
    {
        using var downloader = new CrxDownloader(Fast());

        await Assert.ThrowsAsync<ArgumentException>(
            () => downloader.DownloadFromLinkAsync(new Uri(url), null, TestContext.Current.CancellationToken));
    }
}