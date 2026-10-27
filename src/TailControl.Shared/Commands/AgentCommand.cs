using System.Collections.Frozen;

namespace TailControl.Shared.Commands;

/// <summary>
/// Lista fixa de comenzi pe care un agent le poate executa. Clientul trimite doar
/// numele unei comenzi din aceasta lista, niciodata text de executat.
/// Valoarea 0 nu este o comanda valida, ca un enum neinitializat sa nu insemne nimic.
/// </summary>
public enum AgentCommand
{
    Shutdown = 1,
    Lock = 2,
    Sleep = 3,
    CancelShutdown = 4,
}

/// <summary>
/// Maparea dintre numele din ruta (<c>/v1/commands/{nume}</c>) si <see cref="AgentCommand"/>.
/// Comparatia e exacta (ordinal, litere mici), iar valorile numerice nu sunt acceptate,
/// spre deosebire de <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>.
/// </summary>
public static class AgentCommands
{
    private static readonly FrozenDictionary<string, AgentCommand> ByName =
        new Dictionary<string, AgentCommand>(StringComparer.Ordinal)
        {
            ["shutdown"] = AgentCommand.Shutdown,
            ["lock"] = AgentCommand.Lock,
            ["sleep"] = AgentCommand.Sleep,
            ["cancel-shutdown"] = AgentCommand.CancelShutdown,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<AgentCommand, string> ByCommand =
        ByName.ToFrozenDictionary(kv => kv.Value, kv => kv.Key);

    public static IReadOnlyCollection<AgentCommand> All { get; } = [.. ByCommand.Keys.Order()];

    public static bool TryParse(string? routeName, out AgentCommand command)
    {
        if (routeName is not null && ByName.TryGetValue(routeName, out command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static string ToRouteName(AgentCommand command) =>
        ByCommand.TryGetValue(command, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(command), command, "Comanda necunoscuta.");
}
