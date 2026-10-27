using TailControl.Agent.Power;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;

namespace TailControl.Agent;

/// <summary>
/// Executa o comanda din lista fixa si o scrie in jurnalul de audit. Primeste doar
/// <see cref="AgentCommand"/>, niciodata text venit de la client.
/// </summary>
public sealed partial class CommandService(
    IPowerActions power,
    AgentOptions options,
    AuditLog audit,
    TimeProvider timeProvider,
    ILogger<CommandService> logger)
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _shutdownAt;

    /// <summary>Oprirea programata de acest agent, daca nu a trecut deja.</summary>
    public DateTimeOffset? ShutdownScheduledFor
    {
        get
        {
            lock (_lock)
            {
                return _shutdownAt > timeProvider.GetUtcNow() ? _shutdownAt : null;
            }
        }
    }

    public async Task<CommandResponse> ExecuteAsync(AgentCommand command, AuthorizedClient client, string? remoteIp)
    {
        CommandResponse response;
        try
        {
            response = options.DryRun
                ? DryRun(command)
                : await ExecuteForRealAsync(command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCommandFailed(logger, ex, command);
            response = new CommandResponse(Name(command), CommandStatus.Failed, "Comanda a esuat pe dispozitiv.");
        }

        audit.Write(new AuditEntry(
            timeProvider.GetUtcNow(), client.Id, client.Label, remoteIp, Name(command), response.Status.ToString(), response.Message));
        LogCommand(logger, command, client.Label, response.Status);
        return response;
    }

    private static CommandResponse DryRun(AgentCommand command) =>
        new(Name(command), CommandStatus.DryRun, $"DryRun: comanda '{Name(command)}' a fost doar logata.");

    // Comenzile de sistem nu se anuleaza daca clientul inchide conexiunea, ca starea sa ramana coerenta.
    private async Task<CommandResponse> ExecuteForRealAsync(AgentCommand command)
    {
        var name = Name(command);
        switch (command)
        {
            case AgentCommand.Shutdown:
            {
                var delay = TimeSpan.FromSeconds(options.ShutdownDelaySeconds);
                var result = await power.ScheduleShutdownAsync(delay, CancellationToken.None).ConfigureAwait(false);
                if (result.Outcome == PowerOutcome.Done)
                {
                    var at = timeProvider.GetUtcNow() + delay;
                    lock (_lock)
                    {
                        _shutdownAt = at;
                    }

                    return new(name, CommandStatus.Scheduled, $"Oprire in {options.ShutdownDelaySeconds} s. Poti anula din aplicatie.", at);
                }

                return result.Outcome == PowerOutcome.AlreadyScheduled
                    ? new(name, CommandStatus.NothingToDo, "O oprire este deja programata.", ShutdownScheduledFor)
                    : Failed(name, result);
            }

            case AgentCommand.CancelShutdown:
            {
                var result = await power.CancelShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                if (result.Outcome is PowerOutcome.Done or PowerOutcome.NothingToCancel)
                {
                    lock (_lock)
                    {
                        _shutdownAt = null;
                    }
                }

                return result.Outcome switch
                {
                    PowerOutcome.Done => new(name, CommandStatus.Executed, "Oprirea a fost anulata."),
                    PowerOutcome.NothingToCancel => new(name, CommandStatus.NothingToDo, "Nu exista nicio oprire programata."),
                    _ => Failed(name, result),
                };
            }

            case AgentCommand.Sleep:
            {
                // Raspundem inainte ca dispozitivul sa adoarma.
                var delay = TimeSpan.FromSeconds(options.SleepDelaySeconds);
                _ = Task.Run(() => SleepAfterAsync(delay));
                return new(name, CommandStatus.Scheduled, "Dispozitivul intra in sleep in cateva secunde.", timeProvider.GetUtcNow() + delay);
            }

            case AgentCommand.Lock:
            {
                var result = await power.LockAsync(CancellationToken.None).ConfigureAwait(false);
                return result.Outcome switch
                {
                    PowerOutcome.Done => new(name, CommandStatus.Executed, "Sesiunea a fost blocata."),
                    PowerOutcome.NoUserSession => new(name, CommandStatus.Unavailable, "Niciun utilizator logat: dispozitivul este deja la ecranul de autentificare."),
                    _ => Failed(name, result),
                };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Comanda necunoscuta.");
        }
    }

    private async Task SleepAfterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, timeProvider).ConfigureAwait(false);
            var result = await power.SleepAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Outcome != PowerOutcome.Done)
            {
                LogSleepFailed(logger, result.Detail);
            }
        }
        catch (Exception ex)
        {
            LogCommandFailed(logger, ex, AgentCommand.Sleep);
        }
    }

    private CommandResponse Failed(string name, PowerResult result)
    {
        LogPowerFailed(logger, name, result.Detail);
        return new(name, CommandStatus.Failed, "Comanda a esuat pe dispozitiv.");
    }

    private static string Name(AgentCommand command) => AgentCommands.ToRouteName(command);

    [LoggerMessage(Level = LogLevel.Information, Message = "Comanda {Command} de la {Client}: {Status}")]
    private static partial void LogCommand(ILogger logger, AgentCommand command, string client, CommandStatus status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Comanda {Command} a aruncat o exceptie")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception, AgentCommand command);

    [LoggerMessage(Level = LogLevel.Error, Message = "Comanda {Command} a esuat: {Detail}")]
    private static partial void LogPowerFailed(ILogger logger, string command, string? detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sleep a esuat: {Detail}")]
    private static partial void LogSleepFailed(ILogger logger, string? detail);
}
