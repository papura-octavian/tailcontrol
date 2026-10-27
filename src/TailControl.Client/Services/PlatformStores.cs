using TailControl.Client.Core;

namespace TailControl.Client.Services;

/// <summary>Setarile in <see cref="Preferences"/> (nu contin secrete).</summary>
public sealed class PreferencesSettingsStore : IClientSettingsStore
{
    private const string HubUrlKey = "hub_url";
    private const string ThisDeviceKey = "this_device_id";
    private const string AgentsHttpsKey = "agents_https";
    private const string VerifyKey = "require_verification";

    public ClientSettings Load()
    {
        var hub = Preferences.Default.Get<string?>(HubUrlKey, null);
        var thisDevice = Preferences.Default.Get(ThisDeviceKey, -1);
        return new ClientSettings
        {
            HubUrl = ClientSettings.TryParseHubUrl(hub, out var uri, out _) ? uri : null,
            ThisDeviceId = thisDevice > 0 ? thisDevice : null,
            AgentsUseHttps = Preferences.Default.Get(AgentsHttpsKey, true),
            RequireUserVerification = Preferences.Default.Get(VerifyKey, true),
        };
    }

    public void Save(ClientSettings settings)
    {
        Preferences.Default.Set(HubUrlKey, settings.HubUrl?.ToString());
        Preferences.Default.Set(ThisDeviceKey, settings.ThisDeviceId ?? -1);
        Preferences.Default.Set(AgentsHttpsKey, settings.AgentsUseHttps);
        Preferences.Default.Set(VerifyKey, settings.RequireUserVerification);
    }
}

/// <summary>
/// Cheia privata in <see cref="SecureStorage"/>: Android Keystore pe telefon,
/// DPAPI (legat de contul Windows) pe PC. Nu se scrie nicaieri altundeva.
/// </summary>
public sealed class SecureKeyStore : IKeyStore
{
    private const string Key = "tailcontrol_client_private_key";

    public Task<string?> GetAsync() => SecureStorage.Default.GetAsync(Key);

    public Task SetAsync(string value) => SecureStorage.Default.SetAsync(Key, value);

    public void Remove() => SecureStorage.Default.Remove(Key);
}
