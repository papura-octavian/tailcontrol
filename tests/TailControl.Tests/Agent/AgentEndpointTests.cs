using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TailControl.Agent;
using TailControl.Agent.Power;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;

namespace TailControl.Tests.Agent;

/// <summary>Agentul complet (fara Kestrel), cu actiunile de putere inlocuite de un fals.</summary>
public sealed class AgentEndpointTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestSupport.NewClock();
    private readonly FakePowerActions _power = new();
    private readonly ClientIdentity _phone = ClientIdentity.Create();
    private readonly string _dataDirectory = TestSupport.NewTempPath("agent");
    private AgentOptions _options = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync() => StartAsync(dryRun: false);

    public async Task DisposeAsync()
    {
        await StopAppAsync();
        _phone.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            TestSupport.DeleteQuietly(_dataDirectory);
        }
    }

    [Fact]
    public async Task Shutdown_Signed_SchedulesWithConfiguredDelay_AndAudits()
    {
        var response = await PostCommandAsync("shutdown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert.Equal(CommandStatus.Scheduled, body!.Status);
        Assert.Equal(TestSupport.Start.AddSeconds(10), body.ScheduledForUtc);
        Assert.Equal(["shutdown:10"], _power.Calls);

        var entry = Assert.Single(_app.Services.GetRequiredService<AuditLog>().ReadAll());
        Assert.Equal(_phone.ClientId, entry.ClientId);
        Assert.Equal("Telefon", entry.ClientLabel);
        Assert.Equal("shutdown", entry.Command);
        Assert.Equal("Scheduled", entry.Result);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("hibernate")]
    [InlineData("1")]
    [InlineData("Shutdown")]
    [InlineData("shutdown;calc.exe")]
    [InlineData("cmd.exe%20%2Fc%20whoami")]
    [InlineData("powershell%20-c%20Stop-Computer")]
    [InlineData("..%2Fshutdown")]
    public async Task CommandOutsideFixedList_Returns404_AndExecutesNothing(string name)
    {
        var response = await PostCommandAsync(name);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_power.Calls);
        Assert.Empty(_app.Services.GetRequiredService<AuditLog>().ReadAll());
    }

    [Fact]
    public async Task BodyText_IsIgnored_OnlyFixedCommandRuns()
    {
        var response = await _client.PostAsync(
            new Uri("v1/commands/lock", UriKind.Relative),
            new StringContent("cmd.exe /c del /q C:\\*", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["lock"], _power.Calls);
    }

    [Fact]
    public async Task UnsignedCommand_IsRejected_AndExecutesNothing()
    {
        using var raw = new HttpClient(_app.GetTestServer().CreateHandler()) { BaseAddress = _client.BaseAddress };

        var response = await raw.PostAsync(new Uri("v1/commands/shutdown", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task GetOnCommandRoute_IsNotAllowed()
    {
        var response = await _client.GetAsync(new Uri("v1/commands/shutdown", UriKind.Relative));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task Lock_WithoutUserSession_Returns409()
    {
        _power.LockResult = new PowerResult(PowerOutcome.NoUserSession);

        var response = await PostCommandAsync("lock");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CommandStatus.Unavailable, (await response.Content.ReadFromJsonAsync<CommandResponse>())!.Status);
    }

    [Fact]
    public async Task Shutdown_Failure_Returns500_WithoutInternalDetails()
    {
        _power.ShutdownResult = PowerResult.Fail("shutdown.exe a intors codul 5.");

        var response = await PostCommandAsync("shutdown");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("shutdown.exe", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelShutdown_ClearsScheduledShutdown()
    {
        await PostCommandAsync("shutdown");
        Assert.NotNull((await GetStatusAsync()).ShutdownScheduledForUtc);

        var response = await PostCommandAsync("cancel-shutdown");

        Assert.Equal(CommandStatus.Executed, (await response.Content.ReadFromJsonAsync<CommandResponse>())!.Status);
        Assert.Null((await GetStatusAsync()).ShutdownScheduledForUtc);
    }

    [Fact]
    public async Task CancelShutdown_WhenNothingScheduled_ReturnsNothingToDo()
    {
        _power.CancelResult = new PowerResult(PowerOutcome.NothingToCancel);

        var response = await PostCommandAsync("cancel-shutdown");

        Assert.Equal(CommandStatus.NothingToDo, (await response.Content.ReadFromJsonAsync<CommandResponse>())!.Status);
    }

    [Fact]
    public async Task Sleep_RespondsFirst_ThenSleepsAfterDelay()
    {
        var response = await PostCommandAsync("sleep");

        Assert.Equal(CommandStatus.Scheduled, (await response.Content.ReadFromJsonAsync<CommandResponse>())!.Status);
        Assert.False(_power.SleepCalled.Task.IsCompleted);

        _clock.Advance(TimeSpan.FromSeconds(3));

        await _power.SleepCalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Status_ReportsAgentState()
    {
        var status = await GetStatusAsync();

        Assert.False(status.DryRun);
        Assert.Equal(Environment.MachineName, status.MachineName);
        Assert.Equal(0, status.LockHelperSessions);
    }

    [Fact]
    public async Task DryRun_LogsOnly_AndNeverTouchesPowerActions()
    {
        await StopAppAsync();
        await StartAsync(dryRun: true);

        foreach (var command in new[] { "shutdown", "lock", "sleep", "cancel-shutdown" })
        {
            var response = await PostCommandAsync(command);
            Assert.Equal(CommandStatus.DryRun, (await response.Content.ReadFromJsonAsync<CommandResponse>())!.Status);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(_power.Calls);
        Assert.True((await GetStatusAsync()).DryRun);
    }

    [Fact]
    public void DryRunConfiguration_RegistersDryRunPowerActions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentServices(new AgentOptions { DryRun = true, DataDirectory = _dataDirectory });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DryRunPowerActions>(provider.GetRequiredService<IPowerActions>());
        Assert.Null(provider.GetService<IProcessRunner>());
    }

    private async Task StartAsync(bool dryRun)
    {
        _options = new AgentOptions
        {
            DryRun = dryRun,
            DataDirectory = _dataDirectory,
            LockPipeName = $"TailControl.Tests.{Guid.NewGuid():N}",
        };

        Directory.CreateDirectory(_options.ConfigDirectory);
        AuthorizedClientsFile.Save(
            _options.AuthorizedClientsPath,
            AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", false, TestSupport.Start, out _));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddAgentServices(_options);
        builder.Services.Replace(ServiceDescriptor.Singleton<IPowerActions>(_power));

        _app = builder.Build();
        _app.UseAgentPipeline();
        await _app.StartAsync();

        var signing = new SigningHttpHandler(new RequestSigner(_phone, _clock)) { InnerHandler = _app.GetTestServer().CreateHandler() };
        _client = new HttpClient(signing) { BaseAddress = new Uri("http://pc.example.ts.net:8444/") };
    }

    private async Task StopAppAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> PostCommandAsync(string name) =>
        _client.PostAsync(new Uri("v1/commands/" + name, UriKind.Relative), null);

    private async Task<AgentStatusResponse> GetStatusAsync() =>
        (await _client.GetFromJsonAsync<AgentStatusResponse>(new Uri("v1/status", UriKind.Relative)))!;
}
