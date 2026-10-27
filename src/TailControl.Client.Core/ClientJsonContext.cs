using System.Text.Json;
using System.Text.Json.Serialization;
using TailControl.Shared.Contracts;

namespace TailControl.Client.Core;

/// <summary>Raspunsul de eroare (ProblemDetails) al Hub-ului.</summary>
public sealed record ProblemResponse(string? Title, Dictionary<string, string[]>? Errors);

/// <summary>
/// Serializare generata la compilare: aplicatia Android e "trimmed" in Release, iar
/// serializarea prin reflexie ar putea pierde constructorii DTO-urilor.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<DeviceDto>))]
[JsonSerializable(typeof(DeviceDto))]
[JsonSerializable(typeof(DeviceRequest))]
[JsonSerializable(typeof(WakeResponse))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(AgentStatusResponse))]
[JsonSerializable(typeof(List<AuditEntryDto>))]
[JsonSerializable(typeof(ProblemResponse))]
public sealed partial class ClientJsonContext : JsonSerializerContext;
