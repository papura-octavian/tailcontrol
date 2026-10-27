using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TailControl.Shared.Security;

namespace TailControl.Shared.AspNetCore;

/// <summary>Clientul autentificat al cererii curente.</summary>
public sealed record AuthenticatedClientFeature(AuthorizedClient Client);

/// <summary>
/// Verifica semnatura fiecarei cereri. Nu exista endpoint-uri fara autentificare:
/// verificarea de status a Hub-ului e un simplu TCP connect, nu o cerere HTTP.
/// Toate esecurile intorc acelasi raspuns (401, fara corp); motivul apare doar in log.
/// </summary>
public sealed partial class SignatureVerificationMiddleware(
    RequestDelegate next,
    RequestVerifier verifier,
    FailureBackoff backoff,
    SignatureOptions options,
    ILogger<SignatureVerificationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (backoff.IsBlocked(remote))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var bodyHash = await TryHashBodyAsync(context.Request, options.MaxBodyBytes, context.RequestAborted);
        if (bodyHash is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var request = context.Request;
        var result = verifier.Verify(new SignedRequest(
            request.Method,
            request.Host.Value ?? "",
            GetRawTarget(context),
            SingleHeader(request, SignatureHeaders.ClientId),
            SingleHeader(request, SignatureHeaders.Timestamp),
            SingleHeader(request, SignatureHeaders.Nonce),
            SingleHeader(request, SignatureHeaders.Signature),
            bodyHash));

        if (!result.IsSuccess)
        {
            backoff.RecordFailure(remote);
            var clientId = SingleHeader(request, SignatureHeaders.ClientId);
            LogRejected(logger, result.Failure, SignatureProtocol.IsValidClientId(clientId) ? clientId : "-", remote?.ToString() ?? "-", request.Method, request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Features.Set(new AuthenticatedClientFeature(result.Client!));
        await next(context);
    }

    // Path-ul exact trimis de client (inainte de decodare), ca sa corespunda cu ce a semnat.
    private static string GetRawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return string.IsNullOrEmpty(raw) ? context.Request.GetEncodedPathAndQuery() : raw;
    }

    // Un header repetat e tratat ca lipsa, ca sa nu existe ambiguitate.
    private static string? SingleHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;

    private static async Task<byte[]?> TryHashBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        request.EnableBuffering(bufferThreshold: maxBytes, bufferLimit: maxBytes + 1L);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return hash.GetHashAndReset();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cerere respinsa ({Failure}) client={ClientId} ip={RemoteIp} {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, VerificationFailure failure, string? clientId, string remoteIp, string method, PathString path);
}

public static class SignatureVerificationExtensions
{
    /// <summary>Inregistreaza verificarea semnaturilor. Un <see cref="TimeProvider"/> inregistrat anterior are prioritate.</summary>
    public static IServiceCollection AddTailControlSignatures(this IServiceCollection services, Action<SignatureOptions> configure)
    {
        var options = new SignatureOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.AuthorizedClientsPath))
        {
            throw new InvalidOperationException("AuthorizedClientsPath trebuie configurat.");
        }

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthorizedClientStore>(new AuthorizedClientsFile(options.AuthorizedClientsPath));
        services.AddSingleton<INonceCache>(sp => new MemoryNonceCache(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<FailureBackoff>();
        services.AddSingleton(sp =>
        {
            var time = sp.GetRequiredService<TimeProvider>();
            return new RequestVerifier(
                sp.GetRequiredService<IAuthorizedClientStore>(),
                sp.GetRequiredService<INonceCache>(),
                time,
                options.RejectSignedBeforeStartup ? time.GetUtcNow() : null);
        });

        return services;
    }

    /// <summary>
    /// Adauga middleware-ul. Verificatorul e creat aici, la construirea pipeline-ului,
    /// ca momentul pornirii (<see cref="SignatureOptions.RejectSignedBeforeStartup"/>)
    /// sa fie fixat inainte de prima cerere.
    /// </summary>
    public static IApplicationBuilder UseTailControlSignatures(this IApplicationBuilder app)
    {
        _ = app.ApplicationServices.GetRequiredService<RequestVerifier>();
        return app.UseMiddleware<SignatureVerificationMiddleware>();
    }

    /// <summary>Clientul autentificat; arunca exceptie daca middleware-ul nu a rulat.</summary>
    public static AuthorizedClient GetAuthenticatedClient(this HttpContext context) =>
        context.Features.Get<AuthenticatedClientFeature>()?.Client
            ?? throw new InvalidOperationException("Cererea nu a trecut prin verificarea semnaturii.");
}
