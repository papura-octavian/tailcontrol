using System.Security.Cryptography;
using TailControl.Shared.Security;

namespace TailControl.Tests;

public sealed class ClientKeysTests
{
    [Fact]
    public void ClientIdentity_ExportImport_KeepsSameIdentity()
    {
        using var original = ClientIdentity.Create();
        using var restored = ClientIdentity.FromPrivateKey(original.ExportPrivateKey());

        Assert.Equal(original.ClientId, restored.ClientId);
        Assert.Equal(original.PublicKey, restored.PublicKey);
    }

    [Fact]
    public void ClientId_IsDerivedFromPublicKey()
    {
        using var identity = ClientIdentity.Create();

        Assert.True(SignatureProtocol.IsValidClientId(identity.ClientId));
        Assert.True(ClientKeys.TryParsePublicKey(identity.PublicKey, out _, out var id));
        Assert.Equal(identity.ClientId, id);
    }

    [Fact]
    public void TwoIdentities_HaveDifferentIds()
    {
        using var a = ClientIdentity.Create();
        using var b = ClientIdentity.Create();

        Assert.NotEqual(a.ClientId, b.ClientId);
    }

    [Fact]
    public void Signature_VerifiesOnlyForSameData()
    {
        using var identity = ClientIdentity.Create();
        Assert.True(ClientKeys.TryParsePublicKey(identity.PublicKey, out var spki, out _));
        var signature = identity.Sign("abc"u8.ToArray());

        Assert.Equal(SignatureProtocol.SignatureBytes, signature.Length);
        Assert.True(ClientKeys.VerifySignature(spki, "abc"u8.ToArray(), signature));
        Assert.False(ClientKeys.VerifySignature(spki, "abd"u8.ToArray(), signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nu e base64")]
    [InlineData("AAAA")]
    public void TryParsePublicKey_Garbage_IsRejected(string? value)
    {
        Assert.False(ClientKeys.TryParsePublicKey(value, out _, out _));
    }

    [Fact]
    public void TryParsePublicKey_OtherCurveOrAlgorithm_IsRejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        Assert.False(ClientKeys.TryParsePublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), out _, out _));
        Assert.False(ClientKeys.TryParsePublicKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), out _, out _));
    }

    [Fact]
    public void TryParsePublicKey_TrailingBytes_IsRejected()
    {
        using var identity = ClientIdentity.Create();
        var withExtra = Convert.FromBase64String(identity.PublicKey).Concat(new byte[] { 0 }).ToArray();

        Assert.False(ClientKeys.TryParsePublicKey(Convert.ToBase64String(withExtra), out _, out _));
    }

    [Fact]
    public void FromPrivateKey_OtherCurve_IsRejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<CryptographicException>(() =>
            ClientIdentity.FromPrivateKey(Convert.ToBase64String(p384.ExportPkcs8PrivateKey())));
    }
}
