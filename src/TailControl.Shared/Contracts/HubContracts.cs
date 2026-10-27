using System.Text.Json.Serialization;

namespace TailControl.Shared.Contracts;

/// <summary>Rute expuse de Hub (port implicit 8443).</summary>
public static class HubRoutes
{
    public const int DefaultPort = 8443;

    /// <summary><c>GET</c>: lista cu status. <c>POST</c> (admin): adaugare.</summary>
    public const string Devices = "/v1/devices";

    /// <summary><c>GET</c>; <c>PUT</c> si <c>DELETE</c> (admin).</summary>
    public const string Device = "/v1/devices/{id:int:min(1)}";

    /// <summary><c>POST</c>: trimite magic packet catre MAC-ul din inventar.</summary>
    public const string Wake = "/v1/devices/{id:int:min(1)}/wake";

    /// <summary><c>GET</c> (admin): ultimele intrari din jurnalul de audit.</summary>
    public const string Audit = "/v1/audit";

    public static string ForDevice(int id) => $"/v1/devices/{id}";

    public static string ForWake(int id) => $"/v1/devices/{id}/wake";
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
public enum DeviceKind
{
    Desktop,
    Laptop,
    Other,
}

public sealed record DeviceDto(
    int Id,
    string Name,
    string? MacAddress,
    string Host,
    int AgentPort,
    DeviceKind Kind,
    bool Online,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Adaugare sau modificare de dispozitiv. MAC-ul e optional (fara el, pornirea nu e disponibila).</summary>
public sealed record DeviceRequest(
    string Name,
    string? MacAddress,
    string Host,
    int? AgentPort,
    DeviceKind Kind);

public sealed record WakeResponse(int DeviceId, string Message);

public sealed record AuditEntryDto(
    long Id,
    DateTimeOffset TimeUtc,
    string ClientId,
    string ClientLabel,
    string? RemoteIp,
    string Action,
    string? Target,
    string Result,
    string? Detail);
