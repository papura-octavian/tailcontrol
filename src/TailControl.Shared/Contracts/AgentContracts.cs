using System.Text.Json.Serialization;

namespace TailControl.Shared.Contracts;

/// <summary>Rute expuse de agent (port implicit 8444).</summary>
public static class AgentRoutes
{
    public const int DefaultPort = 8444;

    /// <summary><c>POST</c>, fara body. <c>{command}</c> este un nume din <see cref="Commands.AgentCommands"/>.</summary>
    public const string Command = "/v1/commands/{command}";

    /// <summary><c>GET</c>: starea agentului.</summary>
    public const string Status = "/v1/status";

    public static string ForCommand(Commands.AgentCommand command) =>
        "/v1/commands/" + Commands.AgentCommands.ToRouteName(command);
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandStatus>))]
public enum CommandStatus
{
    /// <summary>Comanda a fost executata (ex. blocare).</summary>
    Executed,

    /// <summary>Comanda va fi executata in scurt timp (ex. oprire cu countdown, sleep).</summary>
    Scheduled,

    /// <summary>Agentul ruleaza in modul DryRun: comanda a fost doar logata.</summary>
    DryRun,

    /// <summary>Nu era nimic de facut (ex. anulare fara oprire programata).</summary>
    NothingToDo,

    /// <summary>Comanda nu se poate executa acum (ex. blocare fara utilizator logat).</summary>
    Unavailable,

    Failed,
}

public sealed record CommandResponse(
    string Command,
    CommandStatus Status,
    string Message,
    DateTimeOffset? ScheduledForUtc = null);

public sealed record AgentStatusResponse(
    string AgentVersion,
    string MachineName,
    bool DryRun,
    DateTimeOffset? ShutdownScheduledForUtc,
    int LockHelperSessions);
