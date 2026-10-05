using System.Security.Cryptography;
using System.Text.Json;

namespace GarenaOrchestrator;

public sealed class StateRepository
{
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinimumProviderInterval = TimeSpan.FromSeconds(61);
    private readonly IStateStore _store;
    private readonly SecretProtector _protector;
    private readonly ProxyXoayClient _proxyXoay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _refreshingKeys = [];
    private OrchestratorState _state = new();

    public StateRepository(IStateStore store, SecretProtector protector, ProxyXoayClient proxyXoay)
    {
        _store = store;
        _protector = protector;
        _proxyXoay = proxyXoay;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _state = await _store.LoadAsync(ct);
        _state.Agents ??= [];
        _state.ProxyKeys ??= [];
        _state.Proxies ??= [];
        _state.SchemaVersion = 2;
    }

    public async Task<DashboardView> GetDashboardAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DateTime now = DateTime.UtcNow;
            var agents = _state.Agents.OrderBy(a => a.Name).Select(a =>
            {
                ProxyKeyRecord? key = _state.ProxyKeys.FirstOrDefault(k => k.Id == a.AssignedProxyKeyId);
                return new AgentView(
                    a.Id, a.Name, a.MaxSlots, a.ActiveJobs, a.AssignedProxyId,
                    a.AssignedProxyKeyId, key?.Name, key?.CurrentProxyUrl, key?.ExpiresUtc,
                    a.ObservedIp, a.CreatedUtc, a.LastSeenUtc,
                    now - a.LastSeenUtc < TimeSpan.FromSeconds(55), a.Version,
                    a.ProxyStatus, a.ProxyEgressIp, a.ProxyLatencyMs);
            }).ToList();
            var keys = _state.ProxyKeys.OrderBy(k => k.Name).Select(k =>
            {
                AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == k.AssignedAgentId);
                return new ProxyKeyView(
                    k.Id, k.Name, MaskKey(_protector.Decrypt(k.EncryptedKey)), k.Enabled,
                    k.AssignedAgentId, agent?.Name, k.WhitelistedIp, k.CurrentProxyUrl,
                    k.Carrier, k.Location, k.ProviderMessage, k.LastError, k.LastRefreshUtc, k.ExpiresUtc);
            }).ToList();
            return new DashboardView(now, _store.Description, agents, keys);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentTokenResponse> CreateAgentAsync(CreateAgentRequest request, string masterUrl, CancellationToken ct)
    {
        string name = CleanName(request.Name, "Agent name");
        int slots = Math.Clamp(request.MaxSlots, 1, 50);
        string id = "agt_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        string token = id + "." + SecretProtector.NewSecret();
        var record = new AgentRecord { Id = id, Name = name, TokenHash = _protector.HashToken(token), MaxSlots = slots };
        await _gate.WaitAsync(ct);
        try
        {
            _state.Agents.Add(record);
            await _store.SaveAsync(_state, ct);
        }
        finally { _gate.Release(); }
        string command = $"dotnet GarenaOrchestrator.dll satellite --master-url {masterUrl.TrimEnd('/')} --agent-token {token} --name \"{name.Replace("\"", "") }\" --slots {slots}";
        return new AgentTokenResponse(id, name, token, command);
    }

    public async Task<bool> DeleteAgentAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            int removed = _state.Agents.RemoveAll(a => a.Id == id);
            if (removed == 0) return false;
            foreach (ProxyKeyRecord key in _state.ProxyKeys.Where(k => k.AssignedAgentId == id))
                ReleaseKey(key);
            await _store.SaveAsync(_state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyKeyView> CreateProxyKeyAsync(CreateProxyKeyRequest request, CancellationToken ct)
    {
        string name = CleanName(request.Name, "Key name");
        string rawKey = CleanKey(request.Key);
        await _gate.WaitAsync(ct);
        try
        {
            if (_state.ProxyKeys.Any(k => SafeCompare.Equals(_protector.Decrypt(k.EncryptedKey), rawKey)))
                throw new ArgumentException("Key này đã tồn tại.");
            var key = new ProxyKeyRecord
            {
                Id = "key_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                Name = name,
                EncryptedKey = _protector.Encrypt(rawKey)
            };
            _state.ProxyKeys.Add(key);
            await _store.SaveAsync(_state, ct);
            return ToView(key);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteProxyKeyAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ProxyKeyRecord? key = _state.ProxyKeys.FirstOrDefault(k => k.Id == id);
            if (key is null) return false;
            foreach (AgentRecord agent in _state.Agents.Where(a => a.AssignedProxyKeyId == id))
            {
                agent.AssignedProxyKeyId = null;
                agent.ProxyStatus = "Chưa có key proxy";
                agent.ProxyEgressIp = null;
                agent.ProxyLatencyMs = null;
            }
            _state.ProxyKeys.Remove(key);
            _refreshingKeys.Remove(id);
            await _store.SaveAsync(_state, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyKeyView?> SetProxyKeyEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ProxyKeyRecord? key = _state.ProxyKeys.FirstOrDefault(k => k.Id == id);
            if (key is null) return null;
            key.Enabled = enabled;
            if (!enabled)
            {
                foreach (AgentRecord agent in _state.Agents.Where(a => a.AssignedProxyKeyId == id))
                {
                    agent.AssignedProxyKeyId = null;
                    agent.ProxyStatus = "Key proxy đã tắt";
                }
                ReleaseKey(key);
            }
            await _store.SaveAsync(_state, ct);
            return ToView(key);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentHeartbeatResponse?> HeartbeatAsync(
        string token,
        AgentHeartbeatRequest request,
        string observedIp,
        CancellationToken ct)
    {
        string agentId = token.Split('.', 2)[0];
        RefreshPlan? refreshPlan = null;
        bool shouldPersist = false;

        await _gate.WaitAsync(ct);
        try
        {
            AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null || !_protector.VerifyToken(token, agent.TokenHash)) return null;

            shouldPersist = agent.ObservedIp != observedIp;
            agent.ObservedIp = observedIp;
            agent.LastSeenUtc = DateTime.UtcNow;
            agent.ActiveJobs = Math.Clamp(request.ActiveJobs, 0, 1000);
            agent.MaxSlots = Math.Clamp(request.MaxSlots, 1, 50);
            agent.Version = Truncate((request.Version ?? "").Trim(), 40);
            if (!string.IsNullOrWhiteSpace(request.Name)) agent.Name = CleanName(request.Name, "Agent name");
            if (!string.IsNullOrWhiteSpace(request.ProxyStatus)) agent.ProxyStatus = Truncate(request.ProxyStatus.Trim(), 180);
            agent.ProxyEgressIp = NullIfWhiteSpace(request.ProxyEgressIp);
            agent.ProxyLatencyMs = request.ProxyLatencyMs;

            ProxyKeyRecord? key = GetUsableAssignedKey(agent);
            if (key is null)
            {
                key = _state.ProxyKeys.FirstOrDefault(k => k.Enabled && string.IsNullOrEmpty(k.AssignedAgentId));
                if (key is not null)
                {
                    key.AssignedAgentId = agent.Id;
                    agent.AssignedProxyKeyId = key.Id;
                    agent.ProxyStatus = "Đang lấy proxy từ ProxyXoay";
                    shouldPersist = true;
                }
                else
                {
                    agent.AssignedProxyKeyId = null;
                    agent.ProxyStatus = "Không có key ProxyXoay khả dụng";
                }
            }

            if (key is not null && NeedsRefresh(key, observedIp) && _refreshingKeys.Add(key.Id))
            {
                key.LastError = null;
                refreshPlan = new RefreshPlan(agent.Id, key.Id, _protector.Decrypt(key.EncryptedKey), observedIp);
            }
            if (shouldPersist) await _store.SaveAsync(_state, ct);
        }
        finally { _gate.Release(); }

        if (refreshPlan is not null)
        {
            ProxyXoayResult? result = null;
            string? error = null;
            try { result = await _proxyXoay.GetProxyAsync(refreshPlan.RawKey, refreshPlan.WhitelistIp, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                string safeMessage = ex is TaskCanceledException ? "ProxyXoay timeout." : ex.Message;
                safeMessage = safeMessage.Replace(refreshPlan.RawKey, "[redacted]", StringComparison.Ordinal);
                error = Truncate(safeMessage, 240);
            }

            await _gate.WaitAsync(ct);
            try
            {
                _refreshingKeys.Remove(refreshPlan.KeyId);
                ProxyKeyRecord? key = _state.ProxyKeys.FirstOrDefault(k => k.Id == refreshPlan.KeyId && k.AssignedAgentId == refreshPlan.AgentId);
                AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == refreshPlan.AgentId && a.AssignedProxyKeyId == refreshPlan.KeyId);
                if (key is not null && agent is not null)
                {
                    key.LastRefreshUtc = DateTime.UtcNow;
                    if (result is not null)
                    {
                        key.WhitelistedIp = refreshPlan.WhitelistIp;
                        key.CurrentProxyUrl = result.Url;
                        key.CurrentProxyUsername = result.Username;
                        key.EncryptedCurrentProxyPassword = result.Password is null ? null : _protector.Encrypt(result.Password);
                        key.ProviderMessage = result.Message;
                        key.Carrier = result.Carrier;
                        key.Location = result.Location;
                        key.ExpiresUtc = result.ExpiresUtc;
                        key.LastError = null;
                        agent.ProxyStatus = "Đã cấp proxy; chờ vệ tinh kiểm tra";
                    }
                    else
                    {
                        key.LastError = error ?? "Không lấy được proxy.";
                        agent.ProxyStatus = "Lỗi ProxyXoay: " + key.LastError;
                    }
                    await _store.SaveAsync(_state, ct);
                }
            }
            finally { _gate.Release(); }
        }

        await _gate.WaitAsync(ct);
        try
        {
            AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return null;
            ProxyKeyRecord? key = GetUsableAssignedKey(agent);
            ProxyLease? lease = key is not null && CanLease(key, observedIp)
                ? new ProxyLease(key.Id, key.Name, key.CurrentProxyUrl!, key.CurrentProxyUsername,
                    key.EncryptedCurrentProxyPassword is null ? null : _protector.Decrypt(key.EncryptedCurrentProxyPassword))
                : null;
            return new AgentHeartbeatResponse(DateTime.UtcNow, observedIp, 20, lease);
        }
        finally { _gate.Release(); }
    }

    private ProxyKeyRecord? GetUsableAssignedKey(AgentRecord agent)
    {
        ProxyKeyRecord? key = _state.ProxyKeys.FirstOrDefault(k =>
            k.Id == agent.AssignedProxyKeyId && k.Enabled && (k.AssignedAgentId is null || k.AssignedAgentId == agent.Id));
        if (key is null && agent.AssignedProxyKeyId is not null) agent.AssignedProxyKeyId = null;
        if (key is not null && key.AssignedAgentId is null) key.AssignedAgentId = agent.Id;
        return key;
    }

    private static bool NeedsRefresh(ProxyKeyRecord key, string observedIp) =>
        (key.LastRefreshUtc is null || key.LastRefreshUtc <= DateTime.UtcNow - MinimumProviderInterval) &&
        (string.IsNullOrWhiteSpace(key.CurrentProxyUrl) || key.WhitelistedIp != observedIp ||
         key.ExpiresUtc is null || key.ExpiresUtc <= DateTime.UtcNow + RefreshBeforeExpiry);

    private static bool CanLease(ProxyKeyRecord key, string observedIp) =>
        key.Enabled && key.WhitelistedIp == observedIp && !string.IsNullOrWhiteSpace(key.CurrentProxyUrl) &&
        key.ExpiresUtc > DateTime.UtcNow.AddSeconds(15);

    private static void ReleaseKey(ProxyKeyRecord key)
    {
        key.AssignedAgentId = null;
        key.WhitelistedIp = null;
        key.CurrentProxyUrl = null;
        key.CurrentProxyUsername = null;
        key.EncryptedCurrentProxyPassword = null;
        key.ExpiresUtc = null;
        key.LastError = null;
        key.ProviderMessage = null;
    }

    private ProxyKeyView ToView(ProxyKeyRecord key)
    {
        AgentRecord? agent = _state.Agents.FirstOrDefault(a => a.Id == key.AssignedAgentId);
        return new ProxyKeyView(
            key.Id, key.Name, MaskKey(_protector.Decrypt(key.EncryptedKey)), key.Enabled,
            key.AssignedAgentId, agent?.Name, key.WhitelistedIp, key.CurrentProxyUrl,
            key.Carrier, key.Location, key.ProviderMessage, key.LastError, key.LastRefreshUtc, key.ExpiresUtc);
    }

    private static string MaskKey(string key) => key.Length <= 4 ? "••••" : "••••••" + key[^4..];

    private static string CleanKey(string? value)
    {
        string key = (value ?? "").Trim();
        if (key.Length is < 4 or > 500 || key.Any(char.IsControl))
            throw new ArgumentException("ProxyXoay key phải có 4-500 ký tự hợp lệ.");
        return key;
    }

    private static string CleanName(string? value, string label)
    {
        string result = (value ?? "").Trim();
        if (result.Length is < 1 or > 80 || result.Any(char.IsControl))
            throw new ArgumentException(label + " must contain 1-80 printable characters.");
        return result;
    }

    private static string Truncate(string value, int length) => value[..Math.Min(value.Length, length)];
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private sealed record RefreshPlan(string AgentId, string KeyId, string RawKey, string WhitelistIp);
}
