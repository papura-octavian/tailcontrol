using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace TailControl.Shared.Security;

/// <summary>Operatii pe cheile publice ale clientilor (format SPKI, base64).</summary>
public static class ClientKeys
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static string ComputeClientId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo)[..(SignatureProtocol.ClientIdLength / 2)]);

    /// <summary>
    /// Valideaza o cheie publica: base64 corect, SPKI complet (fara octeti in plus)
    /// si curba P-256. Intoarce octetii SPKI si ClientId-ul derivat.
    /// </summary>
    public static bool TryParsePublicKey(
        string? publicKeyBase64,
        [NotNullWhen(true)] out byte[]? subjectPublicKeyInfo,
        [NotNullWhen(true)] out string? clientId)
    {
        subjectPublicKeyInfo = null;
        clientId = null;

        if (string.IsNullOrWhiteSpace(publicKeyBase64) || publicKeyBase64.Length > 512)
        {
            return false;
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid?.Value != P256Oid)
            {
                return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }

        subjectPublicKeyInfo = spki;
        clientId = ComputeClientId(spki);
        return true;
    }

    public static bool VerifySignature(ReadOnlySpan<byte> subjectPublicKeyInfo, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
