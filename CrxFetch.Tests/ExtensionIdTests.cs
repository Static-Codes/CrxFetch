namespace CrxFetch.Tests;

public sealed class ExtensionIdTests
{
    private const string KnownId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    [Fact]
    public void BareIdIsAccepted()
    {
        Assert.True(ExtensionId.TryParse(KnownId, out var id));
        Assert.Equal(KnownId, id);
    }

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chrome.google.com/webstore/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm/")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm/1.55.0.0")]
    public void StoreUrlYieldsTheId(string url)
    {
        Assert.True(ExtensionId.TryParse(url, out var id));
        Assert.Equal(KnownId, id);
    }

    [Fact]
    public void IdIsFoundInAQueryString()
    {
        Assert.True(ExtensionId.TryParse($"https://example.invalid/x?id={KnownId}", out var id));
        Assert.Equal(KnownId, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-id")]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("CJPalhdlNBpafiAmejdnhcphjbkeiagm")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiag")]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiagmm")]
    public void UnusableInputIsRejected(string? input)
    {
        Assert.False(ExtensionId.TryParse(input, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        Assert.True(ExtensionId.TryParse($"  {KnownId}\n", out var id));
        Assert.Equal(KnownId, id);
    }

    [Fact]
    public void UBlockOriginIsRecognisedFromIdAndStoreUrl()
    {
        const string uBlockId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
        const string uBlockUrl =
            "https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm";

        Assert.True(ExtensionId.TryParse(uBlockId, out var fromId));
        Assert.True(ExtensionId.TryParse(uBlockUrl, out var fromUrl));

        Assert.Equal(uBlockId, fromId);
        Assert.Equal(uBlockId, fromUrl);
    }
}