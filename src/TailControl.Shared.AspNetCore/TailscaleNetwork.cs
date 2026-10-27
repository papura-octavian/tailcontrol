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
            .Where(n => IsUsable(n.OperationalStatus))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(IsTailscaleAddress)
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

    /// <summary>
    /// Pe Linux, interfetele tun (inclusiv <c>tailscale0</c>) raporteaza starea <c>UNKNOWN</c>,
    /// nu <c>UP</c>, chiar cand functioneaza. Excludem doar interfetele oprite.
    /// </summary>
    public static bool IsUsable(OperationalStatus status) =>
        status is OperationalStatus.Up or OperationalStatus.Unknown;

    /// <summary>
    /// Asteapta ca Tailscale sa porneasca (la boot serviciul poate porni inaintea lui).
    /// Intoarce lista goala daca nu apare nicio adresa in timpul dat.
    /// <paramref name="onWaiting"/> e apelat o data, cand incepe asteptarea (inainte sa existe logger).
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> WaitForLocalAddressesAsync(
        TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken, Action<string>? onWaiting = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;
        while (true)
        {
            var found = FindLocalAddresses();
            if (found.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return found;
            }

            if (!announced)
            {
                onWaiting?.Invoke($"Nicio adresa Tailscale inca; astept pana la {timeout.TotalSeconds:0} s...");
                announced = true;
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
