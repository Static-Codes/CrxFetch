using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CrxFetch.Tests;

/// <summary>
/// Builds CRX3 packages in memory, following crx3.proto, so signature verification can be
/// exercised without the network or checked-in test data.
/// </summary>
internal static class Crx3Builder
{
    private static ReadOnlySpan<byte> Magic => "Cr24"u8;
    private static ReadOnlySpan<byte> SignatureContext => "CRX3 SignedData\0"u8;

    public static byte[] BuildArchive(string manifestJson)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.json");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(manifestJson));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Assembles a CRX3 whose developer proof signs the real payload:
    /// "CRX3 SignedData\0" | uint32le(len(signed_header_data)) | signed_header_data | archive.
    /// </summary>
    /// <param name="key">Developer key. Its public key determines the real extension id.</param>
    /// <param name="archive">The ZIP payload.</param>
    /// <param name="declaredCrsId">Overrides the crx_id, to model an id substitution.</param>
    /// <param name="signArchiveOnly">
    /// Signs just the archive, the way most reimplementations wrongly do. A verifier that
    /// accepts this is broken.
    /// </param>
    public static byte[] Build(
        RSA key,
        byte[] archive,
        byte[]? declaredCrsId = null,
        bool signArchiveOnly = false)
    {
        var publicKeyInfo = key.ExportSubjectPublicKeyInfo();
        var crxId = declaredCrsId ?? SHA256.HashData(publicKeyInfo).AsSpan(0, 16).ToArray();
        var signedHeaderData = LengthDelimited(1, crxId);

        var payload = signArchiveOnly
            ? archive
            : Concat(SignatureContext.ToArray(), LittleEndian((uint)signedHeaderData.Length), signedHeaderData, archive);

        var signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var proof = Concat(LengthDelimited(1, publicKeyInfo), LengthDelimited(2, signature));
        var header = Concat(LengthDelimited(2, proof), LengthDelimited(10000, signedHeaderData));

        return Concat(
            Magic.ToArray(),
            LittleEndian(3),
            LittleEndian((uint)header.Length),
            header,
            archive);
    }

    /// <summary>The extension id a given key produces, via CrxFile's own derivation.</summary>
    public static string IdFor(RSA key) => CrxFile.DeriveExtensionId(key.ExportSubjectPublicKeyInfo());

    public static RSA CreateKey() => RSA.Create(2048);

    private static byte[] LengthDelimited(int fieldNumber, byte[] payload)
    {
        var result = new byte[VarintLength((ulong)((fieldNumber << 3) | 2)) + VarintLength((ulong)payload.Length) + payload.Length];
        var offset = 0;

        WriteVarint(result, ref offset, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(result, ref offset, (ulong)payload.Length);
        payload.CopyTo(result, offset);

        return result;
    }

    private static void WriteVarint(byte[] buffer, ref int offset, ulong value)
    {
        while (true)
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            buffer[offset++] = (byte)(value != 0 ? b | 0x80 : b);
            if (value == 0)
            {
                return;
            }
        }
    }

    private static int VarintLength(ulong value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    private static byte[] LittleEndian(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;

        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}