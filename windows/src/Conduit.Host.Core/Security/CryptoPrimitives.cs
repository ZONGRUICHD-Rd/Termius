using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Conduit.Host.Core.Security;

/// <summary>
/// P-256 helpers. Public keys travel as uncompressed X9.63 points (65 bytes, 0x04 || X || Y),
/// which is what CryptoKit's <c>x963Representation</c> produces on iOS.
/// </summary>
public static class P256
{
    public static ECDiffieHellman NewKeyAgreementKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static ECDsa NewSigningKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] ExportX963(ECParameters p)
    {
        var raw = new byte[65];
        raw[0] = 0x04;
        p.Q.X!.CopyTo(raw, 1);
        p.Q.Y!.CopyTo(raw, 33);
        return raw;
    }

    public static byte[] ExportX963(ECDiffieHellman key) => ExportX963(key.ExportParameters(false));

    public static byte[] ExportX963(ECDsa key) => ExportX963(key.ExportParameters(false));

    public static ECParameters ParseX963(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 65 || raw[0] != 0x04)
            throw new CryptographicException("Expected a 65 byte uncompressed P-256 point.");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw[1..33].ToArray(), Y = raw[33..65].ToArray() },
        };
    }

    /// <summary>Imports a private key from its raw 32 byte scalar (CryptoKit <c>rawRepresentation</c>).</summary>
    public static ECDiffieHellman ImportPrivateRaw(ReadOnlySpan<byte> d)
    {
        var tmp = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d.ToArray() });
        // Re-import with Q populated so that ExportParameters(false) works on every platform.
        var full = tmp.ExportParameters(true);
        tmp.Dispose();
        return ECDiffieHellman.Create(full);
    }

    /// <summary>Raw ECDH: the 32 byte X coordinate of the shared point (same as CryptoKit SharedSecret bytes).</summary>
    public static byte[] Agree(ECDiffieHellman privateKey, ReadOnlySpan<byte> peerX963)
    {
        using var peer = ECDiffieHellman.Create(ParseX963(peerX963));
        return privateKey.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public static bool VerifySignature(ReadOnlySpan<byte> publicX963, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureP1363)
    {
        try
        {
            using var key = ECDsa.Create(ParseX963(publicX963));
            return key.VerifyData(data, signatureP1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

public static class B64
{
    public static string Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromUrl(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
        }
        return Convert.FromBase64String(t);
    }

    public static string Std(ReadOnlySpan<byte> data) => Convert.ToBase64String(data);

    public static byte[] FromStd(string s) => Convert.FromBase64String(s);
}

public static class Ids
{
    /// <summary>Stable short identifier for a public key: base64url(SHA-256(pub))[0..22].</summary>
    public static string FromPublicKey(ReadOnlySpan<byte> publicX963) => B64.Url(SHA256.HashData(publicX963))[..22];

    public static string NewId(int bytes = 12) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string NewToken() => B64.Url(RandomNumberGenerator.GetBytes(18));
}

/// <summary>
/// Key schedule shared with the iOS client (see docs/PROTOCOL.md §2).
///
///   ee = ECDH(clientEph, hostEph)        forward secrecy
///   es = ECDH(clientEph, hostStatic)     authenticates the host
///   ss = ECDH(clientStatic, hostStatic)  authenticates the paired device
///   okm = HKDF-SHA256(ikm = ee||es||ss, salt = nonceC||nonceH,
///                     info = "conduit/v1" || clientEph || hostEph, L = 64)
///   c2h = okm[0..32], h2c = okm[32..64]
/// </summary>
public static class KeySchedule
{
    public static readonly byte[] InfoPrefix = Encoding.ASCII.GetBytes("conduit/v1");

    public static (byte[] C2H, byte[] H2C) Derive(
        ReadOnlySpan<byte> ee, ReadOnlySpan<byte> es, ReadOnlySpan<byte> ss,
        ReadOnlySpan<byte> nonceClient, ReadOnlySpan<byte> nonceHost,
        ReadOnlySpan<byte> clientEph, ReadOnlySpan<byte> hostEph)
    {
        byte[] ikm = [.. ee, .. es, .. ss];
        byte[] salt = [.. nonceClient, .. nonceHost];
        byte[] info = [.. InfoPrefix, .. clientEph, .. hostEph];
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, salt, info);
        CryptographicOperations.ZeroMemory(ikm);
        return (okm[..32], okm[32..]);
    }
}

/// <summary>
/// AES-256-GCM framing. Each binary frame is counter(8, big endian) || ciphertext || tag(16).
/// Nonce = 0x00000000 || counter. Counters start at 0 and must arrive strictly in order.
/// </summary>
public sealed class FrameCipher : IDisposable
{
    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private readonly object _sendLock = new();

    public FrameCipher(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, 16);
        _recv = new AesGcm(recvKey, 16);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        lock (_sendLock)
        {
            var frame = new byte[8 + plaintext.Length + 16];
            BinaryPrimitives.WriteUInt64BigEndian(frame, _sendCounter);
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sendCounter);
            _send.Encrypt(nonce, plaintext, frame.AsSpan(8, plaintext.Length), frame.AsSpan(8 + plaintext.Length, 16));
            _sendCounter++;
            return frame;
        }
    }

    public byte[] Open(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 24) throw new CryptographicException("Frame too short.");
        var counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
        if (counter != _recvCounter) throw new CryptographicException($"Unexpected frame counter {counter}, expected {_recvCounter}.");
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        var ctLen = frame.Length - 24;
        var plain = new byte[ctLen];
        _recv.Decrypt(nonce, frame.Slice(8, ctLen), frame.Slice(8 + ctLen, 16), plain);
        _recvCounter++;
        return plain;
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
    }
}
