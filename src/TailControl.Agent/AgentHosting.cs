using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TailControl.Shared.AspNetCore;

namespace TailControl.Agent;

/// <summary>Configurarea retelei: doar adrese Tailscale, HTTPS cu certificatul Tailscale.</summary>
public static class AgentHosting
{
    public static void ValidateConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        // Endpoint-urile Kestrel din config ar putea deschide alte adrese (ex. 0.0.0.0).
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException("Sectiunea Kestrel:Endpoints nu este permisa; adresele sunt alese de agent.");
        }

        // Hostul face parte din semnatura; agentul trebuie sa accepte doar propriul nume MagicDNS.
        var allowedHosts = configuration["AllowedHosts"];
        if (!isDevelopment && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(h => h.Trim() == "*")))
        {
            throw new InvalidOperationException(
                "AllowedHosts trebuie setat la numele MagicDNS al acestui dispozitiv (ex. pc.tailnet-1234.ts.net), fara '*'.");
        }
    }

    public static async Task<IReadOnlyList<IPAddress>> ResolveListenAddressesAsync(AgentOptions options, bool isDevelopment)
    {
        IReadOnlyList<IPAddress> addresses = options.ListenAddresses.Count > 0
            ? [.. options.ListenAddresses.Select(IPAddress.Parse)]
            : await TailscaleNetwork.WaitForLocalAddressesAsync(
                TimeSpan.FromSeconds(options.TailscaleWaitSeconds), TimeSpan.FromSeconds(5), CancellationToken.None);

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nicio adresa Tailscale gasita dupa {options.TailscaleWaitSeconds} s. Este Tailscale pornit si conectat?");
        }

        TailscaleNetwork.ValidateListenAddresses(addresses, allowLoopback: isDevelopment);
        return addresses;
    }

    public static void ConfigureKestrel(KestrelServerOptions kestrel, AgentOptions options, IReadOnlyList<IPAddress> addresses)
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = 64 * 1024;
        kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;

        var certificates = options.HasCertificate
            ? new PemCertificateProvider(options.CertificatePath!, options.CertificateKeyPath!)
            : null;

        // Incarcam o data la pornire ca un certificat lipsa sa opreasca serviciul imediat, cu mesaj clar.
        certificates?.GetCertificate();

        foreach (var address in addresses)
        {
            kestrel.Listen(address, options.Port, listen =>
            {
                if (certificates is not null)
                {
                    listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.GetCertificate());
                }
            });
        }
    }
}
