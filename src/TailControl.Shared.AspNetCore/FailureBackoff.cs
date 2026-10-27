using System.Collections.Concurrent;
using System.Net;

namespace TailControl.Shared.AspNetCore;

/// <summary>
/// Blocheaza temporar o adresa IP dupa prea multe cereri cu autentificare esuata.
/// O cerere valida nu reseteaza contorul, ca un atacator sa nu poata alterna.
/// </summary>
public sealed class FailureBackoff(SignatureOptions options, TimeProvider timeProvider)
{
    private const int MaxTrackedAddresses = 10_000;

    private readonly ConcurrentDictionary<IPAddress, State> _states = new();

    public bool IsBlocked(IPAddress? address)
    {
        if (!_states.TryGetValue(KeyFor(address), out var state))
        {
            return false;
        }

        lock (state)
        {
            return state.BlockedUntil > timeProvider.GetUtcNow();
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        var now = timeProvider.GetUtcNow();
        if (_states.Count >= MaxTrackedAddresses)
        {
            Sweep(now);
        }

        var state = _states.GetOrAdd(KeyFor(address), _ => new State());
        lock (state)
        {
            state.Failures.Enqueue(now);
            while (state.Failures.TryPeek(out var oldest) && oldest <= now - options.FailureWindow)
            {
                state.Failures.Dequeue();
            }

            if (state.Failures.Count >= options.FailuresBeforeBlock)
            {
                state.BlockedUntil = now + options.BlockDuration;
                state.Failures.Clear();
            }
        }
    }

    // Fara adresa cunoscuta (de ex. transport in proces) toate cererile impart o singura
    // intrare, ca esecurile sa nu poata ocoli blocarea.
    private static IPAddress KeyFor(IPAddress? address) => address ?? IPAddress.None;

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (address, state) in _states)
        {
            lock (state)
            {
                var lastFailure = state.Failures.Count > 0 ? state.Failures.Last() : DateTimeOffset.MinValue;
                if (state.BlockedUntil <= now && lastFailure <= now - options.FailureWindow)
                {
                    _states.TryRemove(address, out _);
                }
            }
        }
    }

    private sealed class State
    {
        public Queue<DateTimeOffset> Failures { get; } = new();

        public DateTimeOffset BlockedUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
