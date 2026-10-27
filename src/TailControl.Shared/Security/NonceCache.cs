using System.Collections.Concurrent;

namespace TailControl.Shared.Security;

public interface INonceCache
{
    /// <summary>
    /// Inregistreaza nonce-ul pana la <paramref name="expiresAt"/>. Intoarce <c>false</c>
    /// daca nonce-ul a mai fost folosit de acelasi client (sau daca cache-ul e plin).
    /// </summary>
    bool TryAdd(string clientId, string nonce, DateTimeOffset expiresAt);
}

/// <summary>
/// Cache de nonce-uri in memorie. Un nonce trebuie tinut minte doar cat timp
/// timestamp-ul cererii este inca acceptat; dupa aceea cererea e respinsa oricum.
/// Se pierde la repornire, de aceea verificatorul respinge si cererile semnate
/// inainte de pornirea serverului.
/// </summary>
public sealed class MemoryNonceCache(TimeProvider timeProvider, int capacity = 100_000) : INonceCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);
    private readonly Lock _sweepLock = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public int Count => _entries.Count;

    public bool TryAdd(string clientId, string nonce, DateTimeOffset expiresAt)
    {
        var now = timeProvider.GetUtcNow();
        SweepIfDue(now);

        if (_entries.Count >= capacity)
        {
            // Fail closed: mai bine respingem o cerere decat sa uitam nonce-uri.
            return false;
        }

        var key = string.Concat(clientId, ":", nonce);
        if (_entries.TryAdd(key, expiresAt))
        {
            return true;
        }

        // O intrare expirata poate fi refolosita doar daca nu a fost inlocuita intre timp.
        return _entries.TryGetValue(key, out var existing)
            && existing <= now
            && _entries.TryUpdate(key, expiresAt, existing);
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        if (now < _nextSweep && _entries.Count < capacity)
        {
            return;
        }

        lock (_sweepLock)
        {
            if (now < _nextSweep && _entries.Count < capacity)
            {
                return;
            }

            foreach (var (key, expiresAt) in _entries)
            {
                if (expiresAt <= now)
                {
                    _entries.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, expiresAt));
                }
            }

            _nextSweep = now + SignatureProtocol.MaxClockSkew;
        }
    }
}
