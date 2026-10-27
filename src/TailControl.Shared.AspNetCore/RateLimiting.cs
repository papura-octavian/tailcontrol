using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace TailControl.Shared.AspNetCore;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Limita globala per adresa IP, aplicata inainte de verificarea semnaturii
    /// (care costa CPU, mai ales pe Raspberry Pi).
    /// </summary>
    public static IServiceCollection AddTailControlRateLimiting(this IServiceCollection services, int permitsPerMinute = 60)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }
}
