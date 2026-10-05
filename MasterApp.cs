using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;

namespace GarenaOrchestrator;

public static class MasterApp
{
    public static async Task RunAsync(string[] args)
    {
        string adminUser = Environment.GetEnvironmentVariable("ADMIN_USER") ?? "admin";
        string adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD")
            ?? throw new InvalidOperationException("ADMIN_PASSWORD is required in master mode.");
        string encryptionKey = Environment.GetEnvironmentVariable("MASTER_ENCRYPTION_KEY")
            ?? throw new InvalidOperationException("MASTER_ENCRYPTION_KEY is required in master mode.");
        var protector = new SecretProtector(encryptionKey);
        IStateStore store = CreateStore();
        var proxyXoay = new ProxyXoayClient(Environment.GetEnvironmentVariable("PROXYXOAY_API_URL"));
        var repository = new StateRepository(store, protector, proxyXoay);
        await repository.InitializeAsync(CancellationToken.None);

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:" + (Environment.GetEnvironmentVariable("PORT") ?? "8080"));
        builder.Services.AddSingleton(repository);
        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(proxyXoay.Dispose);

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        app.UseForwardedHeaders(forwarded);
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (ArgumentException ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
            catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";
            await next();
        });
        app.Use(async (context, next) =>
        {
            if (!NeedsAdminAuth(context.Request.Path)) { await next(); return; }
            if (TryBasicAuth(context.Request.Headers.Authorization, adminUser, adminPassword)) { await next(); return; }
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Garena Orchestrator\", charset=\"UTF-8\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "master", utc = DateTime.UtcNow }));
        app.MapGet("/api/ip", (HttpContext context) => Results.Ok(new { ip = GetClientIp(context) }));
        app.MapGet("/api/admin/snapshot", async (StateRepository repo, CancellationToken ct) => Results.Ok(await repo.GetDashboardAsync(ct)));

        app.MapPost("/api/admin/agents", async (HttpContext context, CreateAgentRequest request, StateRepository repo, CancellationToken ct) =>
        {
            RequireAdminMutation(context);
            string? configuredUrl = Environment.GetEnvironmentVariable("PUBLIC_URL");
            string publicUrl = string.IsNullOrWhiteSpace(configuredUrl)
                ? $"{context.Request.Scheme}://{context.Request.Host}"
                : configuredUrl;
            return Results.Created("/api/admin/agents", await repo.CreateAgentAsync(request, publicUrl, ct));
        });
        app.MapDelete("/api/admin/agents/{id}", async (HttpContext context, string id, StateRepository repo, CancellationToken ct) =>
        {
            RequireAdminMutation(context);
            return await repo.DeleteAgentAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });
        app.MapPost("/api/admin/proxy-keys", async (HttpContext context, CreateProxyKeyRequest request, StateRepository repo, CancellationToken ct) =>
        {
            RequireAdminMutation(context);
            return Results.Created("/api/admin/proxy-keys", await repo.CreateProxyKeyAsync(request, ct));
        });
        app.MapPut("/api/admin/proxy-keys/{id}/enabled", async (HttpContext context, string id, SetProxyKeyEnabledRequest request, StateRepository repo, CancellationToken ct) =>
        {
            RequireAdminMutation(context);
            ProxyKeyView? result = await repo.SetProxyKeyEnabledAsync(id, request.Enabled, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        app.MapDelete("/api/admin/proxy-keys/{id}", async (HttpContext context, string id, StateRepository repo, CancellationToken ct) =>
        {
            RequireAdminMutation(context);
            return await repo.DeleteProxyKeyAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/agent/heartbeat", async (HttpContext context, AgentHeartbeatRequest request, StateRepository repo, CancellationToken ct) =>
        {
            string? token = GetBearer(context.Request.Headers.Authorization);
            if (token is null) return Results.Unauthorized();
            AgentHeartbeatResponse? response = await repo.HeartbeatAsync(token, request, GetClientIp(context), ct);
            return response is null ? Results.Unauthorized() : Results.Ok(response);
        });

        app.MapFallbackToFile("index.html");
        Console.WriteLine($"Master listening on port {Environment.GetEnvironmentVariable("PORT") ?? "8080"}; storage={store.Description}");
        await app.RunAsync();
    }

    private static IStateStore CreateStore()
    {
        string? url = Environment.GetEnvironmentVariable("TURSO_URL");
        string? token = Environment.GetEnvironmentVariable("TURSO_TOKEN");
        if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(token))
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Set both TURSO_URL and TURSO_TOKEN, or neither.");
            return new TursoStateStore(url, token);
        }
        return new JsonFileStateStore(Environment.GetEnvironmentVariable("DATA_PATH") ?? "data/orchestrator-state.json");
    }

    private static bool NeedsAdminAuth(PathString path) =>
        path.StartsWithSegments("/api/admin") ||
        (!path.StartsWithSegments("/api/agent") && !path.StartsWithSegments("/api/ip") && !path.StartsWithSegments("/health"));

    private static bool TryBasicAuth(string? header, string expectedUser, string expectedPassword)
    {
        if (!AuthenticationHeaderValue.TryParse(header, out var auth) || !auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter ?? ""));
            int separator = decoded.IndexOf(':');
            return separator > 0 && SafeCompare.Equals(decoded[..separator], expectedUser) && SafeCompare.Equals(decoded[(separator + 1)..], expectedPassword);
        }
        catch (FormatException) { return false; }
    }

    private static string? GetBearer(string? header) =>
        AuthenticationHeaderValue.TryParse(header, out var auth) && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? auth.Parameter : null;

    private static string GetClientIp(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static void RequireAdminMutation(HttpContext context)
    {
        if (context.Request.Headers["X-Admin-Request"] != "1")
            throw new BadHttpRequestException("Missing X-Admin-Request header.", StatusCodes.Status400BadRequest);
    }
}
