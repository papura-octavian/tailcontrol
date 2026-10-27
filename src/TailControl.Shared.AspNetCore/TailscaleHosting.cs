using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace TailControl.Shared.AspNetCore;

/// <summary>Unde si cum asculta un server TailControl (Agent sau Hub).</summary>
public sealed record TailscaleListenSettings(
    int Port,
    IReadOnlyList<string> ListenAddresses,
    int TailscaleWaitSeconds,
    string? CertificatePath,
    string? CertificateKeyPath)
{
    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificatePath);
}

/// <summary>Configurarea retelei comuna Agent/Hub: doar adrese Tailscale, HTTPS cu certificatul Tailscale.</summary>
public static class TailscaleHosting
{
    public static void ValidateConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        // Endpoint-urile Kestrel din config ar putea deschide alte adrese (ex. 0.0.0.0).
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException("Sectiunea Kestrel:Endpoints nu este permisa; adresele sunt alese de server.");
        }

        // Hostul face parte din semnatura; serverul trebuie sa accepte doar propriul nume MagicDNS.
        var allowedHosts = configuration["AllowedHosts"];
        if (!isDevelopment && (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(h => h.Trim() == "*")))
        {
            throw new InvalidOperationException(
                "AllowedHosts trebuie setat la numele MagicDNS al acestui dispozitiv (ex. pc.tailnet-1234.ts.net), fara '*'.");
        }
    }

    public static async Task<IReadOnlyList<IPAddress>> ResolveListenAddressesAsync(TailscaleListenSettings settings, bool isDevelopment)
    {
        IReadOnlyList<IPAddress> addresses = settings.ListenAddresses.Count > 0
            ? [.. settings.ListenAddresses.Select(IPAddress.Parse)]
            : await TailscaleNetwork.WaitForLocalAddressesAsync(
                TimeSpan.FromSeconds(settings.TailscaleWaitSeconds), TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nicio adresa Tailscale gasita dupa {settings.TailscaleWaitSeconds} s. Este Tailscale pornit si conectat?");
        }

        TailscaleNetwork.ValidateListenAddresses(addresses, allowLoopback: isDevelopment);
        return addresses;
    }

    public static void ConfigureKestrel(KestrelServerOptions kestrel, TailscaleListenSettings settings, IReadOnlyList<IPAddress> addresses)
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = 64 * 1024;
        kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;

        var certificates = settings.HasCertificate
            ? new PemCertificateProvider(settings.CertificatePath!, settings.CertificateKeyPath!)
            : null;

        // Incarcam o data la pornire ca un certificat lipsa sa opreasca serviciul imediat, cu mesaj clar.
        certificates?.GetCertificate();

        foreach (var address in addresses)
        {
            kestrel.Listen(address, settings.Port, listen =>
            {
                if (certificates is not null)
                {
                    listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.GetCertificate());
                }
            });
        }
    }
}
