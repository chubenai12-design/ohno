using System.Text.Json;
using System.Text.RegularExpressions;

namespace GarenaOrchestrator;

public sealed record ProxyXoayResult(
    string Url,
    string? Username,
    string? Password,
    string Message,
    string? Carrier,
    string? Location,
    DateTime ExpiresUtc);

public sealed partial class ProxyXoayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public ProxyXoayClient(string? endpoint = null, HttpMessageHandler? handler = null)
    {
        string raw = string.IsNullOrWhiteSpace(endpoint)
            ? "https://proxyxoay.shop/api/get.php"
            : endpoint.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("PROXYXOAY_API_URL must be an absolute HTTP/HTTPS URL.");
        _endpoint = uri;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(12) })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GarenaOrchestrator-Master/1.0");
    }

    public async Task<ProxyXoayResult> GetProxyAsync(string key, string whitelistIp, CancellationToken ct)
    {
        string separator = string.IsNullOrEmpty(_endpoint.Query) ? "?" : "&";
        string requestUrl = _endpoint + separator + "key=" + Uri.EscapeDataString(key) +
            "&nhamang=random&tinhthanh=0&whitelist=" + Uri.EscapeDataString(whitelistIp);
        using HttpResponseMessage response = await _http.GetAsync(requestUrl, ct);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        JsonElement root = json.RootElement;
        int status = ReadStatus(root);
        string message = ReadString(root, "message") ?? "ProxyXoay không trả message.";
        if (status != 100)
            throw new InvalidOperationException($"ProxyXoay status {status}: {Safe(message)}");

        string rawProxy = ReadString(root, "proxyhttp")
            ?? throw new InvalidOperationException("ProxyXoay không trả proxyhttp.");
        ParseProxy(rawProxy, out string url, out string? username, out string? password);
        int lifetime = ParseLifetimeSeconds(message);
        return new ProxyXoayResult(
            url,
            username,
            password,
            Safe(message),
            ReadString(root, "Nha Mang"),
            ReadString(root, "Vi Tri"),
            DateTime.UtcNow.AddSeconds(lifetime));
    }

    private static int ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out JsonElement value)) return -1;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : -1;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? Safe(value.GetString() ?? "") : null;

    private static void ParseProxy(string raw, out string url, out string? username, out string? password)
    {
        string[] parts = raw.Trim().Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(parts[0]) || parts[0].Any(char.IsControl))
            throw new InvalidOperationException("Định dạng proxyhttp từ ProxyXoay không hợp lệ.");
        url = $"http://{parts[0]}:{port}";
        username = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
        password = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? string.Join(':', parts[3..]) : null;
    }

    private static int ParseLifetimeSeconds(string message)
    {
        Match match = LifetimeRegex().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out int seconds)
            ? Math.Clamp(seconds, 60, 86_400)
            : 1_500;
    }

    private static string Safe(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean[..Math.Min(clean.Length, 240)];
    }

    [GeneratedRegex(@"(?:sau|after)\s+(\d+)\s*s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LifetimeRegex();

    public void Dispose() => _http.Dispose();
}
