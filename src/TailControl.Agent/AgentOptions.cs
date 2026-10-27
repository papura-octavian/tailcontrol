using Microsoft.Extensions.Configuration;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Contracts;

namespace TailControl.Agent;

/// <summary>Configurarea agentului (sectiunea <c>Agent</c> din appsettings.json).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public const string ServiceName = "TailControlAgent";

    public int Port { get; set; } = AgentRoutes.DefaultPort;

    /// <summary>Adrese explicite. Daca lista e goala, se detecteaza adresele Tailscale.</summary>
    public List<string> ListenAddresses { get; set; } = [];

    /// <summary>Cat asteptam la pornire ca Tailscale sa aiba adresa.</summary>
    public int TailscaleWaitSeconds { get; set; } = 300;

    /// <summary>Logheaza comenzile in loc sa le execute. Activ implicit in Development.</summary>
    public bool DryRun { get; set; }

    public int ShutdownDelaySeconds { get; set; } = 10;

    public int SleepDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Radacina datelor. Contine <c>config\</c> (scriibil doar de administratori: clienti
    /// autorizati, certificat) si <c>logs\</c> (scriibil de serviciu: audit).
    /// O cale relativa e fata de folderul executabilului.
    /// </summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TailControl", "Agent");

    /// <summary>Certificat PEM de la <c>tailscale cert</c>. Fara el, agentul ruleaza HTTP (fallback documentat).</summary>
    public string? CertificatePath { get; set; }

    public string? CertificateKeyPath { get; set; }

    public string LockPipeName { get; set; } = LockPipeProtocol.PipeName;

    public string ConfigDirectory => Path.Combine(ResolvedDataDirectory, "config");

    public string LogDirectory => Path.Combine(ResolvedDataDirectory, "logs");

    public string AuthorizedClientsPath => Path.Combine(ConfigDirectory, "authorized-clients.json");

    public string AuditLogPath => Path.Combine(LogDirectory, "audit.log");

    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificatePath);

    public TailscaleListenSettings ToListenSettings() =>
        new(Port, ListenAddresses, TailscaleWaitSeconds, CertificatePath, CertificateKeyPath);

    /// <summary>Contextul pentru <c>authorize-client</c> / <c>revoke-client</c> / <c>list-clients</c>.</summary>
    public AuthorizedClientsCliContext ToCliContext(bool isDevelopment) => new()
    {
        ProgramName = "TailControl.Agent.exe",
        AuthorizedClientsPath = AuthorizedClientsPath,
        RequireElevation = !isDevelopment,
        PrepareConfigDirectory = isDevelopment
            ? path => Directory.CreateDirectory(path)
            : SecureDirectories.EnsureConfigDirectory,
    };

    private string ResolvedDataDirectory => Path.GetFullPath(DataDirectory, AppContext.BaseDirectory);

    /// <summary>
    /// Configurarea specifica masinii, scrisa de installer. Poate fi mutata cu variabila de
    /// mediu <c>TAILCONTROL_AGENT_CONFIG</c>.
    /// </summary>
    public static string SiteConfigPath() =>
        Environment.GetEnvironmentVariable("TAILCONTROL_AGENT_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TailControl", "Agent", "config", "agent.json");

    public static AgentOptions Load(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<AgentOptions>() ?? new AgentOptions();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Agent:Port trebuie sa fie intre 1 si 65535.");
        }

        if (ShutdownDelaySeconds is < 5 or > 600)
        {
            throw new InvalidOperationException("Agent:ShutdownDelaySeconds trebuie sa fie intre 5 si 600.");
        }

        if (SleepDelaySeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("Agent:SleepDelaySeconds trebuie sa fie intre 1 si 60.");
        }

        if (string.IsNullOrWhiteSpace(CertificatePath) != string.IsNullOrWhiteSpace(CertificateKeyPath))
        {
            throw new InvalidOperationException("Agent:CertificatePath si Agent:CertificateKeyPath se configureaza impreuna.");
        }
    }
}
