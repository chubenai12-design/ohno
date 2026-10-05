using System.Text.Json.Serialization;

namespace GarenaOrchestrator;

public sealed class OrchestratorState
{
    public int SchemaVersion { get; set; } = 2;
    public List<AgentRecord> Agents { get; set; } = [];
    public List<ProxyKeyRecord> ProxyKeys { get; set; } = [];
    // Kept only so existing version-1 state can still be loaded safely.
    public List<ProxyRecord> Proxies { get; set; } = [];
}

public sealed class AgentRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public int MaxSlots { get; set; } = 1;
    public string? AssignedProxyId { get; set; }
    public string? AssignedProxyKeyId { get; set; }
    public string? ObservedIp { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; }
    public string Version { get; set; } = "";
    public int ActiveJobs { get; set; }
    public string ProxyStatus { get; set; } = "Chưa kiểm tra";
    public string? ProxyEgressIp { get; set; }
    public long? ProxyLatencyMs { get; set; }
}

public sealed class ProxyKeyRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string EncryptedKey { get; set; }
    public bool Enabled { get; set; } = true;
    public string? AssignedAgentId { get; set; }
    public string? WhitelistedIp { get; set; }
    public string? CurrentProxyUrl { get; set; }
    public string? CurrentProxyUsername { get; set; }
    public string? EncryptedCurrentProxyPassword { get; set; }
    public string? Carrier { get; set; }
    public string? Location { get; set; }
    public string? ProviderMessage { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastRefreshUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProxyRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed record CreateAgentRequest(string Name, int MaxSlots = 1);
public sealed record CreateProxyKeyRequest(string Name, string Key);
public sealed record SetProxyKeyEnabledRequest(bool Enabled);
public sealed record AssignProxyRequest(string? ProxyId);
public sealed record UpsertProxyRequest(string Name, string Url, string? Username, string? Password);
public sealed record AgentHeartbeatRequest(
    string? Name,
    string? Version,
    int MaxSlots,
    int ActiveJobs,
    string? ProxyStatus,
    string? ProxyEgressIp,
    long? ProxyLatencyMs);

public sealed record AgentTokenResponse(string Id, string Name, string Token, string ExampleCommand);

public sealed record ProxyLease(
    string Id,
    string Name,
    string Url,
    string? Username,
    string? Password);

public sealed record AgentHeartbeatResponse(
    DateTime ServerTimeUtc,
    string ObservedIp,
    int HeartbeatSeconds,
    ProxyLease? AssignedProxy);

public sealed record AgentView(
    string Id,
    string Name,
    int MaxSlots,
    int ActiveJobs,
    string? AssignedProxyId,
    string? AssignedProxyKeyId,
    string? AssignedProxyKeyName,
    string? ManagedProxyUrl,
    DateTime? ProxyExpiresUtc,
    string? ObservedIp,
    DateTime CreatedUtc,
    DateTime LastSeenUtc,
    bool Online,
    string Version,
    string ProxyStatus,
    string? ProxyEgressIp,
    long? ProxyLatencyMs);

public sealed record ProxyKeyView(
    string Id,
    string Name,
    string MaskedKey,
    bool Enabled,
    string? AssignedAgentId,
    string? AssignedAgentName,
    string? WhitelistedIp,
    string? CurrentProxyUrl,
    string? Carrier,
    string? Location,
    string? ProviderMessage,
    string? LastError,
    DateTime? LastRefreshUtc,
    DateTime? ExpiresUtc);

public sealed record ProxyView(
    string Id,
    string Name,
    string Url,
    string? Username,
    bool HasPassword,
    int AssignedAgents,
    DateTime UpdatedUtc);

public sealed record DashboardView(
    DateTime ServerTimeUtc,
    string Storage,
    IReadOnlyList<AgentView> Agents,
    IReadOnlyList<ProxyKeyView> ProxyKeys);

public sealed class SatelliteHealth
{
    public bool Connected { get; set; }
    public string? ObservedIp { get; set; }
    public string ProxyStatus { get; set; } = "Chưa được gán proxy";
    public string? ProxyEgressIp { get; set; }
    public long? ProxyLatencyMs { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public string? LastError { get; set; }
}

[JsonSerializable(typeof(OrchestratorState))]
[JsonSerializable(typeof(AgentHeartbeatRequest))]
[JsonSerializable(typeof(AgentHeartbeatResponse))]
[JsonSerializable(typeof(DashboardView))]
internal partial class AppJsonContext : JsonSerializerContext;
