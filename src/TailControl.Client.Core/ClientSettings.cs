using System.Diagnostics.CodeAnalysis;
using TailControl.Shared.Contracts;
using TailControl.Shared.Validation;

namespace TailControl.Client.Core;

/// <summary>Setarile aplicatiei (fara chei: cheia privata sta separat, in SecureStorage).</summary>
public sealed record ClientSettings
{
    /// <summary>Adresa Hub-ului, ex. <c>https://pihole.tailnet-1234.ts.net:8443</c>. <c>null</c> = neconfigurat.</summary>
    public Uri? HubUrl { get; init; }

    /// <summary>Dispozitivul din inventar pe care ruleaza aplicatia; nu apare in lista de tinte.</summary>
    public int? ThisDeviceId { get; init; }

    /// <summary>Agentii folosesc HTTPS (certificat Tailscale). <c>false</c> = fallback HTTP in tailnet.</summary>
    public bool AgentsUseHttps { get; init; } = true;

    /// <summary>Cere amprenta/PIN-ul dispozitivului inainte de oprire si sleep.</summary>
    public bool RequireUserVerification { get; init; } = true;

    public bool IsConfigured => HubUrl is not null;

    /// <summary>
    /// Accepta <c>pihole.tailnet.ts.net</c>, <c>pihole.tailnet.ts.net:8443</c> sau URL complet.
    /// Implicit: HTTPS pe portul 8443. Hostul trebuie sa fie un nume MagicDNS.
    /// </summary>
    public static bool TryParseHubUrl(string? input, [NotNullWhen(true)] out Uri? hubUrl, [NotNullWhen(false)] out string? error)
    {
        hubUrl = null;
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Introdu adresa Hub-ului.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Adresa nu este valida (ex. https://pihole.tailnet-1234.ts.net:8443).";
            return false;
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Adresa Hub-ului nu trebuie sa contina cale, parametri sau utilizator.";
            return false;
        }

        if (!DeviceValidation.TryNormalizeHost(uri.Host, [DeviceValidation.DefaultHostSuffix], out var host))
        {
            error = $"Foloseste numele MagicDNS complet al Hub-ului (se termina in {DeviceValidation.DefaultHostSuffix}).";
            return false;
        }

        var port = uri.IsDefaultPort && !input!.Contains($":{uri.Port}", StringComparison.Ordinal) ? HubRoutes.DefaultPort : uri.Port;
        hubUrl = new UriBuilder(uri.Scheme, host, port).Uri;
        return true;
    }
}

public interface IClientSettingsStore
{
    ClientSettings Load();

    void Save(ClientSettings settings);
}

public sealed class InMemoryClientSettingsStore(ClientSettings? initial = null) : IClientSettingsStore
{
    private ClientSettings _settings = initial ?? new ClientSettings();

    public ClientSettings Load() => _settings;

    public void Save(ClientSettings settings) => _settings = settings;
}
