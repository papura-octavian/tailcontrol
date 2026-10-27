using System.Runtime.InteropServices;
using TailControl.Agent.Locking;

namespace TailControl.Agent.Power;

public enum PowerOutcome
{
    Done,
    AlreadyScheduled,
    NothingToCancel,
    NoUserSession,
    Failed,
}

public readonly record struct PowerResult(PowerOutcome Outcome, string? Detail = null)
{
    public static PowerResult Done { get; } = new(PowerOutcome.Done);

    public static PowerResult Fail(string detail) => new(PowerOutcome.Failed, detail);
}

/// <summary>Actiunile de putere pe care le poate face agentul. Nimic altceva.</summary>
public interface IPowerActions
{
    Task<PowerResult> ScheduleShutdownAsync(TimeSpan delay, CancellationToken cancellationToken);

    Task<PowerResult> CancelShutdownAsync(CancellationToken cancellationToken);

    Task<PowerResult> SleepAsync(CancellationToken cancellationToken);

    Task<PowerResult> LockAsync(CancellationToken cancellationToken);
}

public sealed class WindowsPowerActions(IProcessRunner runner, ILockBroker lockBroker) : IPowerActions
{
    public async Task<PowerResult> ScheduleShutdownAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var exitCode = await runner.RunAsync(SystemCommands.ScheduleShutdown(delay), cancellationToken).ConfigureAwait(false);
        return exitCode switch
        {
            0 => PowerResult.Done,
            SystemCommands.ErrorShutdownIsScheduled => new PowerResult(PowerOutcome.AlreadyScheduled),
            _ => PowerResult.Fail($"shutdown.exe a intors codul {exitCode}."),
        };
    }

    public async Task<PowerResult> CancelShutdownAsync(CancellationToken cancellationToken)
    {
        var exitCode = await runner.RunAsync(SystemCommands.AbortShutdown(), cancellationToken).ConfigureAwait(false);
        return exitCode switch
        {
            0 => PowerResult.Done,
            SystemCommands.ErrorNoShutdownInProgress => new PowerResult(PowerOutcome.NothingToCancel),
            _ => PowerResult.Fail($"shutdown.exe /a a intors codul {exitCode}."),
        };
    }

    public Task<PowerResult> SleepAsync(CancellationToken cancellationToken)
    {
        NativeMethods.EnablePrivilege("SeShutdownPrivilege");
        return Task.FromResult(NativeMethods.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false)
            ? PowerResult.Done
            : PowerResult.Fail($"SetSuspendState a esuat (cod {Marshal.GetLastPInvokeError()})."));
    }

    public async Task<PowerResult> LockAsync(CancellationToken cancellationToken) =>
        await lockBroker.LockAllSessionsAsync(cancellationToken).ConfigureAwait(false) switch
        {
            LockOutcome.Locked => PowerResult.Done,
            LockOutcome.NoSession => new PowerResult(PowerOutcome.NoUserSession),
            _ => PowerResult.Fail("Helper-ul de blocare nu a confirmat."),
        };
}

/// <summary>
/// Folosit in modul DryRun: nu atinge sistemul. E a doua plasa de siguranta,
/// pe langa verificarea DryRun din <see cref="CommandService"/>.
/// </summary>
public sealed partial class DryRunPowerActions(ILogger<DryRunPowerActions> logger) : IPowerActions
{
    public Task<PowerResult> ScheduleShutdownAsync(TimeSpan delay, CancellationToken cancellationToken) => Log($"oprire in {delay.TotalSeconds:0} s");

    public Task<PowerResult> CancelShutdownAsync(CancellationToken cancellationToken) => Log("anulare oprire");

    public Task<PowerResult> SleepAsync(CancellationToken cancellationToken) => Log("sleep");

    public Task<PowerResult> LockAsync(CancellationToken cancellationToken) => Log("blocare");

    private Task<PowerResult> Log(string action)
    {
        LogDryRun(logger, action);
        return Task.FromResult(PowerResult.Done);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DryRun: {Action} (nu s-a executat nimic)")]
    private static partial void LogDryRun(ILogger logger, string action);
}
