using TailControl.Shared.Security;

namespace TailControl.Shared.AspNetCore;

/// <summary>Ce difera intre Agent si Hub la administrarea clientilor autorizati.</summary>
public sealed class AuthorizedClientsCliContext
{
    /// <summary>Cum se apeleaza programul, pentru mesajul de ajutor.</summary>
    public required string ProgramName { get; init; }

    public required string AuthorizedClientsPath { get; init; }

    /// <summary>In productie comenzile cer administrator / root.</summary>
    public required bool RequireElevation { get; init; }

    /// <summary>Flag-ul <c>--admin</c> are sens doar pe Hub (modificarea inventarului).</summary>
    public bool SupportsAdminFlag { get; init; }

    /// <summary>Creeaza folderul de configurare cu drepturile potrivite platformei.</summary>
    public Action<string> PrepareConfigDirectory { get; init; } = path => Directory.CreateDirectory(path);

    /// <summary>Aplica drepturile pe fisier dupa scriere (ex. 0640 pe Linux).</summary>
    public Action<string> SecureFile { get; init; } = _ => { };
}

/// <summary>
/// Comenzi locale de administrare, comune Agent/Hub. Sunt singura cale prin care se
/// modifica lista de clienti autorizati: nu exista API pentru asta.
/// </summary>
public static class AuthorizedClientsCli
{
    private static readonly string[] Commands = ["authorize-client", "revoke-client", "list-clients", "help"];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>Intoarce codul de iesire al procesului.</summary>
    public static int Run(string[] args, AuthorizedClientsCliContext context, TextWriter output, TimeProvider timeProvider)
    {
        var command = args[0].ToLowerInvariant();
        if (command == "help")
        {
            PrintUsage(context, output);
            return 0;
        }

        if (context.RequireElevation && !Environment.IsPrivilegedProcess)
        {
            output.WriteLine(OperatingSystem.IsWindows()
                ? "Eroare: comanda trebuie rulata ca administrator."
                : "Eroare: comanda trebuie rulata cu sudo.");
            return 3;
        }

        var parsed = ParseOptions(args.AsSpan(1));
        if (parsed is null)
        {
            PrintUsage(context, output);
            return 2;
        }

        try
        {
            return command switch
            {
                "authorize-client" => Authorize(parsed, context, output, timeProvider),
                "revoke-client" => Revoke(parsed, context, output, timeProvider),
                _ => List(context, output),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Eroare: {ex.Message}");
            return 1;
        }
    }

    private static int Authorize(Dictionary<string, string?> parsed, AuthorizedClientsCliContext context, TextWriter output, TimeProvider timeProvider)
    {
        if (!parsed.TryGetValue("key", out var key) || key is null || !parsed.TryGetValue("label", out var label) || label is null)
        {
            output.WriteLine("Eroare: sunt necesare --key si --label.");
            return 2;
        }

        var isAdmin = parsed.ContainsKey("admin");
        if (isAdmin && !context.SupportsAdminFlag)
        {
            output.WriteLine("Eroare: --admin se foloseste doar pe Hub.");
            return 2;
        }

        context.PrepareConfigDirectory(Path.GetDirectoryName(Path.GetFullPath(context.AuthorizedClientsPath))!);

        var list = AuthorizedClientsFile.LoadOrEmpty(context.AuthorizedClientsPath)
            .Authorize(key, label, isAdmin, timeProvider.GetUtcNow(), out var added);
        Save(context, list);

        output.WriteLine($"Client autorizat: {added.Label} ({added.Id}){(added.IsAdmin ? ", admin" : "")}.");
        output.WriteLine($"Fisier: {context.AuthorizedClientsPath}");
        return 0;
    }

    private static int Revoke(Dictionary<string, string?> parsed, AuthorizedClientsCliContext context, TextWriter output, TimeProvider timeProvider)
    {
        if (!parsed.TryGetValue("id", out var id) || id is null)
        {
            output.WriteLine("Eroare: este necesar --id.");
            return 2;
        }

        var list = AuthorizedClientsFile.LoadOrEmpty(context.AuthorizedClientsPath).Revoke(id, timeProvider.GetUtcNow());
        Save(context, list);

        output.WriteLine($"Client revocat: {id}. Efectul e imediat, fara repornirea serviciului.");
        return 0;
    }

    private static int List(AuthorizedClientsCliContext context, TextWriter output)
    {
        var clients = AuthorizedClientsFile.LoadOrEmpty(context.AuthorizedClientsPath).Clients;
        if (clients.Count == 0)
        {
            output.WriteLine("Niciun client autorizat.");
            return 0;
        }

        foreach (var c in clients)
        {
            var state = c.IsRevoked ? $"REVOCAT {c.RevokedUtc:yyyy-MM-dd HH:mm}" : "activ";
            var admin = context.SupportsAdminFlag && c.IsAdmin ? "  admin" : "";
            output.WriteLine($"{c.Id}  {c.Label,-20}  adaugat {c.AddedUtc:yyyy-MM-dd HH:mm}  {state}{admin}");
        }

        return 0;
    }

    private static void Save(AuthorizedClientsCliContext context, AuthorizedClientList list)
    {
        AuthorizedClientsFile.Save(context.AuthorizedClientsPath, list);
        context.SecureFile(Path.GetFullPath(context.AuthorizedClientsPath));
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

    private static void PrintUsage(AuthorizedClientsCliContext context, TextWriter output)
    {
        var admin = context.SupportsAdminFlag ? " [--admin]" : "";
        output.WriteLine("Utilizare:");
        output.WriteLine($"  {context.ProgramName} authorize-client --key <cheie publica base64> --label \"Telefon\"{admin}");
        output.WriteLine($"  {context.ProgramName} revoke-client --id <clientId>");
        output.WriteLine($"  {context.ProgramName} list-clients");
    }
}
