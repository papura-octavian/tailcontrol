using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TailControl.Shared.AspNetCore;

/// <summary>
/// Adresele Tailscale: IPv4 100.64.0.0/10 (CGNAT) si IPv6 fd7a:115c:a1e0::/48.
/// Agentul si Hub-ul asculta doar pe aceste adrese, niciodata pe 0.0.0.0.
/// </summary>
public static class TailscaleNetwork
{
    private static readonly byte[] Ipv6Prefix = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    public static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && address.GetAddressBytes().AsSpan(0, Ipv6Prefix.Length).SequenceEqual(Ipv6Prefix);
    }

    /// <summary>Adresele Tailscale ale interfetelor active de pe aceasta masina (IPv4 intai).</summary>
    public static IReadOnlyList<IPAddress> FindLocalAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(IsTailscaleAddress)
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

    /// <summary>
    /// Asteapta ca Tailscale sa porneasca (la boot serviciul poate porni inaintea lui).
    /// Intoarce lista goala daca nu apare nicio adresa in timpul dat.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> WaitForLocalAddressesAsync(
        TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = FindLocalAddresses();
            if (found.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return found;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Valideaza adresele pe care serverul ar asculta. Loopback e permis doar pentru dezvoltare.
    /// Adresele "orice" (0.0.0.0, ::) si cele din LAN sunt refuzate.
    /// </summary>
    public static void ValidateListenAddresses(IEnumerable<IPAddress> addresses, bool allowLoopback)
    {
        var list = addresses.ToList();
        if (list.Count == 0)
        {
            throw new InvalidOperationException("Nu exista nicio adresa pe care sa ascultam.");
        }

        foreach (var address in list)
        {
            var ok = IsTailscaleAddress(address) || (allowLoopback && IPAddress.IsLoopback(address));
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"Adresa {address} nu este o adresa Tailscale (100.64.0.0/10 sau fd7a:115c:a1e0::/48).");
            }
        }
    }
}
