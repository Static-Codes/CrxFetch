namespace CrxFetch.Tests;

public sealed class ValidationTests
{
    private const string ExtensionId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    [Fact]
    public void MatchingPackagePassesValidation()
    {
        using var key = Crx3Builder.CreateKey();
        var id = Crx3Builder.IdFor(key);
        var package = Crx3Builder.Build(key, Crx3Builder.BuildArchive("""{"manifest_version":3}"""));

        var info = CrxDownloader.Validate(package, id);

        Assert.Equal(id, info.ExtensionId);
        Assert.True(info.SignatureVerified, info.SignatureDetail);
    }

    [Fact]
    public void PackageSignedForAnotherIdIsRejected()
    {
        using var key = Crx3Builder.CreateKey();
        var package = Crx3Builder.Build(key, Crx3Builder.BuildArchive("""{"manifest_version":3}"""));

        var ex = Assert.Throws<CrxValidationException>(
            () => CrxDownloader.Validate(package, ExtensionId));

        Assert.Equal(CrxFailureReason.Validation, ex.Reason);
        Assert.Contains("Id mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Crx3Builder.IdFor(key), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedPackageIsRejectedWhenSignatureIsRequired()
    {
        using var key = Crx3Builder.CreateKey();
        var id = Crx3Builder.IdFor(key);
        var package = Crx3Builder.Build(key, Crx3Builder.BuildArchive("""{"manifest_version":3}"""));
        package[^1] ^= 0xFF;

        var ex = Assert.Throws<CrxValidationException>(() => CrxDownloader.Validate(package, id));

        Assert.Equal(CrxFailureReason.Validation, ex.Reason);
        Assert.Contains("Signature check failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedPackageIsReturnedWhenSignatureIsNotRequired()
    {
        using var key = Crx3Builder.CreateKey();
        var id = Crx3Builder.IdFor(key);
        var package = Crx3Builder.Build(key, Crx3Builder.BuildArchive("""{"manifest_version":3}"""));
        package[^1] ^= 0xFF;

        var info = CrxDownloader.Validate(package, id, requireSignature: false);

        Assert.Equal(id, info.ExtensionId);
        Assert.False(info.SignatureVerified);
    }

    [Fact]
    public void MalformedPayloadIsRejected()
    {
        var ex = Assert.Throws<CrxValidationException>(
            () => CrxDownloader.Validate("not a crx at all, really"u8.ToArray(), ExtensionId));

        Assert.Equal(CrxFailureReason.Validation, ex.Reason);
        Assert.Contains("not a valid CRX", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullPackageIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CrxDownloader.Validate(null!, ExtensionId));
    }
}