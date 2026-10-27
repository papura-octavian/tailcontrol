using System.Security.Cryptography;

namespace TailControl.Shared.Security;

/// <summary>
/// Perechea de chei ECDSA P-256 a unui client. Cheia privata nu paraseste clientul:
/// se exporta doar ca sa fie pusa in SecureStorage pe acelasi dispozitiv.
/// </summary>
public sealed class ClientIdentity : IDisposable
{
    private readonly ECDsa _key;

    private ClientIdentity(ECDsa key)
    {
        _key = key;
        var spki = key.ExportSubjectPublicKeyInfo();
        PublicKey = Convert.ToBase64String(spki);
        ClientId = ClientKeys.ComputeClientId(spki);
    }

    /// <summary>Cheia publica SPKI, base64. Se afiseaza pentru pairing.</summary>
    public string PublicKey { get; }

    public string ClientId { get; }

    public static ClientIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static ClientIdentity FromPrivateKey(string pkcs8Base64)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
            if (!ClientKeys.TryParsePublicKey(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), out _, out _))
            {
                throw new CryptographicException("Cheia trebuie sa fie ECDSA P-256.");
            }

            return new ClientIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Cheia privata PKCS#8, base64. Doar pentru stocare locala securizata.</summary>
    public string ExportPrivateKey() => Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
