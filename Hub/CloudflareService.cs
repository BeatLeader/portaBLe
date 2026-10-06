using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortableHub;

/// <summary>
/// Proxied A records for deployments. Records the hub creates carry <see cref="Marker"/> in their comment, and only those
/// are ever modified or deleted. The token is read from a file on every call, so dropping it in place needs no restart.
/// </summary>
public sealed class CloudflareService(HubConfig config, IHttpClientFactory httpFactory, ILogger<CloudflareService> logger)
{
    public const string Marker = "managed by portaBLe Hub";
    private string? _zoneId;

    public string Status { get; private set; } = "not checked";

    private string? Token()
    {
        try { return File.Exists(config.CloudflareTokenFile) ? File.ReadAllText(config.CloudflareTokenFile).Trim() : null; }
        catch (Exception e) { Status = $"cannot read token: {e.Message}"; return null; }
    }

    public bool Configured => !string.IsNullOrEmpty(Token()) && !string.IsNullOrEmpty(config.PublicIp);

    private async Task<JsonNode> Call(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = Token() ?? throw new InvalidOperationException("no Cloudflare token (" + config.CloudflareTokenFile + ")");
        using var request = new HttpRequestMessage(method, "https://api.cloudflare.com/client/v4/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await httpFactory.CreateClient("cloudflare").SendAsync(request, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
        if (json["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"Cloudflare {method} {path}: {(int)response.StatusCode} {json["errors"]?.ToJsonString()}");
        return json["result"]!;
    }

    private async Task<string> ZoneId(CancellationToken ct)
    {
        if (_zoneId != null) return _zoneId;
        var zones = (await Call(HttpMethod.Get, $"zones?name={Uri.EscapeDataString(config.Zone)}", null, ct)).AsArray();
        _zoneId = zones.FirstOrDefault()?["id"]?.GetValue<string>() ?? throw new InvalidOperationException($"zone {config.Zone} not visible to the token");
        return _zoneId;
    }

    private async Task<JsonArray> Records(string host, CancellationToken ct) =>
        (await Call(HttpMethod.Get, $"zones/{await ZoneId(ct)}/dns_records?name={Uri.EscapeDataString(host)}", null, ct)).AsArray();

    /// <summary>Creates (or repairs, if ours) a proxied A record for <paramref name="host"/>. Returns true if the hub manages it.</summary>
    public async Task<bool> EnsureRecord(string host, TextWriter? log, CancellationToken ct)
    {
        try
        {
            var zone = await ZoneId(ct);
            var existing = await Records(host, ct);
            var ours = existing.FirstOrDefault(r => r?["comment"]?.GetValue<string>() == Marker);
            if (ours != null)
            {
                if (ours["type"]?.GetValue<string>() != "A" || ours["content"]?.GetValue<string>() != config.PublicIp || ours["proxied"]?.GetValue<bool>() != true)
                {
                    await Call(HttpMethod.Patch, $"zones/{zone}/dns_records/{ours["id"]}",
                        new { type = "A", content = config.PublicIp, proxied = true, ttl = 1 }, ct);
                    log?.WriteLine($"DNS: repaired {host} -> {config.PublicIp}");
                }
                Status = "ok";
                return true;
            }
            if (existing.Count > 0)
            {
                // someone else's record: leave it alone
                log?.WriteLine($"DNS: {host} already has records not managed by the hub; left unchanged");
                Status = "ok";
                return false;
            }
            await Call(HttpMethod.Post, $"zones/{zone}/dns_records",
                new { type = "A", name = host, content = config.PublicIp, proxied = true, ttl = 1, comment = Marker }, ct);
            log?.WriteLine($"DNS: created {host} -> {config.PublicIp} (proxied)");
            Status = "ok";
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = e.Message;
            logger.LogWarning("Cloudflare: {Message}", e.Message);
            log?.WriteLine($"DNS: {e.Message}");
            return false;
        }
    }

    public async Task DeleteRecord(string host, TextWriter? log, CancellationToken ct)
    {
        try
        {
            var zone = await ZoneId(ct);
            foreach (var r in await Records(host, ct))
            {
                if (r?["comment"]?.GetValue<string>() != Marker) continue;
                await Call(HttpMethod.Delete, $"zones/{zone}/dns_records/{r["id"]}", null, ct);
                log?.WriteLine($"DNS: deleted {host}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = e.Message;
            log?.WriteLine($"DNS: {e.Message}");
        }
    }
}
