using System.Collections.Concurrent;
using TailControl.Agent.Power;

namespace TailControl.Tests.Agent;

/// <summary>Inregistreaza apelurile in loc sa atinga sistemul.</summary>
internal sealed class FakePowerActions : IPowerActions
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public TaskCompletionSource SleepCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PowerResult ShutdownResult { get; set; } = PowerResult.Done;

    public PowerResult CancelResult { get; set; } = PowerResult.Done;

    public PowerResult LockResult { get; set; } = PowerResult.Done;

    public Task<PowerResult> ScheduleShutdownAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"shutdown:{delay.TotalSeconds}");
        return Task.FromResult(ShutdownResult);
    }

    public Task<PowerResult> CancelShutdownAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("cancel");
        return Task.FromResult(CancelResult);
    }

    public Task<PowerResult> SleepAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("sleep");
        SleepCalled.TrySetResult();
        return Task.FromResult(PowerResult.Done);
    }

    public Task<PowerResult> LockAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("lock");
        return Task.FromResult(LockResult);
    }
}
