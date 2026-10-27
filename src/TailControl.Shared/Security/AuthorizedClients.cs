using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailControl.Shared.Security;

/// <summary>Un client autorizat sa trimita cereri catre un agent sau catre Hub.</summary>
public sealed record AuthorizedClient
{
    public required string Id { get; init; }

    /// <summary>Cheia publica ECDSA P-256, SPKI base64.</summary>
    public required string PublicKey { get; init; }

    public required string Label { get; init; }

    public required DateTimeOffset AddedUtc { get; init; }

    /// <summary>Poate modifica inventarul pe Hub. Agentii il ignora.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>Daca e setat, clientul este revocat si toate cererile lui sunt respinse.</summary>
    public DateTimeOffset? RevokedUtc { get; init; }

    [JsonIgnore]
    public bool IsRevoked => RevokedUtc is not null;
}

/// <summary>Un client autorizat, cu cheia publica deja decodata si validata.</summary>
public sealed record AuthorizedClientEntry(AuthorizedClient Client, byte[] SubjectPublicKeyInfo);

public interface IAuthorizedClientStore
{
    AuthorizedClientEntry? Find(string clientId);
}

/// <summary>
/// Lista de clienti autorizati, serializata ca JSON. Se modifica doar prin comenzi
/// locale de administrare (<c>authorize-client</c> / <c>revoke-client</c>), niciodata prin API.
/// </summary>
public sealed class AuthorizedClientList
{
    public const int MaxLabelLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Fisier citit de admin, nu HTML: cheile base64 raman lizibile (fara +).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, AuthorizedClientEntry> _byId;

    private AuthorizedClientList(Dictionary<string, AuthorizedClientEntry> byId) => _byId = byId;

    public static AuthorizedClientList Empty { get; } = new([]);

    public IReadOnlyCollection<AuthorizedClient> Clients => [.. _byId.Values.Select(e => e.Client).OrderBy(c => c.AddedUtc)];

    public AuthorizedClientEntry? Find(string clientId) => _byId.GetValueOrDefault(clientId);

    /// <summary>
    /// Citeste lista. Orice intrare invalida (cheie gresita, id care nu corespunde cheii,
    /// duplicate) invalideaza tot fisierul: un fisier modificat gresit nu trebuie sa
    /// autorizeze pe nimeni.
    /// </summary>
    public static AuthorizedClientList Parse(string json)
    {
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Fisierul cu clienti autorizati nu este JSON valid.", ex);
        }

        if (model is null || model.Version != FileModel.CurrentVersion)
        {
            throw new InvalidDataException("Versiune necunoscuta a fisierului cu clienti autorizati.");
        }

        var byId = new Dictionary<string, AuthorizedClientEntry>(StringComparer.Ordinal);
        foreach (var client in model.Clients)
        {
            if (!ClientKeys.TryParsePublicKey(client.PublicKey, out var spki, out var id))
            {
                throw new InvalidDataException($"Cheie publica invalida pentru clientul '{client.Id}'.");
            }

            if (!string.Equals(id, client.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Id-ul '{client.Id}' nu corespunde cheii publice.");
            }

            ValidateLabel(client.Label);

            if (!byId.TryAdd(id, new AuthorizedClientEntry(client, spki)))
            {
                throw new InvalidDataException($"Clientul '{client.Id}' apare de doua ori.");
            }
        }

        return new AuthorizedClientList(byId);
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new FileModel { Clients = [.. Clients] }, JsonOptions);

    public AuthorizedClientList Authorize(string publicKeyBase64, string label, bool isAdmin, DateTimeOffset now, out AuthorizedClient added)
    {
        if (!ClientKeys.TryParsePublicKey(publicKeyBase64, out var spki, out var id))
        {
            throw new ArgumentException("Cheia publica nu este o cheie ECDSA P-256 valida (SPKI base64).", nameof(publicKeyBase64));
        }

        ValidateLabel(label);

        if (_byId.ContainsKey(id))
        {
            throw new InvalidOperationException($"Clientul '{id}' exista deja in lista.");
        }

        added = new AuthorizedClient
        {
            Id = id,
            PublicKey = Convert.ToBase64String(spki),
            Label = label,
            AddedUtc = now,
            IsAdmin = isAdmin,
        };

        return new AuthorizedClientList(new(_byId, StringComparer.Ordinal) { [id] = new AuthorizedClientEntry(added, spki) });
    }

    public AuthorizedClientList Revoke(string clientId, DateTimeOffset now)
    {
        if (!_byId.TryGetValue(clientId, out var entry))
        {
            throw new InvalidOperationException($"Clientul '{clientId}' nu exista in lista.");
        }

        if (entry.Client.IsRevoked)
        {
            return this;
        }

        var revoked = entry with { Client = entry.Client with { RevokedUtc = now } };
        return new AuthorizedClientList(new(_byId, StringComparer.Ordinal) { [clientId] = revoked });
    }

    private static void ValidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)
            || label.Length > MaxLabelLength
            || label.Any(char.IsControl))
        {
            throw new InvalidDataException($"Eticheta trebuie sa aiba 1-{MaxLabelLength} caractere, fara caractere de control.");
        }
    }

    private sealed class FileModel
    {
        public const int CurrentVersion = 1;

        public int Version { get; init; } = CurrentVersion;

        public List<AuthorizedClient> Clients { get; init; } = [];
    }
}

/// <summary>
/// Lista de clienti autorizati citita dintr-un fisier. Fisierul e recitit automat
/// cand se schimba (de exemplu dupa <c>revoke-client</c>), fara repornirea serviciului.
/// Daca fisierul lipseste sau e invalid, nu e autorizat nimeni (fail closed).
/// </summary>
public sealed class AuthorizedClientsFile(string path) : IAuthorizedClientStore
{
    private readonly Lock _lock = new();
    private AuthorizedClientList _current = AuthorizedClientList.Empty;
    private (DateTime WriteTime, long Length)? _loadedVersion;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Ultima eroare de citire, pentru log. <c>null</c> daca fisierul e valid.</summary>
    public string? LastError { get; private set; }

    public AuthorizedClientEntry? Find(string clientId) => Current().Find(clientId);

    public AuthorizedClientList Current()
    {
        var info = new FileInfo(Path);
        (DateTime, long)? version = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;

        lock (_lock)
        {
            if (_loadedVersion == version && (version is not null || LastError is not null))
            {
                return _current;
            }

            try
            {
                _current = version is null
                    ? throw new FileNotFoundException("Fisierul cu clienti autorizati nu exista.", Path)
                    : AuthorizedClientList.Parse(File.ReadAllText(Path));
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _current = AuthorizedClientList.Empty;
                LastError = ex.Message;
            }

            _loadedVersion = version;
            return _current;
        }
    }

    /// <summary>
    /// Scrie lista atomic (fisier temporar + rename). Pe Windows, un antivirus care scaneaza fisierul
    /// il poate bloca pentru scurt timp, asa ca inlocuirea se reincearca de cateva ori.
    /// </summary>
    public static void Save(string path, AuthorizedClientList list)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, list.Serialize());

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, fullPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    public static AuthorizedClientList LoadOrEmpty(string path) =>
        File.Exists(path) ? AuthorizedClientList.Parse(File.ReadAllText(path)) : AuthorizedClientList.Empty;
}
