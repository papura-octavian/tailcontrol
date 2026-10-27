using System.Net;
using System.Net.Sockets;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Contracts;
using TailControl.Shared.Validation;

namespace TailControl.Hub;

/// <summary>
/// Configurarea Hub-ului (sectiunea <c>Hub</c>). Pe Pi, valorile specifice retelei stau in
/// <c>/etc/tailcontrol/hub/hub.json</c> (vezi <c>deploy/hub</c>), nu in folderul aplicatiei.
/// </summary>
public sealed class HubOptions
{
    public const string SectionName = "Hub";

    public int Port { get; set; } = HubRoutes.DefaultPort;

    public List<string> ListenAddresses { get; set; } = [];

    public int TailscaleWaitSeconds { get; set; } = 300;

    public string? CertificatePath { get; set; }

    public string? CertificateKeyPath { get; set; }

    /// <summary>Scriibil doar de root: clienti autorizati, certificat. Relativ = fata de folderul aplicatiei.</summary>
    public string ConfigDirectory { get; set; } = "/etc/tailcontrol/hub";

    /// <summary>Baza de date SQLite (inventar + audit). Pe Pi: StateDirectory din systemd.</summary>
    public string DatabasePath { get; set; } = "/var/lib/tailcontrol-hub/hub.db";

    /// <summary>Adresa de broadcast a LAN-ului pentru Wake-on-LAN (ex. 192.168.1.255). Gol = wake dezactivat.</summary>
    public string? WakeBroadcastAddress { get; set; }

    public int WakePort { get; set; } = 9;

    public int StatusIntervalSeconds { get; set; } = 15;

    public int StatusTimeoutMilliseconds { get; set; } = 1500;

    /// <summary>Sufixele permise pentru hostul unui dispozitiv (implicit doar MagicDNS).</summary>
    public List<string> AllowedDeviceHostSuffixes { get; set; } = [];

    public int AuditMaxEntries { get; set; } = 10_000;

    public string AuthorizedClientsPath => Path.Combine(ResolvedConfigDirectory, "authorized-clients.json");

    public string ResolvedDatabasePath => Path.GetFullPath(DatabasePath, AppContext.BaseDirectory);

    public IReadOnlyCollection<string> HostSuffixes =>
        AllowedDeviceHostSuffixes.Count > 0 ? AllowedDeviceHostSuffixes : [DeviceValidation.DefaultHostSuffix];

    public IPAddress? ParsedWakeBroadcast =>
        string.IsNullOrWhiteSpace(WakeBroadcastAddress) ? null : IPAddress.Parse(WakeBroadcastAddress);

    private string ResolvedConfigDirectory => Path.GetFullPath(ConfigDirectory, AppContext.BaseDirectory);

    public static HubOptions Load(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<HubOptions>() ?? new HubOptions();
        options.Validate();
        return options;
    }

    public TailscaleListenSettings ToListenSettings() =>
        new(Port, ListenAddresses, TailscaleWaitSeconds, CertificatePath, CertificateKeyPath);

    /// <summary>
    /// Contextul pentru <c>authorize-client</c> / <c>revoke-client</c> / <c>list-clients</c>.
    /// Fisierul e 0640: root scrie, grupul serviciului (setgid pe folder, vezi install.sh) citeste.
    /// </summary>
    public AuthorizedClientsCliContext ToCliContext(bool isDevelopment) => new()
    {
        ProgramName = "sudo /opt/tailcontrol-hub/TailControl.Hub",
        AuthorizedClientsPath = AuthorizedClientsPath,
        RequireElevation = !isDevelopment,
        SupportsAdminFlag = true,
        SecureFile = path =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
        },
    };

    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Hub:Port trebuie sa fie intre 1 si 65535.");
        }

        if (StatusIntervalSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException("Hub:StatusIntervalSeconds trebuie sa fie intre 5 si 300.");
        }

        if (StatusTimeoutMilliseconds is < 200 or > 10_000)
        {
            throw new InvalidOperationException("Hub:StatusTimeoutMilliseconds trebuie sa fie intre 200 si 10000.");
        }

        if (WakePort is not (7 or 9))
        {
            throw new InvalidOperationException("Hub:WakePort trebuie sa fie 7 sau 9.");
        }

        if (AuditMaxEntries is < 100 or > 1_000_000)
        {
            throw new InvalidOperationException("Hub:AuditMaxEntries trebuie sa fie intre 100 si 1000000.");
        }

        if (string.IsNullOrWhiteSpace(CertificatePath) != string.IsNullOrWhiteSpace(CertificateKeyPath))
        {
            throw new InvalidOperationException("Hub:CertificatePath si Hub:CertificateKeyPath se configureaza impreuna.");
        }

        if (!string.IsNullOrWhiteSpace(WakeBroadcastAddress) && !IsAllowedBroadcast(WakeBroadcastAddress))
        {
            throw new InvalidOperationException(
                "Hub:WakeBroadcastAddress trebuie sa fie adresa de broadcast a LAN-ului (IPv4 privat, ex. 192.168.1.255) sau 255.255.255.255.");
        }

        if (AllowedDeviceHostSuffixes.Any(s => string.IsNullOrWhiteSpace(s) || s.Trim('.').Split('.').Length < 2))
        {
            throw new InvalidOperationException("Hub:AllowedDeviceHostSuffixes trebuie sa contina domenii complete (ex. ts.net).");
        }
    }

    /// <summary>Magic packet-ul pleaca doar in LAN-ul privat, niciodata spre o adresa publica.</summary>
    internal static bool IsAllowedBroadcast(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        return isPrivate && b[3] == 255;
    }
}
