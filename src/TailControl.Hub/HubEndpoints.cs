using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TailControl.Hub.Data;
using TailControl.Hub.Status;
using TailControl.Hub.Wake;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Contracts;
using TailControl.Shared.Validation;

namespace TailControl.Hub;

public static partial class HubEndpoints
{
    public static IServiceCollection AddHubServices(this IServiceCollection services, HubOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        Directory.CreateDirectory(Path.GetDirectoryName(options.ResolvedDatabasePath)!);
        services.AddDbContext<HubDb>(o => o.UseSqlite($"Data Source={options.ResolvedDatabasePath}"));
        services.AddScoped<HubAudit>();

        services.AddSingleton<DeviceStatusCache>();
        services.AddSingleton<IDeviceProbe, TcpDeviceProbe>();
        services.AddSingleton<StatusMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusMonitor>());
        services.AddSingleton<IWakeOnLanSender, UdpWakeOnLanSender>();

        // Cereri stricte: campuri necunoscute sau lipsa => 400.
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            o.SerializerOptions.RespectNullableAnnotations = true;
            o.SerializerOptions.RespectRequiredConstructorParameters = true;
        });

        services.AddTailControlRateLimiting();
        services.AddTailControlSignatures(o => o.AuthorizedClientsPath = options.AuthorizedClientsPath);
        return services;
    }

    /// <summary>Aplica migrarile si face o interogare de incalzire (primul apel pe Pi e lent fara ea).</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        await db.Database.MigrateAsync();
        _ = await db.Devices.CountAsync();
    }

    /// <summary>Pipeline-ul: rate limiting, apoi semnatura, apoi endpoint-urile. Nimic nu e public.</summary>
    public static WebApplication UseHubPipeline(this WebApplication app)
    {
        app.UseRateLimiter();
        app.UseTailControlSignatures();

        app.MapGet(HubRoutes.Devices, async (HubDb db, DeviceStatusCache statuses) =>
        {
            var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
            return devices.Select(d => ToDto(d, statuses.Get(d.Id)));
        });

        app.MapGet(HubRoutes.Device, async (int id, HubDb db, DeviceStatusCache statuses) =>
            await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id) is { } device
                ? Results.Ok(ToDto(device, statuses.Get(id)))
                : Results.NotFound());

        app.MapPost(HubRoutes.Devices, async (DeviceRequest request, HttpContext context, HubDb db, HubAudit audit, HubOptions options, TimeProvider time) =>
        {
            if (await RequireAdminAsync(context, audit, AuditActions.DeviceCreate, request.Name) is { } forbidden)
            {
                return forbidden;
            }

            var errors = DeviceValidation.Validate(request, options.HostSuffixes, out var valid);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (await FindConflictAsync(db, valid, exceptId: null) is { } conflict)
            {
                return conflict;
            }

            var now = time.GetUtcNow();
            var device = new Device
            {
                Name = valid.Name,
                MacAddress = valid.MacAddress,
                Host = valid.Host,
                AgentPort = valid.AgentPort!.Value,
                Kind = valid.Kind,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            db.Devices.Add(device);
            if (!await TrySaveAsync(db))
            {
                return Conflict();
            }

            await audit.WriteAsync(context, AuditActions.DeviceCreate, Target(device), "ok");
            return Results.Created(HubRoutes.ForDevice(device.Id), ToDto(device, default));
        });

        app.MapPut(HubRoutes.Device, async (int id, DeviceRequest request, HttpContext context, HubDb db, HubAudit audit, HubOptions options, TimeProvider time, DeviceStatusCache statuses) =>
        {
            if (await RequireAdminAsync(context, audit, AuditActions.DeviceUpdate, $"#{id}") is { } forbidden)
            {
                return forbidden;
            }

            var device = await db.Devices.FindAsync(id);
            if (device is null)
            {
                return Results.NotFound();
            }

            var errors = DeviceValidation.Validate(request, options.HostSuffixes, out var valid);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            if (await FindConflictAsync(db, valid, exceptId: id) is { } conflict)
            {
                return conflict;
            }

            device.Name = valid.Name;
            device.MacAddress = valid.MacAddress;
            device.Host = valid.Host;
            device.AgentPort = valid.AgentPort!.Value;
            device.Kind = valid.Kind;
            device.UpdatedUtc = time.GetUtcNow();
            if (!await TrySaveAsync(db))
            {
                return Conflict();
            }

            await audit.WriteAsync(context, AuditActions.DeviceUpdate, Target(device), "ok");
            return Results.Ok(ToDto(device, statuses.Get(id)));
        });

        app.MapDelete(HubRoutes.Device, async (int id, HttpContext context, HubDb db, HubAudit audit, DeviceStatusCache statuses) =>
        {
            if (await RequireAdminAsync(context, audit, AuditActions.DeviceDelete, $"#{id}") is { } forbidden)
            {
                return forbidden;
            }

            var device = await db.Devices.FindAsync(id);
            if (device is null)
            {
                return Results.NotFound();
            }

            db.Devices.Remove(device);
            await db.SaveChangesAsync();
            statuses.Remove(id);

            await audit.WriteAsync(context, AuditActions.DeviceDelete, Target(device), "ok");
            return Results.NoContent();
        });

        app.MapPost(HubRoutes.Wake, async (int id, HttpContext context, HubDb db, HubAudit audit, HubOptions options, IWakeOnLanSender sender, ILoggerFactory loggers) =>
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (device.MacAddress is null)
            {
                await audit.WriteAsync(context, AuditActions.Wake, Target(device), "unavailable", "fara MAC");
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Dispozitivul nu are adresa MAC configurata.");
            }

            if (options.ParsedWakeBroadcast is null)
            {
                await audit.WriteAsync(context, AuditActions.Wake, Target(device), "unavailable", "broadcast neconfigurat");
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Wake-on-LAN nu este configurat pe Hub.");
            }

            try
            {
                await sender.SendAsync(DeviceValidation.MacToBytes(device.MacAddress), CancellationToken.None);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
            {
                LogWakeFailed(loggers.CreateLogger(typeof(HubEndpoints)), ex, device.Host);
                await audit.WriteAsync(context, AuditActions.Wake, Target(device), "failed", ex.GetType().Name);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Trimiterea pachetului de pornire a esuat.");
            }

            await audit.WriteAsync(context, AuditActions.Wake, Target(device), "ok");
            return Results.Ok(new WakeResponse(id, "Pachetul de pornire a fost trimis. Pornirea poate dura pana la un minut."));
        });

        app.MapGet(HubRoutes.Audit, async (int? take, HttpContext context, HubDb db, HubAudit audit) =>
        {
            if (await RequireAdminAsync(context, audit, "audit.read", null) is { } forbidden)
            {
                return forbidden;
            }

            var count = Math.Clamp(take ?? 50, 1, 500);
            var entries = await db.AuditLog.AsNoTracking().OrderByDescending(a => a.Id).Take(count)
                .Select(a => new AuditEntryDto(a.Id, a.TimeUtc, a.ClientId, a.ClientLabel, a.RemoteIp, a.Action, a.Target, a.Result, a.Detail))
                .ToListAsync();
            return Results.Ok(entries);
        });

        return app;
    }

    internal static DeviceDto ToDto(Device d, DeviceStatus status) =>
        new(d.Id, d.Name, d.MacAddress, d.Host, d.AgentPort, d.Kind,
            status.Online, status.LastSeenUtc ?? d.LastSeenUtc, status.LastCheckedUtc, d.CreatedUtc, d.UpdatedUtc);

    private static string Target(Device d) => $"#{d.Id} {d.Name}";

    // Doar clientii marcati admin (authorize-client --admin) pot modifica inventarul sau citi auditul.
    private static async Task<IResult?> RequireAdminAsync(HttpContext context, HubAudit audit, string action, string? target)
    {
        if (context.GetAuthenticatedClient().IsAdmin)
        {
            return null;
        }

        await audit.WriteAsync(context, AuditActions.Forbidden, target, "denied", action);
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Doar clientii admin pot face aceasta operatie.");
    }

    private static async Task<IResult?> FindConflictAsync(HubDb db, DeviceRequest valid, int? exceptId)
    {
        // Coloana Name are colatia NOCASE, deci comparatia e case-insensitive in SQLite.
        var exists = await db.Devices.AnyAsync(d => d.Id != exceptId && (d.Host == valid.Host || d.Name == valid.Name));
        return exists ? Conflict() : null;
    }

    private static IResult Conflict() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Exista deja un dispozitiv cu acelasi nume sau host.");

    private static async Task<bool> TrySaveAsync(HubDb db)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Constrangerile UNIQUE prind cererile simultane.
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Wake-on-LAN catre {Host} a esuat")]
    private static partial void LogWakeFailed(ILogger logger, Exception exception, string host);
}
