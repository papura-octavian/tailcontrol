using System.Text.Json;

namespace TailControl.Agent;

public sealed record AuditEntry(
    DateTimeOffset TimeUtc,
    string ClientId,
    string ClientLabel,
    string? RemoteIp,
    string Command,
    string Result,
    string? Detail);

/// <summary>
/// Jurnal de audit pe agent: cine, ce, cand, cu ce rezultat. Fisier JSON Lines (un obiect
/// per linie), deci valorile nu pot injecta linii false. Rotatie simpla la 5 MB.
/// </summary>
public sealed class AuditLog(string path, long maxBytes = 5 * 1024 * 1024)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public void Write(AuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var info = new FileInfo(Path);
            if (info.Exists && info.Length + line.Length > maxBytes)
            {
                File.Move(Path, Path + ".1", overwrite: true);
            }

            File.AppendAllText(Path, line);
        }
    }

    public IReadOnlyList<AuditEntry> ReadAll()
    {
        lock (_lock)
        {
            return File.Exists(Path)
                ? [.. File.ReadAllLines(Path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AuditEntry>(l, JsonOptions)!)]
                : [];
        }
    }
}
