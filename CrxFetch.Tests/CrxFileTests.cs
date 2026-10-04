using System.IO.Compression;
using System.Security.Cryptography;

namespace CrxFetch.Tests;

public sealed class CrxFileTests
{
    private const string GenuineExtensionId = "ojjgnpkioondelmggbekfhllhdaimnho";
    private const string GenuineTestDataPath = "TestData/valid_publisher.crx3";

    private static byte[] LoadRealTestData() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, GenuineTestDataPath));

    [Fact]
    public void GenuineChromiumPackageVerifies()
    {
        var info = CrxFile.Inspect(LoadRealTestData());

        Assert.Equal(CrxFormat.Crx3, info.Format);
        Assert.Equal(GenuineExtensionId, info.ExtensionId);
        Assert.True(info.SignatureVerified, info.SignatureDetail);
        Assert.Contains("developer key ok", info.SignatureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void GenuineChromiumArchiveOpensAtTheReportedOffset()
    {
        var package = LoadRealTestData();
        var info = CrxFile.Inspect(package);

        using var archive = new ZipArchive(
            new MemoryStream(package, info.ZipOffset, info.ZipLength, writable: false));

        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Equal(info.ZipLength, package.Length - info.ZipOffset);
    }

    [Fact]
    public void SyntheticPackageVerifies()
    {
        using var key = Crx3Builder.CreateKey();
        var archive = Crx3Builder.BuildArchive("""{"manifest_version":3,"name":"Test","version":"1.0.0"}""");
        var info = CrxFile.Inspect(Crx3Builder.Build(key, archive));

        Assert.Equal(CrxFormat.Crx3, info.Format);
        Assert.Equal(Crx3Builder.IdFor(key), info.ExtensionId);
        Assert.True(info.SignatureVerified, info.SignatureDetail);
        Assert.Equal(archive.Length, info.ZipLength);
    }

    [Fact]
    public void TamperedArchiveFailsVerification()
    {
        using var key = Crx3Builder.CreateKey();
        var archive = Crx3Builder.BuildArchive("""{"manifest_version":3,"name":"Test"}""");
        var package = Crx3Builder.Build(key, archive);

        package[^1] ^= 0xFF;

        var info = CrxFile.Inspect(package);
        Assert.Equal(Crx3Builder.IdFor(key), info.ExtensionId);
        Assert.False(info.SignatureVerified);
    }

    [Fact]
    public void DeclaredIdSubstitutionIsRejected()
    {
        using var key = Crx3Builder.CreateKey();
        var archive = Crx3Builder.BuildArchive("""{"manifest_version":3}""");

        // A crx_id that no proof carries a matching public key for.
        var package = Crx3Builder.Build(key, archive, declaredCrsId: Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());

        var ex = Assert.Throws<InvalidDataException>(() => CrxFile.Inspect(package));
        Assert.Contains("no proof carries the matching public key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveOnlySignatureIsRejected()
    {
        using var key = Crx3Builder.CreateKey();
        var archive = Crx3Builder.BuildArchive("""{"manifest_version":3}""");

        // The mistake most reimplementations make: signing only the ZIP. Chromium signs the
        // context string, the signed-header length, the signed header and then the archive, so
        // a package built this way must not verify.
        var package = Crx3Builder.Build(key, archive, signArchiveOnly: true);

        var info = CrxFile.Inspect(package);
        Assert.False(info.SignatureVerified);
    }

    [Fact]
    public void IdIsDerivedTheSameWayAsChromium()
    {
        using var key = Crx3Builder.CreateKey();
        var publicKeyInfo = key.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(publicKeyInfo);

        var validChars = "abcdefghijklmnop";
        var expected = new string(
            [.. Enumerable.Range(0, 16).SelectMany(i => new[] {
                    validChars[hash[i] >> 4], validChars[hash[i] & 0x0F]
                })
            ]
        );

        Assert.Equal(expected, CrxFile.DeriveExtensionId(publicKeyInfo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(4)]
    public void NonCrxBytesAreRejected(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => CrxFile.Inspect(bytes));
        Assert.Contains("Not a CRX file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedPackageIsRejected()
    {
        var package = LoadRealTestData();
        var truncated = package.AsSpan(0, 600).ToArray();

        Assert.Throws<InvalidDataException>(() => CrxFile.Inspect(truncated));
    }

    [Fact]
    public void UnsupportedVersionIsRejected()
    {
        var package = LoadRealTestData();
        package[4] = 9;

        var ex = Assert.Throws<InvalidDataException>(() => CrxFile.Inspect(package));
        Assert.Contains("Unsupported CRX version", ex.Message, StringComparison.Ordinal);
    }
}
