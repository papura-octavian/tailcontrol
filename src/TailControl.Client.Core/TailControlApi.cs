using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;

namespace TailControl.Client.Core;

public enum ApiErrorKind
{
    NotConfigured,
    Network,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Validation,
    RateLimited,
    Server,
}

public sealed record ApiError(ApiErrorKind Kind, string Message, IReadOnlyDictionary<string, string[]>? FieldErrors = null);

public sealed record ApiResult<T>(T? Value, ApiError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class ApiResult
{
    public static ApiResult<T> Ok<T>(T value) => new(value, null);

    public static ApiResult<T> Fail<T>(ApiError error) => new(default, error);
}

/// <summary>Raspuns fara continut (ex. DELETE).</summary>
public readonly record struct NoContent;

/// <summary>
/// Apelurile catre Hub (inventar, wake) si direct catre agenti (comenzi, status).
/// Fiecare cerere e semnata cu cheia acestui client. Erorile devin mesaje in romana.
/// </summary>
public sealed class TailControlApi(
    ClientIdentityProvider identities,
    IClientSettingsStore settings,
    Func<HttpMessageHandler> createInnerHandler,
    TimeProvider timeProvider) : IDisposable
{
    private const string HubName = "Hub-ul";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private HttpClient? _http;
    private string? _httpClientId;

    public Task<ApiResult<List<DeviceDto>>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Get, HubRoutes.Devices, null, ClientJsonContext.Default.ListDeviceDto, cancellationToken);

    public Task<ApiResult<DeviceDto>> GetDeviceAsync(int id, CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Get, HubRoutes.ForDevice(id), null, ClientJsonContext.Default.DeviceDto, cancellationToken);

    public Task<ApiResult<DeviceDto>> CreateDeviceAsync(DeviceRequest request, CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Post, HubRoutes.Devices, JsonContent.Create(request, ClientJsonContext.Default.DeviceRequest), ClientJsonContext.Default.DeviceDto, cancellationToken);

    public Task<ApiResult<DeviceDto>> UpdateDeviceAsync(int id, DeviceRequest request, CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Put, HubRoutes.ForDevice(id), JsonContent.Create(request, ClientJsonContext.Default.DeviceRequest), ClientJsonContext.Default.DeviceDto, cancellationToken);

    public Task<ApiResult<NoContent>> DeleteDeviceAsync(int id, CancellationToken cancellationToken = default) =>
        SendToHubAsync<NoContent>(HttpMethod.Delete, HubRoutes.ForDevice(id), null, null, cancellationToken);

    public Task<ApiResult<WakeResponse>> WakeAsync(int id, CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Post, HubRoutes.ForWake(id), null, ClientJsonContext.Default.WakeResponse, cancellationToken);

    public Task<ApiResult<List<AuditEntryDto>>> GetAuditAsync(int take = 50, CancellationToken cancellationToken = default) =>
        SendToHubAsync(HttpMethod.Get, $"{HubRoutes.Audit}?take={take}", null, ClientJsonContext.Default.ListAuditEntryDto, cancellationToken);

    public Task<ApiResult<AgentStatusResponse>> GetAgentStatusAsync(DeviceDto device, CancellationToken cancellationToken = default) =>
        SendAsync(AgentUri(device, AgentRoutes.Status), HttpMethod.Get, null, ClientJsonContext.Default.AgentStatusResponse, device.Name, cancellationToken);

    /// <summary>
    /// Trimite o comanda din lista fixa direct agentului. Raspunsurile 409/500 ale agentului au
    /// tot forma <see cref="CommandResponse"/>, cu mesajul pentru utilizator.
    /// </summary>
    public async Task<ApiResult<CommandResponse>> SendCommandAsync(DeviceDto device, AgentCommand command, CancellationToken cancellationToken = default)
    {
        var uri = AgentUri(device, AgentRoutes.ForCommand(command));
        return await SendAsync(uri, HttpMethod.Post, null, ClientJsonContext.Default.CommandResponse, device.Name, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _clientLock.Dispose();
    }

    private Uri AgentUri(DeviceDto device, string path)
    {
        var scheme = settings.Load().AgentsUseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        return new Uri(new UriBuilder(scheme, device.Host, device.AgentPort).Uri, path);
    }

    private Task<ApiResult<T>> SendToHubAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T>? responseType, CancellationToken cancellationToken)
    {
        var hub = settings.Load().HubUrl;
        return hub is null
            ? Task.FromResult(ApiResult.Fail<T>(new ApiError(ApiErrorKind.NotConfigured, "Configureaza adresa Hub-ului in Setari.")))
            : SendAsync(new Uri(hub, path), method, content, responseType, HubName, cancellationToken);
    }

    private async Task<ApiResult<T>> SendAsync<T>(Uri uri, HttpMethod method, HttpContent? content, JsonTypeInfo<T>? responseType, string target, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            var http = await GetHttpClientAsync().ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return ApiResult.Fail<T>(new ApiError(ApiErrorKind.Network,
                $"Conexiunea securizata cu {target} a esuat (certificat). Daca nu folosesti HTTPS acolo, schimba setarea din aplicatie."));
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(new ApiError(ApiErrorKind.Network,
                $"Nu pot contacta {target}. Este pornit si conectat la Tailscale?"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<T>(new ApiError(ApiErrorKind.Network, $"{target} nu a raspuns la timp."));
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (responseType is null)
                {
                    return ApiResult.Ok<T>(default!);
                }

                var value = await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false);
                return value is null
                    ? ApiResult.Fail<T>(new ApiError(ApiErrorKind.Server, $"Raspuns gol de la {target}."))
                    : ApiResult.Ok<T>(value);
            }

            return ApiResult.Fail<T>(await ToErrorAsync(response, target, cancellationToken).ConfigureAwait(false));
        }
    }

    internal static async Task<ApiError> ToErrorAsync(HttpResponseMessage response, string target, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (title, errors) = ParseBody(body);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new(ApiErrorKind.Unauthorized,
                $"{target} a refuzat cererea: acest dispozitiv nu este autorizat acolo, a fost revocat, sau ceasul e decalat cu peste 30 s."),
            HttpStatusCode.Forbidden => new(ApiErrorKind.Forbidden, title ?? "Doar clientii admin pot face aceasta operatie."),
            HttpStatusCode.NotFound => new(ApiErrorKind.NotFound, title ?? "Nu a fost gasit (poate a fost sters)."),
            HttpStatusCode.Conflict => new(ApiErrorKind.Conflict, title ?? "Operatia nu se poate face acum."),
            HttpStatusCode.BadRequest when errors is not null => new(ApiErrorKind.Validation, "Verifica datele introduse.", errors),
            HttpStatusCode.BadRequest => new(ApiErrorKind.Validation, "Cererea nu este valida."),
            HttpStatusCode.TooManyRequests => new(ApiErrorKind.RateLimited,
                $"{target} a blocat temporar cererile (prea multe cereri sau esecuri recente). Incearca peste cateva minute."),
            _ => new(ApiErrorKind.Server, title ?? $"Eroare pe {target} ({(int)response.StatusCode})."),
        };
    }

    // Hub-ul trimite ProblemDetails (title/errors); agentul trimite CommandResponse (message).
    private static (string? Title, Dictionary<string, string[]>? Errors) ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return (message.GetString(), null);
            }

            var problem = doc.RootElement.Deserialize(ClientJsonContext.Default.ProblemResponse);
            return (problem?.Title, problem?.Errors);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task<HttpClient> GetHttpClientAsync()
    {
        var identity = await identities.GetAsync().ConfigureAwait(false);
        if (_http is not null && _httpClientId == identity.ClientId)
        {
            return _http;
        }

        await _clientLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_http is null || _httpClientId != identity.ClientId)
            {
                var signing = new SigningHttpHandler(new RequestSigner(identity, timeProvider)) { InnerHandler = createInnerHandler() };
                var old = _http;
                _http = new HttpClient(signing) { Timeout = Timeout };
                _httpClientId = identity.ClientId;
                old?.Dispose();
            }

            return _http;
        }
        finally
        {
            _clientLock.Release();
        }
    }
}
