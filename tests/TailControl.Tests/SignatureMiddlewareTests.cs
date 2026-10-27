using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Security;

namespace TailControl.Tests;

/// <summary>Teste end-to-end prin pipeline-ul ASP.NET Core (TestServer).</summary>
public sealed class SignatureMiddlewareTests : IAsyncLifetime
{
    private static readonly Uri BaseAddress = new("http://pc.example.ts.net:8444/");

    private readonly FakeTimeProvider _clock = TestSupport.NewClock();
    private readonly ClientIdentity _phone = ClientIdentity.Create();
    private readonly ClientIdentity _stranger = ClientIdentity.Create();
    private readonly string _clientsPath = TestSupport.NewTempPath("clients.json");
    private AuthorizedClientList _clients = AuthorizedClientList.Empty;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_clientsPath)!);
        _clients = AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _);
        AuthorizedClientsFile.Save(_clientsPath, _clients);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddTailControlSignatures(o =>
        {
            o.AuthorizedClientsPath = _clientsPath;
            o.MaxBodyBytes = 1024;
            o.FailuresBeforeBlock = 3;
        });

        _app = builder.Build();
        _app.UseTailControlSignatures();
        _app.MapGet("/v1/whoami", (HttpContext ctx) => ctx.GetAuthenticatedClient().Label);
        _app.MapPost("/v1/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            return await reader.ReadToEndAsync();
        });

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _phone.Dispose();
        _stranger.Dispose();
        TestSupport.DeleteQuietly(_clientsPath);
    }

    [Fact]
    public async Task SignedGet_IsAccepted_AndExposesClient()
    {
        using var client = CreateClient(_phone);

        var response = await client.GetAsync(new Uri("v1/whoami?x=1&y=a%20b", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Telefon", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedPost_BodyStillReadableByEndpoint()
    {
        using var client = CreateClient(_phone);

        var response = await client.PostAsync(new Uri("v1/echo", UriKind.Relative), new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"a\":1}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnsignedRequest_IsRejected_WithoutDetails()
    {
        using var client = new HttpClient(_app.GetTestServer().CreateHandler()) { BaseAddress = BaseAddress };

        var response = await client.GetAsync(new Uri("v1/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnauthorizedClient_IsRejected()
    {
        using var client = CreateClient(_stranger);

        var response = await client.GetAsync(new Uri("v1/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplayedRequest_IsRejected()
    {
        HttpRequestMessage? captured = null;
        using (var client = CreateClient(_phone, r => captured = Clone(r)))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);
        }

        using var raw = new HttpClient(_app.GetTestServer().CreateHandler());
        var replay = await raw.SendAsync(captured!);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task BodyChangedAfterSigning_IsRejected()
    {
        using var client = CreateClient(_phone, r => r.Content = new StringContent("{\"a\":2}", Encoding.UTF8, "application/json"));

        var response = await client.PostAsync(new Uri("v1/echo", UriKind.Relative), new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequestSignedForAnotherHost_IsRejected()
    {
        // Cerere semnata pentru laptop, trimisa catre PC.
        using var client = CreateClient(_phone, r => r.Headers.Host = "pc.example.ts.net:8444");
        client.BaseAddress = new Uri("http://laptop.example.ts.net:8444/");

        var response = await client.GetAsync(new Uri("v1/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DuplicatedAuthHeader_IsRejected()
    {
        using var client = CreateClient(_phone, r => r.Headers.TryAddWithoutValidation(SignatureHeaders.ClientId, "0123456789abcdef0123456789abcdef"));

        var response = await client.GetAsync(new Uri("v1/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_IsRejected()
    {
        using var client = CreateClient(_phone);

        var response = await client.PostAsync(new Uri("v1/echo", UriKind.Relative), new StringContent(new string('x', 2048)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RevokedOnDisk_IsRejectedWithoutRestart()
    {
        using var client = CreateClient(_phone);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);

        AuthorizedClientsFile.Save(_clientsPath, _clients.Revoke(_phone.ClientId, TestSupport.Start));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task RepeatedFailures_BlockAddress_EvenForValidRequests()
    {
        using var stranger = CreateClient(_stranger);
        using var phone = CreateClient(_phone);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await phone.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync(new Uri("v1/whoami", UriKind.Relative))).StatusCode);
    }

    private HttpClient CreateClient(ClientIdentity identity, Action<HttpRequestMessage>? afterSigning = null)
    {
        HttpMessageHandler inner = _app.GetTestServer().CreateHandler();
        if (afterSigning is not null)
        {
            inner = new TamperHandler(afterSigning) { InnerHandler = inner };
        }

        var signing = new SigningHttpHandler(new RequestSigner(identity, _clock)) { InnerHandler = inner };
        return new HttpClient(signing) { BaseAddress = BaseAddress };
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
