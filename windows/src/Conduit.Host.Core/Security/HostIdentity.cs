using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Host.Core.Security;

/// <summary>Protects secrets at rest. The WinUI host plugs in DPAPI; the default is a no-op.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class NoopProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>
/// Long-lived host keys:
///  * a P-256 key agreement key, used in the E2E handshake with phones;
///  * a P-256 signing key, used to prove ownership of the host id to the relay.
/// The host id is derived from the signing key so the relay can verify it without any registry.
/// </summary>
public sealed class HostIdentity : IDisposable
{
    public ECDiffieHellman KeyAgreement { get; }
    public ECDsa Signing { get; }
    public byte[] KeyAgreementPublic { get; }
    public byte[] SigningPublic { get; }
    public string HostId { get; }

    private HostIdentity(ECDiffieHellman kx, ECDsa sig)
    {
        KeyAgreement = kx;
        Signing = sig;
        KeyAgreementPublic = P256.ExportX963(kx);
        SigningPublic = P256.ExportX963(sig);
        HostId = Ids.FromPublicKey(SigningPublic);
    }

    public static HostIdentity Create() => new(P256.NewKeyAgreementKey(), P256.NewSigningKey());

    public static HostIdentity LoadOrCreate(string path, ISecretProtector protector)
    {
        if (File.Exists(path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize(File.ReadAllText(path), IdentityJson.Default.StoredIdentity)!;
                var kx = ECDiffieHellman.Create();
                kx.ImportPkcs8PrivateKey(protector.Unprotect(B64.FromStd(stored.KeyAgreement)), out _);
                var sig = ECDsa.Create();
                sig.ImportPkcs8PrivateKey(protector.Unprotect(B64.FromStd(stored.Signing)), out _);
                return new HostIdentity(kx, sig);
            }
            catch (Exception)
            {
                // Corrupt or unreadable (e.g. DPAPI blob from another Windows account): keep a backup and regenerate.
                File.Copy(path, path + ".bak", overwrite: true);
            }
        }

        var id = Create();
        var json = new StoredIdentity
        {
            KeyAgreement = B64.Std(protector.Protect(id.KeyAgreement.ExportPkcs8PrivateKey())),
            Signing = B64.Std(protector.Protect(id.Signing.ExportPkcs8PrivateKey())),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(json, IdentityJson.Default.StoredIdentity));
        return id;
    }

    /// <summary>Relay auth: signature over "conduit-relay-v1|{hostId}|{unixSeconds}".</summary>
    public string SignRelayChallenge(long unixSeconds)
    {
        var data = Encoding.UTF8.GetBytes($"conduit-relay-v1|{HostId}|{unixSeconds}");
        return B64.Url(Signing.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose()
    {
        KeyAgreement.Dispose();
        Signing.Dispose();
    }
}

public sealed class StoredIdentity
{
    public string KeyAgreement { get; set; } = "";
    public string Signing { get; set; } = "";
}

[JsonSerializable(typeof(StoredIdentity))]
internal partial class IdentityJson : JsonSerializerContext;
