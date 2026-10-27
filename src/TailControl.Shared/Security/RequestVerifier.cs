namespace TailControl.Shared.Security;

public enum VerificationFailure
{
    None = 0,
    MissingHeaders,
    MalformedHeaders,
    UnknownClient,
    RevokedClient,
    TimestampOutOfRange,
    SignedBeforeStartup,
    BadSignature,
    ReplayedNonce,
}

public readonly record struct VerificationResult(VerificationFailure Failure, AuthorizedClient? Client)
{
    public bool IsSuccess => Failure == VerificationFailure.None;

    public static VerificationResult Fail(VerificationFailure failure) => new(failure, null);
}

/// <summary>O cerere primita de server, cu valorile brute ale headerelor.</summary>
public sealed record SignedRequest(
    string Method,
    string Host,
    string PathAndQuery,
    string? ClientId,
    string? Timestamp,
    string? Nonce,
    string? Signature,
    byte[] BodySha256);

/// <summary>
/// Verifica o cerere semnata. Ordinea conteaza: nonce-ul se inregistreaza doar dupa
/// ce semnatura e valida, ca cineva fara cheie sa nu poata umple cache-ul.
/// </summary>
public sealed class RequestVerifier(
    IAuthorizedClientStore clients,
    INonceCache nonces,
    TimeProvider timeProvider,
    DateTimeOffset? notBefore = null)
{
    public VerificationResult Verify(SignedRequest request)
    {
        if (request.ClientId is null || request.Timestamp is null || request.Nonce is null || request.Signature is null)
        {
            return VerificationResult.Fail(VerificationFailure.MissingHeaders);
        }

        if (!SignatureProtocol.IsValidClientId(request.ClientId)
            || !SignatureProtocol.TryParseTimestamp(request.Timestamp, out var timestamp)
            || !SignatureProtocol.IsValidNonce(request.Nonce)
            || !SignatureProtocol.TryDecodeSignature(request.Signature, out var signature)
            || request.BodySha256.Length != 32
            || HasSeparator(request.Method) || HasSeparator(request.Host) || HasSeparator(request.PathAndQuery))
        {
            return VerificationResult.Fail(VerificationFailure.MalformedHeaders);
        }

        var entry = clients.Find(request.ClientId);
        if (entry is null)
        {
            return VerificationResult.Fail(VerificationFailure.UnknownClient);
        }

        if (entry.Client.IsRevoked)
        {
            return VerificationResult.Fail(VerificationFailure.RevokedClient);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var maxSkew = (long)SignatureProtocol.MaxClockSkew.TotalSeconds;
        if (Math.Abs(now - timestamp) > maxSkew)
        {
            return VerificationResult.Fail(VerificationFailure.TimestampOutOfRange);
        }

        if (notBefore is { } start && timestamp < start.ToUnixTimeSeconds())
        {
            return VerificationResult.Fail(VerificationFailure.SignedBeforeStartup);
        }

        var canonical = SignatureProtocol.BuildCanonicalBytes(
            request.Method, request.Host, request.PathAndQuery, timestamp, request.Nonce, request.BodySha256);

        if (!ClientKeys.VerifySignature(entry.SubjectPublicKeyInfo, canonical, signature))
        {
            return VerificationResult.Fail(VerificationFailure.BadSignature);
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(timestamp) + SignatureProtocol.MaxClockSkew + TimeSpan.FromSeconds(1);
        if (!nonces.TryAdd(request.ClientId, request.Nonce, expiresAt))
        {
            return VerificationResult.Fail(VerificationFailure.ReplayedNonce);
        }

        return new VerificationResult(VerificationFailure.None, entry.Client);
    }

    private static bool HasSeparator(string value) => value.Contains('\n', StringComparison.Ordinal);
}
