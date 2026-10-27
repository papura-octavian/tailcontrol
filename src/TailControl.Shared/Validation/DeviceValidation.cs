using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using TailControl.Shared.Contracts;

namespace TailControl.Shared.Validation;

/// <summary>
/// Validarea datelor despre dispozitive. Aceleasi reguli pe Hub (autoritar) si in client
/// (pentru mesaje imediate in formular).
/// </summary>
public static class DeviceValidation
{
    public const int MaxNameLength = 40;
    public const int MaxHostLength = 253;

    /// <summary>Numele MagicDNS se termina in <c>.ts.net</c>.</summary>
    public const string DefaultHostSuffix = ".ts.net";

    /// <summary>
    /// Accepta <c>AA:BB:CC:DD:EE:FF</c>, <c>aa-bb-cc-dd-ee-ff</c> sau <c>aabbccddeeff</c> si intoarce
    /// forma normalizata <c>AA:BB:CC:DD:EE:FF</c>. Respinge adresele multicast/broadcast si zero.
    /// </summary>
    public static bool TryNormalizeMac(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        string hex;
        if (text.Length == 17 && (text.Count(c => c == ':') == 5 || text.Count(c => c == '-') == 5))
        {
            var separator = text[2];
            for (var i = 2; i < 17; i += 3)
            {
                if (text[i] != separator)
                {
                    return false;
                }
            }

            hex = text.Replace(separator.ToString(), "", StringComparison.Ordinal);
        }
        else if (text.Length == 12)
        {
            hex = text;
        }
        else
        {
            return false;
        }

        if (!hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var bytes = Convert.FromHexString(hex);
        var isMulticast = (bytes[0] & 0x01) != 0;
        if (isMulticast || bytes.All(b => b == 0))
        {
            return false;
        }

        normalized = string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        return true;
    }

    public static byte[] MacToBytes(string normalizedMac) =>
        Convert.FromHexString(normalizedMac.Replace(":", "", StringComparison.Ordinal));

    /// <summary>
    /// Nume DNS complet, cu litere mici, care se termina intr-unul din sufixele permise (implicit
    /// <c>.ts.net</c>). Adresele IP nu sunt acceptate: clientul are nevoie de numele MagicDNS
    /// pentru certificatul HTTPS si pentru semnatura.
    /// </summary>
    public static bool TryNormalizeHost(string? value, IReadOnlyCollection<string> allowedSuffixes, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var host = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length > MaxHostLength || IPAddress.TryParse(host, out _))
        {
            return false;
        }

        var labels = host.Split('.');
        if (labels.Length < 2 || !labels.All(IsValidLabel))
        {
            return false;
        }

        if (!allowedSuffixes.Any(s => host.EndsWith(s.StartsWith('.') ? s : "." + s, StringComparison.Ordinal)))
        {
            return false;
        }

        normalized = host;
        return true;
    }

    private static bool IsValidLabel(string label) =>
        label.Length is >= 1 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= MaxNameLength
        && !name.Any(char.IsControl);

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>Valideaza si normalizeaza o cerere. Erorile sunt pe camp, in romana fara diacritice.</summary>
    public static Dictionary<string, string[]> Validate(
        DeviceRequest request,
        IReadOnlyCollection<string> allowedHostSuffixes,
        out DeviceRequest normalized)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        normalized = request;

        if (!IsValidName(request.Name))
        {
            errors["name"] = [$"Numele trebuie sa aiba 1-{MaxNameLength} caractere."];
        }

        string? mac = null;
        if (!string.IsNullOrWhiteSpace(request.MacAddress) && !TryNormalizeMac(request.MacAddress, out mac))
        {
            errors["macAddress"] = ["Adresa MAC nu este valida (ex. AA:BB:CC:DD:EE:FF)."];
        }

        if (!TryNormalizeHost(request.Host, allowedHostSuffixes, out var host))
        {
            errors["host"] = [$"Hostul trebuie sa fie numele MagicDNS complet (ex. pc.tailnet-1234{DefaultHostSuffix})."];
        }

        var port = request.AgentPort ?? AgentRoutes.DefaultPort;
        if (!IsValidPort(port))
        {
            errors["agentPort"] = ["Portul trebuie sa fie intre 1 si 65535."];
        }

        if (!Enum.IsDefined(request.Kind))
        {
            errors["kind"] = ["Tip de dispozitiv necunoscut."];
        }

        if (errors.Count == 0)
        {
            normalized = new DeviceRequest(request.Name.Trim(), mac, host!, port, request.Kind);
        }

        return errors;
    }
}
