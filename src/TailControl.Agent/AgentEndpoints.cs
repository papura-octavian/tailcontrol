using Microsoft.Extensions.DependencyInjection.Extensions;
using TailControl.Agent.Locking;
using TailControl.Agent.Power;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;

namespace TailControl.Agent;

public static class AgentEndpoints
{
    public static IServiceCollection AddAgentServices(this IServiceCollection services, AgentOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AuditLog(options.AuditLogPath));

        services.AddSingleton(new LockBrokerOptions { PipeName = options.LockPipeName });
        services.AddSingleton<LockBroker>();
        services.AddSingleton<ILockBroker>(sp => sp.GetRequiredService<LockBroker>());
        services.AddHostedService(sp => sp.GetRequiredService<LockBroker>());

        if (options.DryRun)
        {
            services.AddSingleton<IPowerActions, DryRunPowerActions>();
        }
        else
        {
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IPowerActions, WindowsPowerActions>();
        }

        services.AddSingleton<CommandService>();

        services.AddTailControlRateLimiting();
        services.AddTailControlSignatures(o => o.AuthorizedClientsPath = options.AuthorizedClientsPath);
        return services;
    }

    /// <summary>Pipeline-ul: rate limiting, apoi semnatura, apoi endpoint-urile. Nimic nu e public.</summary>
    public static WebApplication UseAgentPipeline(this WebApplication app)
    {
        app.UseRateLimiter();
        app.UseTailControlSignatures();

        app.MapPost(AgentRoutes.Command, async (string command, HttpContext context, CommandService commands) =>
        {
            // Doar numele din lista fixa; orice alt text da 404 si nu ajunge la CommandService.
            if (!AgentCommands.TryParse(command, out var parsed))
            {
                return Results.NotFound();
            }

            var response = await commands.ExecuteAsync(
                parsed, context.GetAuthenticatedClient(), context.Connection.RemoteIpAddress?.ToString());

            return response.Status switch
            {
                CommandStatus.Unavailable => Results.Conflict(response),
                CommandStatus.Failed => Results.Json(response, statusCode: StatusCodes.Status500InternalServerError),
                _ => Results.Ok(response),
            };
        });

        app.MapGet(AgentRoutes.Status, (CommandService commands, ILockBroker lockBroker, AgentOptions options) =>
            new AgentStatusResponse(
                typeof(AgentEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Environment.MachineName,
                options.DryRun,
                commands.ShutdownScheduledFor,
                lockBroker.ConnectedSessions));

        return app;
    }
}
