using Microsoft.EntityFrameworkCore;
using TailControl.Hub.Data;
using TailControl.Shared.AspNetCore;

namespace TailControl.Hub;

public static class AuditActions
{
    public const string DeviceCreate = "device.create";
    public const string DeviceUpdate = "device.update";
    public const string DeviceDelete = "device.delete";
    public const string Wake = "wake";
    public const string Forbidden = "forbidden";
}

/// <summary>
/// Jurnalul de audit al Hub-ului (tabela <c>AuditLog</c>): cine, ce, cand, rezultat.
/// Citirile nu sunt jurnalizate, doar actiunile. Pastreaza ultimele <c>AuditMaxEntries</c> intrari.
/// </summary>
public sealed class HubAudit(HubDb db, HubOptions options, TimeProvider timeProvider)
{
    private const int TrimEvery = 100;

    public async Task WriteAsync(HttpContext context, string action, string? target, string result, string? detail = null)
    {
        var client = context.GetAuthenticatedClient();
        var record = new AuditRecord
        {
            TimeUtc = timeProvider.GetUtcNow(),
            ClientId = client.Id,
            ClientLabel = client.Label,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            Action = action,
            Target = target,
            Result = result,
            Detail = detail is { Length: > 256 } ? detail[..256] : detail,
        };

        db.AuditLog.Add(record);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (record.Id % TrimEvery == 0)
        {
            var cutoff = record.Id - options.AuditMaxEntries;
            await db.AuditLog.Where(a => a.Id <= cutoff).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }
}
