using System.Security.Principal;
using TailControl.Shared.Security;

namespace TailControl.Agent;

/// <summary>
/// Comenzi locale de administrare. Ruleaza doar pe masina agentului, ca administrator,
/// si sunt singura cale prin care se modifica lista de clienti autorizati (nu exista API).
/// <code>
/// TailControl.Agent.exe authorize-client --key &lt;cheie publica base64&gt; --label "Telefon"
/// TailControl.Agent.exe revoke-client --id &lt;clientId&gt;
/// TailControl.Agent.exe list-clients
/// </code>
/// </summary>
public static class AdminCli
{
    private static readonly string[] Commands = ["authorize-client", "revoke-client", "list-clients", "help"];

    public static bool IsAdminCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>Intoarce codul de iesire al procesului.</summary>
    public static int Run(string[] args, AgentOptions options, bool isDevelopment, TextWriter output, TimeProvider timeProvider)
    {
        var command = args[0].ToLowerInvariant();
        if (command == "help")
        {
            PrintUsage(output);
            return 0;
        }

        if (!isDevelopment && !IsElevated())
        {
            output.WriteLine("Eroare: comanda trebuie rulata ca administrator.");
            return 3;
        }

        var parsed = ParseOptions(args.AsSpan(1));
        if (parsed is null)
        {
            PrintUsage(output);
            return 2;
        }

        try
        {
            return command switch
            {
                "authorize-client" => Authorize(parsed, options, isDevelopment, output, timeProvider),
                "revoke-client" => Revoke(parsed, options, output, timeProvider),
                _ => List(options, output),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
        {
            output.WriteLine($"Eroare: {ex.Message}");
            return 1;
        }
    }

    private static int Authorize(Dictionary<string, string?> parsed, AgentOptions options, bool isDevelopment, TextWriter output, TimeProvider timeProvider)
    {
        if (!parsed.TryGetValue("key", out var key) || key is null || !parsed.TryGetValue("label", out var label) || label is null)
        {
            output.WriteLine("Eroare: sunt necesare --key si --label.");
            return 2;
        }

        if (!isDevelopment)
        {
            SecureDirectories.EnsureConfigDirectory(options.ConfigDirectory);
        }
        else
        {
            Directory.CreateDirectory(options.ConfigDirectory);
        }

        var list = AuthorizedClientsFile.LoadOrEmpty(options.AuthorizedClientsPath)
            .Authorize(key, label, parsed.ContainsKey("admin"), timeProvider.GetUtcNow(), out var added);
        AuthorizedClientsFile.Save(options.AuthorizedClientsPath, list);

        output.WriteLine($"Client autorizat: {added.Label} ({added.Id}).");
        output.WriteLine($"Fisier: {options.AuthorizedClientsPath}");
        return 0;
    }

    private static int Revoke(Dictionary<string, string?> parsed, AgentOptions options, TextWriter output, TimeProvider timeProvider)
    {
        if (!parsed.TryGetValue("id", out var id) || id is null)
        {
            output.WriteLine("Eroare: este necesar --id.");
            return 2;
        }

        var list = AuthorizedClientsFile.LoadOrEmpty(options.AuthorizedClientsPath).Revoke(id, timeProvider.GetUtcNow());
        AuthorizedClientsFile.Save(options.AuthorizedClientsPath, list);

        output.WriteLine($"Client revocat: {id}. Efectul e imediat, fara repornirea serviciului.");
        return 0;
    }

    private static int List(AgentOptions options, TextWriter output)
    {
        var clients = AuthorizedClientsFile.LoadOrEmpty(options.AuthorizedClientsPath).Clients;
        if (clients.Count == 0)
        {
            output.WriteLine("Niciun client autorizat.");
            return 0;
        }

        foreach (var c in clients)
        {
            var state = c.IsRevoked ? $"REVOCAT {c.RevokedUtc:yyyy-MM-dd HH:mm}" : "activ";
            output.WriteLine($"{c.Id}  {c.Label,-20}  adaugat {c.AddedUtc:yyyy-MM-dd HH:mm}  {state}");
        }

        return 0;
    }

    // --nume valoare, sau --nume fara valoare pentru flag-uri (ex. --admin).
    private static Dictionary<string, string?>? ParseOptions(ReadOnlySpan<string> args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length < 3)
            {
                return null;
            }

            var name = args[i][2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            result[name] = value;
        }

        return result;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Utilizare (ca administrator):");
        output.WriteLine("  TailControl.Agent.exe authorize-client --key <cheie publica base64> --label \"Telefon\"");
        output.WriteLine("  TailControl.Agent.exe revoke-client --id <clientId>");
        output.WriteLine("  TailControl.Agent.exe list-clients");
    }
}
