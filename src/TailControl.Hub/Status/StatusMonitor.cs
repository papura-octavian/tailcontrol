using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using TailControl.Hub.Data;

namespace TailControl.Hub.Status;

public readonly record struct DeviceStatus(bool Online, DateTimeOffset? LastSeenUtc, DateTimeOffset? LastCheckedUtc);

/// <summary>Ultimul status cunoscut al fiecarui dispozitiv, in memorie.</summary>
public sealed class DeviceStatusCache
{
    private readonly ConcurrentDictionary<int, DeviceStatus> _statuses = new();

    public DeviceStatus Get(int deviceId) => _statuses.GetValueOrDefault(deviceId);

    public void Set(int deviceId, DeviceStatus status) => _statuses[deviceId] = status;

    public void Remove(int deviceId) => _statuses.TryRemove(deviceId, out _);

    public void RemoveAllExcept(IReadOnlySet<int> deviceIds)
    {
        foreach (var id in _statuses.Keys.Where(id => !deviceIds.Contains(id)))
        {
            _statuses.TryRemove(id, out _);
        }
    }
}

public interface IDeviceProbe
{
    /// <summary><c>true</c> daca portul agentului accepta conexiuni TCP.</summary>
    Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Doar TCP connect: nu trimite nimic, nu se autentifica si nu afla nimic in afara de
/// "portul raspunde". Agentul nu expune nimic fara semnatura.
/// </summary>
public sealed class TcpDeviceProbe : IDeviceProbe
{
    public async Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Verifica periodic (implicit la 15 s) daca fiecare agent e pornit. <c>LastSeenUtc</c> e precis
/// in memorie, dar se scrie in baza de date cel mult o data la 10 minute per dispozitiv.
/// </summary>
public sealed partial class StatusMonitor(
    IServiceScopeFactory scopes,
    IDeviceProbe probe,
    DeviceStatusCache cache,
    HubOptions options,
    TimeProvider timeProvider,
    ILogger<StatusMonitor> logger) : BackgroundService
{
    internal static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(10);

    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var devices = await db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.Host, d.AgentPort, d.LastSeenUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var timeout = TimeSpan.FromMilliseconds(options.StatusTimeoutMilliseconds);
        var results = await Task.WhenAll(devices.Select(async d =>
            (Device: d, Online: await probe.IsReachableAsync(d.Host, d.AgentPort, timeout, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        foreach (var (device, online) in results)
        {
            var previous = cache.Get(device.Id);
            var lastSeen = online ? now : previous.LastSeenUtc ?? device.LastSeenUtc;
            cache.Set(device.Id, new DeviceStatus(online, lastSeen, now));

            if (previous.LastCheckedUtc is not null && previous.Online != online)
            {
                LogTransition(logger, device.Host, online ? "online" : "offline");
            }

            if (online && (device.LastSeenUtc is null || now - device.LastSeenUtc >= PersistInterval))
            {
                await db.Devices.Where(d => d.Id == device.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenUtc, now), cancellationToken).ConfigureAwait(false);
            }
        }

        cache.RemoveAllExcept(devices.Select(d => d.Id).ToHashSet());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.StatusIntervalSeconds), timeProvider);
        do
        {
            try
            {
                await CheckAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Host} este acum {State}")]
    private static partial void LogTransition(ILogger logger, string host, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Verificarea statusului a esuat")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
