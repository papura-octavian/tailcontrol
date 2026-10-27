using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace TailControl.Shared.Security;

public readonly record struct SignedHeaders(string ClientId, string Timestamp, string Nonce, string Signature);

/// <summary>Produce headerele de autentificare pentru o cerere.</summary>
public sealed class RequestSigner(ClientIdentity identity, TimeProvider timeProvider)
{
    public RequestSigner(ClientIdentity identity)
        : this(identity, TimeProvider.System)
    {
    }

    public SignedHeaders Sign(string method, string host, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SignatureProtocol.NonceBytes));
        var canonical = SignatureProtocol.BuildCanonicalBytes(
            method, host, pathAndQuery, timestamp, nonce, SHA256.HashData(body));

        return new SignedHeaders(
            identity.ClientId,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            Convert.ToBase64String(identity.Sign(canonical)));
    }
}

/// <summary>
/// Handler pentru <see cref="HttpClient"/> care semneaza fiecare cerere inainte de trimitere.
/// </summary>
public sealed class SigningHttpHandler(RequestSigner signer) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("Cererea nu are URI absolut.");

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        // Uri.Authority omite portul implicit, la fel ca headerul Host trimis de HttpClient.
        var host = request.Headers.Host ?? uri.Authority;
        var headers = signer.Sign(request.Method.Method, host, uri.PathAndQuery, body);

        request.Headers.Remove(SignatureHeaders.ClientId);
        request.Headers.Remove(SignatureHeaders.Timestamp);
        request.Headers.Remove(SignatureHeaders.Nonce);
        request.Headers.Remove(SignatureHeaders.Signature);
        request.Headers.Add(SignatureHeaders.ClientId, headers.ClientId);
        request.Headers.Add(SignatureHeaders.Timestamp, headers.Timestamp);
        request.Headers.Add(SignatureHeaders.Nonce, headers.Nonce);
        request.Headers.Add(SignatureHeaders.Signature, headers.Signature);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
