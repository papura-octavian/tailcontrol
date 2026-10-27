using System.Globalization;
using System.Text;

namespace TailControl.Shared.Security;

/// <summary>Numele headerelor de autentificare trimise de client.</summary>
public static class SignatureHeaders
{
    public const string ClientId = "X-Client-Id";
    public const string Timestamp = "X-Timestamp";
    public const string Nonce = "X-Nonce";
    public const string Signature = "X-Signature";
}

/// <summary>
/// Protocolul de semnare a cererilor (versiunea TC1).
/// <para>
/// Sirul semnat (UTF-8, separat prin <c>\n</c>):
/// <c>TC1, METODA, host[:port], path?query, timestamp, nonce, hex(SHA-256(body))</c>.
/// Hostul este inclus ca o cerere semnata pentru un dispozitiv sa nu poata fi
/// retrimisa catre alt dispozitiv (fiecare server are propriul cache de nonce).
/// </para>
/// <para>Semnatura: ECDSA P-256 cu SHA-256, format IEEE P1363 (r||s, 64 octeti), base64.</para>
/// </summary>
public static class SignatureProtocol
{
    public const string Version = "TC1";

    /// <summary>Deviatia maxima acceptata intre ceasul clientului si al serverului.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Nonce: 16 octeti aleatori, base64url fara padding (22 de caractere).</summary>
    public const int NonceBytes = 16;
    public const int NonceLength = 22;

    /// <summary>ClientId: primii 16 octeti din SHA-256(cheie publica SPKI), hex cu litere mici.</summary>
    public const int ClientIdLength = 32;

    public const int SignatureBytes = 64;

    /// <summary>Timestamp: secunde Unix, doar cifre.</summary>
    public const int MaxTimestampLength = 12;

    public static string BuildCanonicalString(
        string method,
        string host,
        string pathAndQuery,
        long timestamp,
        string nonce,
        ReadOnlySpan<byte> bodySha256)
    {
        RejectSeparator(method, nameof(method));
        RejectSeparator(host, nameof(host));
        RejectSeparator(pathAndQuery, nameof(pathAndQuery));
        RejectSeparator(nonce, nameof(nonce));

        return string.Join('\n',
            Version,
            method.ToUpperInvariant(),
            host.ToLowerInvariant(),
            pathAndQuery,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            Convert.ToHexStringLower(bodySha256));
    }

    public static byte[] BuildCanonicalBytes(
        string method,
        string host,
        string pathAndQuery,
        long timestamp,
        string nonce,
        ReadOnlySpan<byte> bodySha256) =>
        Encoding.UTF8.GetBytes(BuildCanonicalString(method, host, pathAndQuery, timestamp, nonce, bodySha256));

    public static bool IsValidClientId(string? value) =>
        value is { Length: ClientIdLength } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    public static bool IsValidNonce(string? value) =>
        value is { Length: NonceLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool TryParseTimestamp(string? value, out long timestamp)
    {
        timestamp = 0;
        return value is { Length: > 0 and <= MaxTimestampLength }
            && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
    }

    public static bool TryDecodeSignature(string? value, out byte[] signature)
    {
        signature = [];
        if (value is null || value.Length > 128)
        {
            return false;
        }

        var buffer = new byte[96];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written != SignatureBytes)
        {
            return false;
        }

        signature = buffer[..SignatureBytes];
        return true;
    }

    // Separatorul nu poate aparea in campuri, altfel doua cereri diferite ar
    // putea produce acelasi sir semnat.
    private static void RejectSeparator(string value, string name)
    {
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Campul nu poate contine caracterul newline.", name);
        }
    }
}
