using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TailControl.Hub;
using TailControl.Hub.Data;
using TailControl.Hub.Status;
using TailControl.Hub.Wake;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;

namespace TailControl.Tests.Hub;

/// <summary>Hub-ul complet (SQLite real in fisier temporar), cu wake si probe false.</summary>
public sealed class HubEndpointTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestSupport.NewClock();
    private readonly FakeWakeSender _wake = new();
    private readonly FakeProbe _probe = new();
    private readonly ClientIdentity _admin = ClientIdentity.Create();
    private readonly ClientIdentity _viewer = ClientIdentity.Create();
    private readonly string _directory = TestSupport.NewTempPath("hub");
    private WebApplication _app = null!;
    private HttpClient _adminClient = null!;
    private HttpClient _viewerClient = null!;
    private HubOptions _options = null!;

    public async Task InitializeAsync()
    {
        _options = new HubOptions
        {
            ConfigDirectory = Path.Combine(_directory, "config"),
            DatabasePath = Path.Combine(_directory, "hub.db"),
            WakeBroadcastAddress = "192.168.1.255",
        };

        Directory.CreateDirectory(Path.Combine(_directory, "config"));
        AuthorizedClientsFile.Save(
            _options.AuthorizedClientsPath,
            AuthorizedClientList.Empty
                .Authorize(_admin.PublicKey, "Telefon", isAdmin: true, TestSupport.Start, out _)
                .Authorize(_viewer.PublicKey, "Laptop", isAdmin: false, TestSupport.Start, out _));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddHubServices(_options);
        builder.Services.Replace(ServiceDescriptor.Singleton<IWakeOnLanSender>(_wake));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDeviceProbe>(_probe));

        _app = builder.Build();
        await _app.InitializeDatabaseAsync();
        _app.UseHubPipeline();
        await _app.StartAsync();

        _adminClient = CreateClient(_admin);
        _viewerClient = CreateClient(_viewer);
    }

    public async Task DisposeAsync()
    {
        _adminClient.Dispose();
        _viewerClient.Dispose();
        await _app.DisposeAsync();
        _admin.Dispose();
        _viewer.Dispose();
        SqliteConnection.ClearAllPools();
        TestSupport.DeleteQuietly(_directory);
    }

    [Fact]
    public async Task Admin_CreatesDevice_NormalizesFields_AndAudits()
    {
        var response = await CreateAsync(new DeviceRequest("  PC birou ", "cc-28-aa-00-00-01", "PC.Example-Tailnet.ts.net.", null, DeviceKind.Desktop));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var device = (await response.Content.ReadFromJsonAsync<DeviceDto>())!;
        Assert.Equal("PC birou", device.Name);
        Assert.Equal("CC:28:AA:00:00:01", device.MacAddress);
        Assert.Equal("pc.example-tailnet.ts.net", device.Host);
        Assert.Equal(8444, device.AgentPort);
        Assert.Equal($"/v1/devices/{device.Id}", response.Headers.Location!.OriginalString);

        var audit = await AuditAsync();
        Assert.Equal(AuditActions.DeviceCreate, audit[0].Action);
        Assert.Equal("Telefon", audit[0].ClientLabel);
    }

    [Fact]
    public async Task Viewer_CanList_ButCannotModify()
    {
        var created = await CreateDeviceAsync("PC", "pc.example.ts.net");

        var list = await _viewerClient.GetFromJsonAsync<List<DeviceDto>>(new Uri("v1/devices", UriKind.Relative));
        var create = await _viewerClient.PostAsJsonAsync(new Uri("v1/devices", UriKind.Relative), Request("Alt", "alt.example.ts.net"));
        var update = await _viewerClient.PutAsJsonAsync(new Uri($"v1/devices/{created.Id}", UriKind.Relative), Request("Redenumit", "pc.example.ts.net"));
        var delete = await _viewerClient.DeleteAsync(new Uri($"v1/devices/{created.Id}", UriKind.Relative));
        var audit = await _viewerClient.GetAsync(new Uri("v1/audit", UriKind.Relative));

        Assert.Single(list!);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        Assert.Equal("PC", (await GetAsync(created.Id)).Name);
        Assert.Equal(4, (await AuditAsync()).Count(a => a.Action == AuditActions.Forbidden && a.ClientLabel == "Laptop"));
    }

    [Theory]
    [InlineData("", "pc.example.ts.net", null, "name")]
    [InlineData("PC", "192.168.1.10", null, "host")]
    [InlineData("PC", "pc.local", null, "host")]
    [InlineData("PC", "pc", null, "host")]
    [InlineData("PC", "pc.example.ts.net.evil.com", null, "host")]
    [InlineData("PC", "pc_1.example.ts.net", null, "host")]
    [InlineData("PC", "pc.example.ts.net", "zz:zz:zz:zz:zz:zz", "macAddress")]
    [InlineData("PC", "pc.example.ts.net", "FF:FF:FF:FF:FF:FF", "macAddress")]
    [InlineData("PC", "pc.example.ts.net", "00:00:00:00:00:00", "macAddress")]
    public async Task InvalidDevice_Returns400_WithFieldError(string name, string host, string? mac, string field)
    {
        var response = await CreateAsync(new DeviceRequest(name, mac, host, null, DeviceKind.Desktop));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"name\":\"PC\",\"host\":\"pc.example.ts.net\",\"kind\":\"Desktop\",\"macAddress\":null,\"agentPort\":null,\"command\":\"calc.exe\"}")]
    [InlineData("{\"host\":\"pc.example.ts.net\",\"kind\":\"Desktop\",\"macAddress\":null,\"agentPort\":null}")]
    [InlineData("{\"name\":null,\"host\":\"pc.example.ts.net\",\"kind\":\"Desktop\",\"macAddress\":null,\"agentPort\":null}")]
    [InlineData("{\"name\":\"PC\",\"host\":\"pc.example.ts.net\",\"kind\":\"Server\",\"macAddress\":null,\"agentPort\":null}")]
    [InlineData("nu e json")]
    public async Task MalformedOrUnknownJson_Returns400(string json)
    {
        var response = await _adminClient.PostAsync(
            new Uri("v1/devices", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _adminClient.GetFromJsonAsync<List<DeviceDto>>(new Uri("v1/devices", UriKind.Relative)) ?? []);
    }

    [Fact]
    public async Task DuplicateNameOrHost_Returns409()
    {
        await CreateDeviceAsync("PC", "pc.example.ts.net");

        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(Request("pc", "alt.example.ts.net"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(Request("Alt", "PC.example.ts.net"))).StatusCode);
    }

    [Fact]
    public async Task Update_AndDelete_Work()
    {
        var created = await CreateDeviceAsync("PC", "pc.example.ts.net");

        var update = await _adminClient.PutAsJsonAsync(
            new Uri($"v1/devices/{created.Id}", UriKind.Relative),
            new DeviceRequest("PC sufragerie", "AA:BB:CC:00:00:02", "pc.example.ts.net", 8444, DeviceKind.Desktop));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("PC sufragerie", (await GetAsync(created.Id)).Name);

        var delete = await _adminClient.DeleteAsync(new Uri($"v1/devices/{created.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _adminClient.GetAsync(new Uri($"v1/devices/{created.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _adminClient.DeleteAsync(new Uri($"v1/devices/{created.Id}", UriKind.Relative))).StatusCode);
    }

    [Theory]
    [InlineData("v1/devices/0")]
    [InlineData("v1/devices/-1")]
    [InlineData("v1/devices/abc")]
    [InlineData("v1/devices/1;drop")]
    public async Task InvalidIdInRoute_Returns404(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _adminClient.GetAsync(new Uri(path, UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Wake_SendsToInventoryMac_AnyAuthorizedClient()
    {
        var created = await CreateDeviceAsync("PC", "pc.example.ts.net", "CC:28:AA:00:00:01");

        var response = await _viewerClient.PostAsync(new Uri($"v1/devices/{created.Id}/wake", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new byte[] { 0xCC, 0x28, 0xAA, 0x00, 0x00, 0x01 }], _wake.Sent);
        var entry = (await AuditAsync()).First(a => a.Action == AuditActions.Wake);
        Assert.Equal("ok", entry.Result);
        Assert.Equal("Laptop", entry.ClientLabel);
    }

    [Fact]
    public async Task Wake_WithoutMac_Returns409_AndSendsNothing()
    {
        var created = await CreateDeviceAsync("Laptop", "laptop.example.ts.net");

        var response = await _adminClient.PostAsync(new Uri($"v1/devices/{created.Id}/wake", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_wake.Sent);
    }

    [Fact]
    public async Task Wake_UnknownDevice_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _adminClient.PostAsync(new Uri("v1/devices/999/wake", UriKind.Relative), null)).StatusCode);
        Assert.Empty(_wake.Sent);
    }

    [Fact]
    public async Task Status_ReflectsProbe_AndKeepsLastSeen()
    {
        var created = await CreateDeviceAsync("PC", "pc.example.ts.net");
        var monitor = _app.Services.GetRequiredService<StatusMonitor>();

        _probe.Online.Add("pc.example.ts.net");
        await monitor.CheckAllAsync(CancellationToken.None);
        var online = await GetAsync(created.Id);
        Assert.True(online.Online);
        Assert.Equal(TestSupport.Start, online.LastSeenUtc);

        // Golim inainte de Advance: tick-ul timer-ului ruleaza si el o verificare in fundal.
        _probe.Online.Clear();
        _clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.CheckAllAsync(CancellationToken.None);
        var offline = await GetAsync(created.Id);
        Assert.False(offline.Online);
        Assert.Equal(TestSupport.Start, offline.LastSeenUtc);
        Assert.Equal(TestSupport.Start.AddSeconds(15), offline.LastCheckedUtc);

        // LastSeen a fost persistat, deci supravietuieste unei reporniri (cache gol).
        await using var scope = _app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<HubDb>().Devices.SingleAsync(d => d.Id == created.Id);
        Assert.Equal(TestSupport.Start, stored.LastSeenUtc);
    }

    [Fact]
    public async Task UnsignedRequest_IsRejected()
    {
        using var raw = new HttpClient(_app.GetTestServer().CreateHandler()) { BaseAddress = _adminClient.BaseAddress };

        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.GetAsync(new Uri("v1/devices", UriKind.Relative))).StatusCode);
    }

    private HttpClient CreateClient(ClientIdentity identity) =>
        new(new SigningHttpHandler(new RequestSigner(identity, _clock)) { InnerHandler = _app.GetTestServer().CreateHandler() })
        {
            BaseAddress = new Uri("http://pi.example.ts.net:8443/"),
        };

    private static DeviceRequest Request(string name, string host, string? mac = null) =>
        new(name, mac, host, null, DeviceKind.Desktop);

    private Task<HttpResponseMessage> CreateAsync(DeviceRequest request) =>
        _adminClient.PostAsJsonAsync(new Uri("v1/devices", UriKind.Relative), request);

    private async Task<DeviceDto> CreateDeviceAsync(string name, string host, string? mac = null)
    {
        var response = await CreateAsync(Request(name, host, mac));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeviceDto>())!;
    }

    private async Task<DeviceDto> GetAsync(int id) =>
        (await _adminClient.GetFromJsonAsync<DeviceDto>(new Uri($"v1/devices/{id}", UriKind.Relative)))!;

    private async Task<List<AuditEntryDto>> AuditAsync() =>
        (await _adminClient.GetFromJsonAsync<List<AuditEntryDto>>(new Uri("v1/audit", UriKind.Relative)))!;

    private sealed class FakeWakeSender : IWakeOnLanSender
    {
        public List<byte[]> Sent { get; } = [];

        public Task SendAsync(byte[] mac, CancellationToken cancellationToken)
        {
            Sent.Add(mac);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe : IDeviceProbe
    {
        public HashSet<string> Online { get; } = [];

        public Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(Online.Contains(host));
    }
}
