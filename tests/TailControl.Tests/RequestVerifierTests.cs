using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using TailControl.Shared.Security;

namespace TailControl.Tests;

public sealed class RequestVerifierTests : IDisposable
{
    private const string Host = "pc.example.ts.net:8444";
    private const string Path = "/v1/commands/shutdown";

    private readonly FakeTimeProvider _clock = TestSupport.NewClock();
    private readonly ClientIdentity _phone = ClientIdentity.Create();
    private readonly ClientIdentity _stranger = ClientIdentity.Create();
    private readonly InMemoryClientStore _store;
    private readonly RequestVerifier _verifier;

    public RequestVerifierTests()
    {
        _store = new InMemoryClientStore(
            AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", isAdmin: true, TestSupport.Start, out _));
        _verifier = new RequestVerifier(_store, new MemoryNonceCache(_clock), _clock, notBefore: TestSupport.Start);
    }

    public void Dispose()
    {
        _phone.Dispose();
        _stranger.Dispose();
    }

    [Fact]
    public void Verify_ValidRequest_IsAccepted()
    {
        var result = _verifier.Verify(Sign(_phone));

        Assert.True(result.IsSuccess);
        Assert.Equal("Telefon", result.Client!.Label);
        Assert.True(result.Client.IsAdmin);
    }

    [Fact]
    public void Verify_SignatureFromAnotherKey_IsRejected()
    {
        // Un atacator foloseste id-ul telefonului, dar semneaza cu propria cheie.
        var forged = Sign(_stranger) with { ClientId = _phone.ClientId };

        Assert.Equal(VerificationFailure.BadSignature, _verifier.Verify(forged).Failure);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("host")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("body")]
    [InlineData("timestamp")]
    [InlineData("nonce")]
    public void Verify_FieldChangedAfterSigning_IsRejected(string field)
    {
        var request = Sign(_phone, body: "{\"x\":1}");
        var tampered = field switch
        {
            "method" => request with { Method = "GET" },
            "host" => request with { Host = "laptop.example.ts.net:8444" },
            "path" => request with { PathAndQuery = "/v1/commands/sleep" },
            "query" => request with { PathAndQuery = Path + "?x=1" },
            "body" => request with { BodySha256 = SHA256.HashData("{\"x\":2}"u8) },
            "timestamp" => request with { Timestamp = (long.Parse(request.Timestamp!, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) },
            "nonce" => request with { Nonce = NewNonce() },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.Equal(VerificationFailure.BadSignature, _verifier.Verify(tampered).Failure);
    }

    [Fact]
    public void Verify_SignatureIsCaseInsensitiveForMethodAndHost()
    {
        var request = Sign(_phone) with { Method = "post", Host = Host.ToUpperInvariant() };

        Assert.True(_verifier.Verify(request).IsSuccess);
    }

    [Fact]
    public void Verify_ReusedNonce_IsRejected()
    {
        var request = Sign(_phone);

        Assert.True(_verifier.Verify(request).IsSuccess);
        Assert.Equal(VerificationFailure.ReplayedNonce, _verifier.Verify(request).Failure);
    }

    [Fact]
    public void Verify_ReplayAfterWindow_IsRejectedByTimestamp()
    {
        var request = Sign(_phone);
        Assert.True(_verifier.Verify(request).IsSuccess);

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(VerificationFailure.TimestampOutOfRange, _verifier.Verify(request).Failure);
    }

    [Fact]
    public void Verify_InvalidSignature_DoesNotConsumeNonce()
    {
        var request = Sign(_phone);

        Assert.False(_verifier.Verify(request with { Host = "alt.example:1" }).IsSuccess);
        Assert.True(_verifier.Verify(request).IsSuccess);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Verify_OldTimestamp_IsAcceptedOnlyWithinSkew(int ageSeconds, bool accepted)
    {
        var request = Sign(_phone);
        _clock.Advance(TimeSpan.FromSeconds(ageSeconds));

        var result = _verifier.Verify(request);

        Assert.Equal(accepted, result.IsSuccess);
        if (!accepted)
        {
            Assert.Equal(VerificationFailure.TimestampOutOfRange, result.Failure);
        }
    }

    [Fact]
    public void Verify_TimestampTooFarInFuture_IsRejected()
    {
        var future = new FakeTimeProvider(TestSupport.Start.AddSeconds(31));
        var request = Sign(_phone, clock: future);

        Assert.Equal(VerificationFailure.TimestampOutOfRange, _verifier.Verify(request).Failure);
    }

    [Fact]
    public void Verify_RequestSignedBeforeStartup_IsRejected()
    {
        // Cache-ul de nonce e gol dupa repornire; o cerere semnata inainte nu trebuie acceptata.
        var beforeStart = new FakeTimeProvider(TestSupport.Start.AddSeconds(-5));
        var request = Sign(_phone, clock: beforeStart);

        Assert.Equal(VerificationFailure.SignedBeforeStartup, _verifier.Verify(request).Failure);
    }

    [Fact]
    public void Verify_UnknownClient_IsRejected()
    {
        Assert.Equal(VerificationFailure.UnknownClient, _verifier.Verify(Sign(_stranger)).Failure);
    }

    [Fact]
    public void Verify_RevokedClient_IsRejected()
    {
        var request = Sign(_phone);
        _store.List = _store.List.Revoke(_phone.ClientId, TestSupport.Start);

        Assert.Equal(VerificationFailure.RevokedClient, _verifier.Verify(request).Failure);
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("timestamp")]
    [InlineData("nonce")]
    [InlineData("signature")]
    public void Verify_MissingHeader_IsRejected(string header)
    {
        var request = Sign(_phone);
        var missing = header switch
        {
            "clientId" => request with { ClientId = null },
            "timestamp" => request with { Timestamp = null },
            "nonce" => request with { Nonce = null },
            _ => request with { Signature = null },
        };

        Assert.Equal(VerificationFailure.MissingHeaders, _verifier.Verify(missing).Failure);
    }

    [Theory]
    [InlineData("clientId", "ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("clientId", "abc")]
    [InlineData("timestamp", "-1782561600")]
    [InlineData("timestamp", "1782561600.0")]
    [InlineData("timestamp", " 1782561600")]
    [InlineData("timestamp", "9999999999999")]
    [InlineData("nonce", "short")]
    [InlineData("nonce", "AAAAAAAAAAAAAAAAAAAA+/")]
    [InlineData("signature", "not-base64!")]
    [InlineData("signature", "AAAA")]
    public void Verify_MalformedHeader_IsRejected(string header, string value)
    {
        var request = Sign(_phone);
        var malformed = header switch
        {
            "clientId" => request with { ClientId = value },
            "timestamp" => request with { Timestamp = value },
            "nonce" => request with { Nonce = value },
            _ => request with { Signature = value },
        };

        Assert.Equal(VerificationFailure.MalformedHeaders, _verifier.Verify(malformed).Failure);
    }

    [Fact]
    public void Verify_NewlineInSignedField_IsRejected()
    {
        var request = Sign(_phone) with { PathAndQuery = Path + "\nTC1" };

        Assert.Equal(VerificationFailure.MalformedHeaders, _verifier.Verify(request).Failure);
    }

    [Fact]
    public void BuildCanonicalString_FieldWithNewline_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SignatureProtocol.BuildCanonicalString("POST", Host, "/a\n/b", 1, NewNonce(), new byte[32]));
    }

    [Fact]
    public void BuildCanonicalString_HasExpectedLayout()
    {
        var canonical = SignatureProtocol.BuildCanonicalString(
            "post", "PC.Example:8444", "/v1/x?a=1", 1782561600, "AAAAAAAAAAAAAAAAAAAAAA", SHA256.HashData([]));

        Assert.Equal(
            "TC1\nPOST\npc.example:8444\n/v1/x?a=1\n1782561600\nAAAAAAAAAAAAAAAAAAAAAA\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            canonical);
    }

    private SignedRequest Sign(ClientIdentity identity, string body = "", FakeTimeProvider? clock = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new RequestSigner(identity, clock ?? _clock).Sign("POST", Host, Path, bytes);
        return new SignedRequest(
            "POST", Host, Path, headers.ClientId, headers.Timestamp, headers.Nonce, headers.Signature, SHA256.HashData(bytes));
    }

    private static string NewNonce() =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SignatureProtocol.NonceBytes));
}
