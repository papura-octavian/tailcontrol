using System.Security.Cryptography;
using TailControl.Shared.Security;

namespace TailControl.Client.Core;

/// <summary>Stocarea securizata a cheii private (SecureStorage: Android Keystore / DPAPI pe Windows).</summary>
public interface IKeyStore
{
    Task<string?> GetAsync();

    Task SetAsync(string value);

    void Remove();
}

public sealed class InMemoryKeyStore : IKeyStore
{
    private string? _value;

    public Task<string?> GetAsync() => Task.FromResult(_value);

    public Task SetAsync(string value)
    {
        _value = value;
        return Task.CompletedTask;
    }

    public void Remove() => _value = null;
}

/// <summary>
/// Identitatea acestui client. Cheia se genereaza la prima pornire si nu paraseste dispozitivul;
/// in afara apare doar cheia publica (pentru pairing).
/// </summary>
public sealed class ClientIdentityProvider(IKeyStore store) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ClientIdentity? _current;

    /// <summary>
    /// <c>true</c> daca la incarcare cheia salvata era corupta si a fost inlocuita (clientul
    /// trebuie reautorizat). Aplicatia afiseaza asta in setari.
    /// </summary>
    public bool WasRegenerated { get; private set; }

    public async Task<ClientIdentity> GetAsync()
    {
        if (_current is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                return _current;
            }

            var saved = await store.GetAsync().ConfigureAwait(false);
            if (saved is not null)
            {
                try
                {
                    return _current = ClientIdentity.FromPrivateKey(saved);
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    WasRegenerated = true;
                }
            }

            return _current = await CreateAndSaveAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Genereaza o cheie noua. Cheia veche trebuie revocata pe agenti si Hub.</summary>
    public async Task<ClientIdentity> RegenerateAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _current;
            _current = await CreateAndSaveAsync().ConfigureAwait(false);
            old?.Dispose();
            return _current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _lock.Dispose();
    }

    private async Task<ClientIdentity> CreateAndSaveAsync()
    {
        var identity = ClientIdentity.Create();
        await store.SetAsync(identity.ExportPrivateKey()).ConfigureAwait(false);
        return identity;
    }
}
