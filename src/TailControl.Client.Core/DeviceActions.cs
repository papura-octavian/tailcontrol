using System.Text;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;

namespace TailControl.Client.Core;

/// <summary>Ce butoane sunt active pentru un dispozitiv, in functie de status.</summary>
public sealed record DeviceActionState(
    bool CanWake,
    bool CanShutdown,
    bool CanCancelShutdown,
    bool CanLock,
    bool CanSleep,
    string? WakeHint);

public static class DeviceActions
{
    public static DeviceActionState Evaluate(DeviceDto device, AgentStatusResponse? agentStatus, DateTimeOffset now)
    {
        var online = device.Online;
        var shutdownPending = online && agentStatus?.ShutdownScheduledForUtc > now;

        string? wakeHint = null;
        if (!online && device.MacAddress is null)
        {
            wakeHint = "Adauga adresa MAC ca sa poti porni dispozitivul.";
        }
        else if (!online && device.Kind == DeviceKind.Laptop)
        {
            wakeHint = "Laptopurile pornesc de obicei doar din sleep, nu din starea oprit.";
        }

        return new DeviceActionState(
            CanWake: !online && device.MacAddress is not null,
            CanShutdown: online && !shutdownPending,
            CanCancelShutdown: shutdownPending,
            CanLock: online,
            CanSleep: online && !shutdownPending,
            WakeHint: wakeHint);
    }

    /// <summary>Comenzile care opresc dispozitivul cer amprenta/PIN-ul (daca setarea e activa).</summary>
    public static bool RequiresUserVerification(AgentCommand command) =>
        command is AgentCommand.Shutdown or AgentCommand.Sleep;

    public static string ConfirmationText(AgentCommand command, string deviceName) => command switch
    {
        AgentCommand.Shutdown => $"Opresti {deviceName}? Aplicatiile deschise vor fi inchise fortat dupa 10 secunde.",
        AgentCommand.Sleep => $"Pui {deviceName} in sleep?",
        AgentCommand.Lock => $"Blochezi sesiunea pe {deviceName}?",
        AgentCommand.CancelShutdown => $"Anulezi oprirea lui {deviceName}?",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>Lista de tinte: fara dispozitivul curent; cele online primele, apoi dupa nume.</summary>
    public static IReadOnlyList<DeviceDto> VisibleTargets(IEnumerable<DeviceDto> devices, int? thisDeviceId) =>
        [.. devices
            .Where(d => d.Id != thisDeviceId)
            .OrderByDescending(d => d.Online)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Text scurt pentru "vazut ultima data", ex. "acum 3 min".</summary>
    public static string LastSeenText(DeviceDto device, DateTimeOffset now)
    {
        if (device.Online)
        {
            return "Online";
        }

        if (device.LastSeenUtc is not { } seen)
        {
            return "Offline, nu a fost vazut inca";
        }

        var ago = now - seen;
        return ago switch
        {
            { TotalMinutes: < 1 } => "Offline, vazut acum cateva secunde",
            { TotalHours: < 1 } => $"Offline, vazut acum {(int)ago.TotalMinutes} min",
            { TotalDays: < 1 } => $"Offline, vazut acum {(int)ago.TotalHours} h",
            _ => $"Offline, vazut acum {(int)ago.TotalDays} zile",
        };
    }
}

/// <summary>Comenzile de autorizare pe care utilizatorul le ruleaza pe agent / Hub (pairing).</summary>
public static class AuthorizationInstructions
{
    public static string SafeLabel(string? deviceName)
    {
        var text = new StringBuilder();
        foreach (var c in deviceName ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')
            {
                text.Append(c);
            }
        }

        var label = text.ToString().Trim();
        return label.Length == 0 ? "Client" : label[..Math.Min(label.Length, 40)];
    }

    public static string ForAgent(string publicKey, string label) =>
        $"TailControl.Agent.exe authorize-client --key {publicKey} --label \"{SafeLabel(label)}\"";

    public static string ForHub(string publicKey, string label) =>
        $"sudo /opt/tailcontrol-hub/TailControl.Hub authorize-client --key {publicKey} --label \"{SafeLabel(label)}\" --admin";
}
