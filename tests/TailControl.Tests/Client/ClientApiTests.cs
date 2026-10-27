using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TailControl.Agent;
using TailControl.Agent.Power;
using TailControl.Client.Core;
using TailControl.Hub;
using TailControl.Hub.Status;
using TailControl.Hub.Wake;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;
using TailControl.Tests.Agent;

namespace TailControl.Tests.Client;

/// <summary>
/// Clientul real (TailControlApi) contra Hub-ului si agentului reale, in memorie.
/// Cererile trec prin semnare, verificare, rutare si serializare exact ca in productie.
/// </summary>
public sealed class ClientApiTests : IAsyncLifetime
{
    private const string HubHost = "pi.example.ts.net";
    private const string AgentHost = "pc.example.ts.net";

    private readonly FakeTimeProvider _clock = TestSupport.NewClock();
    private readonly FakePowerActions _power = new();
    private readonly InMemoryKeyStore _keys = new();
    private readonly string _directory = TestSupport.NewTempPath("client");
    private ClientIdentityProvider _identities = null!;
    private InMemoryClientSettingsStore _settings = null!;
    private WebApplication _hub = null!;
    private WebApplication _agent = null!;
    private TailControlApi _api = null!;

    public async Task InitializeAsync()
    {
        _identities = new ClientIdentityProvider(_keys);
        var me = await _identities.GetAsync();

        var hubOptions = new HubOptions
        {
            ConfigDirectory = Path.Combine(_directory, "hub-config"),
            DatabasePath = Path.Combine(_directory, "hub.db"),
            WakeBroadcastAddress = "192.168.1.255",
        };
        var agentOptions = new AgentOptions
        {
            DataDirectory = Path.Combine(_directory, "agent"),
            LockPipeName = $"TailControl.Tests.{Guid.NewGuid():N}",
        };

        var authorized = AuthorizedClientList.Empty.Authorize(me.PublicKey, "Telefon", isAdmin: true, TestSupport.Start, out _);
        Directory.CreateDirectory(Path.GetDirectoryName(hubOptions.AuthorizedClientsPath)!);
        Directory.CreateDirectory(agentOptions.ConfigDirectory);
        AuthorizedClientsFile.Save(hubOptions.AuthorizedClientsPath, authorized);
        AuthorizedClientsFile.Save(agentOptions.AuthorizedClientsPath, authorized);

        _hub = Build(s =>
        {
            s.AddHubServices(hubOptions);
            s.Replace(ServiceDescriptor.Singleton<IWakeOnLanSender>(new NoopWake()));
            s.Replace(ServiceDescriptor.Singleton<IDeviceProbe>(new AlwaysOnlineProbe()));
        });
        await _hub.InitializeDatabaseAsync();
        _hub.UseHubPipeline();
        await _hub.StartAsync();

        _agent = Build(s =>
        {
            s.AddAgentServices(agentOptions);
            s.Replace(ServiceDescriptor.Singleton<IPowerActions>(_power));
        });
        _agent.UseAgentPipeline();
        await _agent.StartAsync();

        _settings = new InMemoryClientSettingsStore(new ClientSettings { HubUrl = new Uri($"https://{HubHost}:8443/") });
        var routes = new Dictionary<string, HttpMessageHandler>(StringComparer.OrdinalIgnoreCase)
        {
            [HubHost] = _hub.GetTestServer().CreateHandler(),
            [AgentHost] = _agent.GetTestServer().CreateHandler(),
        };
        _api = new TailControlApi(_identities, _settings, () => new RoutingHandler(routes), _clock);
    }

    public async Task DisposeAsync()
    {
        _api.Dispose();
        await _hub.DisposeAsync();
        await _agent.DisposeAsync();
        _identities.Dispose();
        SqliteConnection.ClearAllPools();
        TestSupport.DeleteQuietly(_directory);
    }

    [Fact]
    public async Task FullFlow_InventoryWakeAndAgentCommands()
    {
        var created = await _api.CreateDeviceAsync(new DeviceRequest("PC", "CC:28:AA:00:00:01", AgentHost, null, DeviceKind.Desktop));
        Assert.True(created.IsSuccess, created.Error?.Message);

        await _hub.Services.GetRequiredService<StatusMonitor>().CheckAllAsync(CancellationToken.None);
        var devices = await _api.GetDevicesAsync();
        var pc = Assert.Single(devices.Value!);
        Assert.True(pc.Online);

        Assert.True((await _api.WakeAsync(pc.Id)).IsSuccess);

        var shutdown = await _api.SendCommandAsync(pc, AgentCommand.Shutdown);
        Assert.Equal(CommandStatus.Scheduled, shutdown.Value!.Status);

        var status = await _api.GetAgentStatusAsync(pc);
        Assert.Equal(TestSupport.Start.AddSeconds(10), status.Value!.ShutdownScheduledForUtc);
        Assert.True(DeviceActions.Evaluate(pc, status.Value, _clock.GetUtcNow()).CanCancelShutdown);

        Assert.Equal(CommandStatus.Executed, (await _api.SendCommandAsync(pc, AgentCommand.CancelShutdown)).Value!.Status);
        Assert.Equal(["shutdown:10", "cancel"], _power.Calls);
    }

    [Fact]
    public async Task NotConfigured_ReturnsFriendlyError()
    {
        _settings.Save(new ClientSettings());

        var result = await _api.GetDevicesAsync();

        Assert.Equal(ApiErrorKind.NotConfigured, result.Error!.Kind);
    }

    [Fact]
    public async Task ValidationErrors_AreReturnedPerField()
    {
        var result = await _api.CreateDeviceAsync(new DeviceRequest("PC", "nu-e-mac", "192.168.1.10", null, DeviceKind.Desktop));

        Assert.Equal(ApiErrorKind.Validation, result.Error!.Kind);
        Assert.Contains("host", result.Error.FieldErrors!.Keys);
        Assert.Contains("macAddress", result.Error.FieldErrors!.Keys);
    }

    [Fact]
    public async Task AgentConflict_ShowsAgentMessage()
    {
        _power.LockResult = new PowerResult(PowerOutcome.NoUserSession);
        var pc = (await _api.CreateDeviceAsync(new DeviceRequest("PC", null, AgentHost, null, DeviceKind.Desktop))).Value!;

        var result = await _api.SendCommandAsync(pc, AgentCommand.Lock);

        Assert.Equal(ApiErrorKind.Conflict, result.Error!.Kind);
        Assert.Contains("Niciun utilizator logat", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownHost_IsNetworkError()
    {
        var ghost = new DeviceDto(9, "Fantoma", null, "ghost.example.ts.net", 8444, DeviceKind.Other, true, null, null, TestSupport.Start, TestSupport.Start);

        var result = await _api.SendCommandAsync(ghost, AgentCommand.Lock);

        Assert.Equal(ApiErrorKind.Network, result.Error!.Kind);
        Assert.Contains("Fantoma", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegeneratedKey_IsNoLongerAuthorized()
    {
        Assert.True((await _api.GetDevicesAsync()).IsSuccess);

        await _identities.RegenerateAsync();
        var result = await _api.GetDevicesAsync();

        Assert.Equal(ApiErrorKind.Unauthorized, result.Error!.Kind);
        Assert.Contains("nu este autorizat", result.Error.Message, StringComparison.Ordinal);
    }

    private WebApplication Build(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        configure(builder.Services);
        return builder.Build();
    }

    /// <summary>Trimite cererea catre serverul in memorie potrivit hostului; altfel, eroare de retea.</summary>
    private sealed class RoutingHandler(Dictionary<string, HttpMessageHandler> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            routes.TryGetValue(request.RequestUri!.Host, out var handler)
                ? new HttpMessageInvoker(handler, disposeHandler: false).SendAsync(request, cancellationToken)
                : throw new HttpRequestException($"Host necunoscut: {request.RequestUri.Host}");
    }

    private sealed class NoopWake : IWakeOnLanSender
    {
        public Task SendAsync(byte[] mac, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysOnlineProbe : IDeviceProbe
    {
        public Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
