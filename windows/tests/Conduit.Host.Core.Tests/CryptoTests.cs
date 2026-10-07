using System.Security.Cryptography;
using System.Text;
using Conduit.Host.Core.Security;
using Xunit;

namespace Conduit.Host.Core.Tests;

public class CryptoTests
{
    private static byte[] Seq(byte start, int count = 32) => Enumerable.Range(0, count).Select(i => (byte)(start + i)).ToArray();

    /// <summary>
    /// Cross-platform test vector. The same inputs and expected outputs live in
    /// ios/ConduitKit/Tests/ConduitKitTests/CryptoVectorTests.swift — if you change the key schedule, update both.
    /// </summary>
    internal static (byte[] C2H, byte[] H2C, byte[] Frame) ComputeVector()
    {
        using var clientStatic = P256.ImportPrivateRaw(Seq(0x01));
        using var clientEph = P256.ImportPrivateRaw(Seq(0x21));
        using var hostStatic = P256.ImportPrivateRaw(Seq(0x41));
        using var hostEph = P256.ImportPrivateRaw(Seq(0x61));
        var nonceC = Seq(0xA0, 16);
        var nonceH = Seq(0xB0, 16);
        var cEph = P256.ExportX963(clientEph);
        var hEph = P256.ExportX963(hostEph);

        var ee = P256.Agree(clientEph, hEph);
        var es = P256.Agree(clientEph, P256.ExportX963(hostStatic));
        var ss = P256.Agree(clientStatic, P256.ExportX963(hostStatic));
        var (c2h, h2c) = KeySchedule.Derive(ee, es, ss, nonceC, nonceH, cEph, hEph);
        using var cipher = new FrameCipher(c2h, h2c);
        var frame = cipher.Seal(Encoding.UTF8.GetBytes("{\"t\":\"req\",\"id\":1,\"m\":\"ping\"}"));
        return (c2h, h2c, frame);
    }

    public const string VectorC2H = "3f51d364288afb9d4d6b7a37f3ccb04ed619c1fe4ed9a7234d2209ecc59b1ad4";
    public const string VectorH2C = "5acb2e6085e9810c12be0b27444985756a5558f87e941cf44b459be0338fa20c";
    public const string VectorFrame = "00000000000000002910cbbb7c6a058a31e9432db6b46e5b32642f5b2439ac1c2c812ce00aa94c894b8e09e74cf10d86fe34d4c3a2";

    [Fact]
    public void KeyScheduleMatchesSharedVector()
    {
        var (c2h, h2c, frame) = ComputeVector();
        Assert.Equal(VectorC2H, Convert.ToHexString(c2h).ToLowerInvariant());
        Assert.Equal(VectorH2C, Convert.ToHexString(h2c).ToLowerInvariant());
        Assert.Equal(VectorFrame, Convert.ToHexString(frame).ToLowerInvariant());
    }

    [Fact]
    public void BothSidesDeriveTheSameKeys()
    {
        using var clientStatic = P256.NewKeyAgreementKey();
        using var clientEph = P256.NewKeyAgreementKey();
        using var hostStatic = P256.NewKeyAgreementKey();
        using var hostEph = P256.NewKeyAgreementKey();
        var nc = RandomNumberGenerator.GetBytes(16);
        var nh = RandomNumberGenerator.GetBytes(16);
        var ce = P256.ExportX963(clientEph);
        var he = P256.ExportX963(hostEph);

        var client = KeySchedule.Derive(P256.Agree(clientEph, he), P256.Agree(clientEph, P256.ExportX963(hostStatic)),
            P256.Agree(clientStatic, P256.ExportX963(hostStatic)), nc, nh, ce, he);
        var host = KeySchedule.Derive(P256.Agree(hostEph, ce), P256.Agree(hostStatic, ce),
            P256.Agree(hostStatic, P256.ExportX963(clientStatic)), nc, nh, ce, he);

        Assert.Equal(client.C2H, host.C2H);
        Assert.Equal(client.H2C, host.H2C);
        Assert.NotEqual(client.C2H, client.H2C);
    }

    [Fact]
    public void FramesRoundTripAndRejectReplayOrTampering()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        using var a = new FrameCipher(k1, k2);
        using var b = new FrameCipher(k2, k1);

        var f0 = a.Seal("hello"u8);
        var f1 = a.Seal("world"u8);
        Assert.Equal("hello", Encoding.UTF8.GetString(b.Open(f0)));
        Assert.Throws<CryptographicException>(() => b.Open(f0)); // replay
        var tampered = (byte[])f1.Clone();
        tampered[10] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.Open(tampered));
    }

    [Fact]
    public void RelaySignatureVerifies()
    {
        using var id = HostIdentity.Create();
        var ts = 1_700_000_000L;
        var sig = B64.FromUrl(id.SignRelayChallenge(ts));
        Assert.True(P256.VerifySignature(id.SigningPublic, Encoding.UTF8.GetBytes($"conduit-relay-v1|{id.HostId}|{ts}"), sig));
        Assert.False(P256.VerifySignature(id.SigningPublic, Encoding.UTF8.GetBytes($"conduit-relay-v1|{id.HostId}|{ts + 1}"), sig));
        Assert.Equal(id.HostId, Ids.FromPublicKey(id.SigningPublic));
    }
}
