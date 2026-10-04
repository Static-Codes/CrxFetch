using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CrxFetch;

/// <summary>The CRX container version.</summary>
public enum CrxFormat
{
    /// <summary>Legacy CRX2 container. No longer accepted by Chromium.</summary>
    Crx2 = 2,

    /// <summary>CRX3 container, the only format the Chrome Web Store serves.</summary>
    Crx3 = 3,
}

/// <summary>What parsing a CRX package revealed about it.</summary>
/// <param name="Format">Container version.</param>
/// <param name="ExtensionId">The id the package is signed for.</param>
/// <param name="SignatureVerified">True when the developer-key proof verifies.</param>
/// <param name="SignatureDetail">Human readable breakdown of the key proofs.</param>
/// <param name="ZipOffset">Byte offset where the ZIP archive starts.</param>
/// <param name="ZipLength">Length in bytes of the ZIP archive.</param>
public sealed record CrxInfo(
    CrxFormat Format,
    string ExtensionId,
    bool SignatureVerified,
    string SignatureDetail,
    int ZipOffset,
    int ZipLength);

/// <summary>
/// Parses the CRX container and verifies its signatures the way Chromium's
/// components/crx_file does.
///
/// CRX3 signed payload, per crx3.proto and crx_verifier.cc:
///   "CRX3 SignedData\0" | uint32le(len(signed_header_data)) | signed_header_data | archive
/// The archive alone is not what is signed, which is the detail most reimplementations get
/// wrong.
///
/// CRX2 is parsed only far enough to locate the archive. Chromium no longer accepts CRX2 at
/// all, so there is no signature check to make.
/// </summary>
public static class CrxFile
{
    private const int MaxHeaderSize = 1 << 18;

    private static ReadOnlySpan<byte> Magic => "Cr24"u8;

    // "CRX3 SignedData" followed by a NUL octet.
    private static ReadOnlySpan<byte> SignatureContext => "CRX3 SignedData\0"u8;

    /// <summary>
    /// Parses a CRX package and verifies its key proofs.
    /// </summary>
    /// <param name="crx">Raw CRX bytes.</param>
    /// <returns>What the package declares about itself.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed CRX, or the signed header is inconsistent.</exception>
    public static CrxInfo Inspect(ReadOnlySpan<byte> crx)
    {
        if (crx.Length < 16 || !crx[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                $"Not a CRX file: expected magic 'Cr24', got {Describe(crx[..Math.Min(4, crx.Length)])}.");
        }

        return ReadUInt32(crx[4..]) switch
        {
            3 => InspectCrx3(crx),
            2 => InspectCrx2(crx),
            var v => throw new InvalidDataException($"Unsupported CRX version {v}."),
        };
    }

    private static CrxInfo InspectCrx3(ReadOnlySpan<byte> crx)
    {
        var headerLength = (int)ReadUInt32(crx[8..]);
        if (headerLength <= 0 || headerLength > MaxHeaderSize || 12 + headerLength > crx.Length)
        {
            throw new InvalidDataException("CRX3 header length is out of range.");
        }

        var header = crx[12..(12 + headerLength)];
        var archive = crx[(12 + headerLength)..];

        byte[]? signedHeaderData = null;
        var proofs = new List<(int Field, byte[] PublicKey, byte[] Signature)>();

        foreach (var (field, _, data) in ReadFields(header))
        {
            switch (field)
            {
                case 2 or 3:
                    proofs.Add(ParseProof(data, field));
                    break;
                case 10000:
                    signedHeaderData = data;
                    break;
            }
        }

        if (signedHeaderData is null)
        {
            throw new InvalidDataException("CRX3 header carries no signed_header_data.");
        }

        if (proofs.Count == 0)
        {
            throw new InvalidDataException("CRX3 header carries no key proofs.");
        }

        var declaredId = ReadDeclaredId(signedHeaderData);
        var signedPayload = BuildSignedPayload(signedHeaderData, archive);

        var verified = 0;
        var unsupported = 0;
        byte[]? developerKey = null;
        var developerVerified = false;

        foreach (var (field, publicKey, signature) in proofs)
        {
            var derived = DeriveExtensionId(publicKey);
            var result = VerifyProof(field, publicKey, signature, signedPayload);
            switch (result)
            {
                case ProofResult.Valid:
                    verified++;
                    break;
                case ProofResult.Unsupported:
                    unsupported++;
                    break;
            }

            if (derived == declaredId)
            {
                developerKey = publicKey;
                developerVerified = result == ProofResult.Valid;
            }
        }

        // Chromium refuses a CRX3 without a proof matching the declared crx_id.
        if (developerKey is null)
        {
            throw new InvalidDataException(
                $"CRX3 declares id {declaredId} but no proof carries the matching public key.");
        }

        var detail = developerVerified
            ? $"developer key ok, {verified - 1}/{proofs.Count - 1} other proofs valid" +
              (unsupported > 0 ? $", {unsupported} unsupported" : string.Empty)
            : "DEVELOPER KEY SIGNATURE INVALID";

        return new CrxInfo(CrxFormat.Crx3, declaredId, developerVerified, detail, 12 + headerLength, archive.Length);
    }

    private static CrxInfo InspectCrx2(ReadOnlySpan<byte> crx)
    {
        var publicKeyLength = (int)ReadUInt32(crx[8..]);
        if (publicKeyLength <= 0 || 16 + publicKeyLength > crx.Length)
        {
            throw new InvalidDataException("CRX2 public key length is out of range.");
        }

        // Two header orders exist in the wild. The common one is
        //   magic | version | publen | key | siglen | sig
        // but some producers emit siglen ahead of the key:
        //   magic | version | publen | siglen | key | sig
        // Both put the archive at the same offset, so only the key offset differs.
        var leadingLength = (int)ReadUInt32(crx[12..]);
        var sigLengthFirst = leadingLength is 256 or 512 or 1024;

        var publicKeyOffset = sigLengthFirst ? 16 : 12;
        var signatureLengthOffset = sigLengthFirst ? 12 : 12 + publicKeyLength;
        var publicKey = crx[publicKeyOffset..(publicKeyOffset + publicKeyLength)];

        if (signatureLengthOffset + 4 > crx.Length)
        {
            throw new InvalidDataException("CRX2 header is truncated.");
        }

        var signatureLength = (int)ReadUInt32(crx[signatureLengthOffset..]);
        var zipOffset = 16 + publicKeyLength + signatureLength;

        if (zipOffset < 0 || zipOffset > crx.Length)
        {
            throw new InvalidDataException("CRX2 archive offset is out of range.");
        }

        return new CrxInfo(
            CrxFormat.Crx2,
            DeriveExtensionId(publicKey),
            SignatureVerified: false,
            "not checked (Chromium no longer accepts CRX2)",
            zipOffset,
            crx.Length - zipOffset);
    }

    /// <summary>
    /// Derives the extension id from a DER-encoded SubjectPublicKeyInfo, the same way
    /// Chromium's <c>id_util::GenerateId</c> does: the first 16 bytes of its SHA-256, with
    /// each nibble mapped into the <c>a</c>-<c>p</c> alphabet.
    /// </summary>
    /// <param name="subjectPublicKeyInfo">DER SubjectPublicKeyInfo bytes.</param>
    /// <returns>The 32-character extension id.</returns>
    public static string DeriveExtensionId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        FormatCrsId(SHA256.HashData(subjectPublicKeyInfo).AsSpan(0, 16));

    private static byte[] BuildSignedPayload(byte[] signedHeaderData, ReadOnlySpan<byte> archive)
    {
        var payload = new byte[SignatureContext.Length + 4 + signedHeaderData.Length + archive.Length];
        var offset = 0;

        SignatureContext.CopyTo(payload.AsSpan(offset));
        offset += SignatureContext.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(offset, 4), (uint)signedHeaderData.Length);
        offset += 4;

        signedHeaderData.CopyTo(payload.AsSpan(offset));
        offset += signedHeaderData.Length;

        archive.CopyTo(payload.AsSpan(offset));
        return payload;
    }

    private enum ProofResult
    {
        Valid,
        Invalid,
        Unsupported,
    }

    private static ProofResult VerifyProof(int field, byte[] publicKey, byte[] signature, byte[] payload)
    {
        try
        {
            if (field == 2)
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
                if (bytesRead != publicKey.Length)
                {
                    return ProofResult.Unsupported;
                }

                return rsa.VerifyData(
                    payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    ? ProofResult.Valid
                    : ProofResult.Invalid;
            }

            // Google publisher proofs are ECDSA P-256 and are not needed to establish the
            // developer key, so an unparseable one is skipped rather than fatal.
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var ecBytesRead);
            if (ecBytesRead != publicKey.Length)
            {
                return ProofResult.Unsupported;
            }

            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256)
                ? ProofResult.Valid
                : ProofResult.Invalid;
        }
        catch (CryptographicException)
        {
            return ProofResult.Unsupported;
        }
    }

    private static (int Field, byte[] PublicKey, byte[] Signature) ParseProof(byte[] data, int field)
    {
        byte[]? publicKey = null;
        byte[]? signature = null;

        foreach (var (innerField, _, innerData) in ReadFields(data))
        {
            if (innerField == 1)
            {
                publicKey = innerData;
            }
            else if (innerField == 2)
            {
                signature = innerData;
            }
        }

        return (field, publicKey ?? [], signature ?? []);
    }

    private static string ReadDeclaredId(byte[] signedHeaderData)
    {
        foreach (var (field, _, data) in ReadFields(signedHeaderData))
        {
            if (field == 1 && data.Length == 16)
            {
                return FormatCrsId(data);
            }
        }

        throw new InvalidDataException("CRX3 signed header does not carry a 16-byte crx_id.");
    }

    private static List<(int Field, int Wire, byte[] Data)> ReadFields(ReadOnlySpan<byte> buf)
    {
        var fields = new List<(int, int, byte[])>();
        var pos = 0;

        while (pos < buf.Length)
        {
            if (!TryReadVarint(buf, ref pos, out var tag))
            {
                break;
            }

            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);

            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(buf, ref pos, out _))
                    {
                        return fields;
                    }

                    break;

                case 1:
                    pos += 8;
                    break;

                case 5:
                    pos += 4;
                    break;

                case 2:
                    if (!TryReadVarint(buf, ref pos, out var length))
                    {
                        return fields;
                    }

                    var end = pos + (int)length;
                    if (end > buf.Length || end < pos)
                    {
                        return fields;
                    }

                    fields.Add((field, wire, buf[pos..end].ToArray()));
                    pos = end;
                    break;

                default:
                    return fields;
            }
        }

        return fields;
    }

    private static string FormatCrsId(ReadOnlySpan<byte> crsId)
    {
        Span<char> chars = crsId.Length <= 32 ? stackalloc char[crsId.Length * 2] : new char[crsId.Length * 2];
        for (var i = 0; i < crsId.Length; i++)
        {
            chars[i * 2] = (char)('a' + (crsId[i] >> 4));
            chars[(i * 2) + 1] = (char)('a' + (crsId[i] & 0x0F));
        }

        return new string(chars);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b) =>
        (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

    private static bool TryReadVarint(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (pos < buf.Length && shift <= 63)
        {
            var b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static string Describe(ReadOnlySpan<byte> b) =>
        b.Length == 0 ? "<empty>" : Convert.ToHexString(b);
}