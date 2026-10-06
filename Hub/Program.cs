using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortableHub;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("PORTABLE_HUB_CONFIG") ?? "/etc/portable-hub/hub.json", optional: false);
var config = builder.Configuration.Get<HubConfig>() ?? new HubConfig();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<S3Service>();
builder.Services.AddSingleton<CloudflareService>();
builder.Services.AddSingleton<HostingService>();
builder.Services.AddSingleton<Deployer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Deployer>());
builder.Services.AddSingleton<Poller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Poller>());
builder.Services.AddHttpClient("cloudflare", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("health", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
var nameRegex = new Regex("^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$");
var keyRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,200}$");

// Browsers cannot add custom headers to cross-site requests without a CORS preflight (which the hub never allows), so this
// header keeps other sites from driving the hub through the user's cached basic-auth credentials.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && path != "/api/github-webhook" && ctx.Request.Headers["X-Portable-Hub"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("missing X-Portable-Hub header");
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/overview", async (StateStore state, GitService git, HostingService hosting, CloudflareService cloudflare, Poller poller) =>
{
    var deployments = new JsonArray();
    var all = state.All();
    foreach (var d in all.OrderBy(d => d.Name))
    {
        var node = JsonSerializer.SerializeToNode(d, json)!.AsObject();
        var unit = await hosting.State(d.Name);
        node["host"] = config.HostFor(d.Name);
        node["url"] = $"https://{config.HostFor(d.Name)}";
        node["unit"] = new JsonObject
        {
            ["activeState"] = unit.ActiveState,
            ["subState"] = unit.SubState,
            ["memoryMb"] = unit.MemoryBytes is { } m ? m >> 20 : null,
        };
        node["branchTip"] = git.Branch(d.Branch)?.Sha;
        deployments.Add(node);
    }
    var branches = git.Branches.Select(b => new
    {
        b.Name, b.Sha, b.Date, b.Author, b.Subject,
        Deployments = all.Where(d => d.Branch == b.Name).Select(d => d.Name).ToArray(),
    });
    var disk = new DriveInfo(config.DataDir);
    var meminfo = File.Exists("/proc/meminfo")
        ? File.ReadLines("/proc/meminfo").Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => long.TryParse(p[1].Trim().Split(' ')[0], out var kb) ? kb >> 10 : 0)
        : new Dictionary<string, long>();
    return Results.Json(new
    {
        Hub = new
        {
            config.Zone, config.HubHost, config.HostPattern, config.RepoWebUrl, config.PollSeconds, config.PublicIp,
            Webhook = !string.IsNullOrEmpty(config.GitHubWebhookSecret),
            poller.LastWebhook,
            WebhookUrl = $"https://{config.HubHost}/api/github-webhook",
            Cloudflare = new { cloudflare.Configured, cloudflare.Status },
            git.LastFetch, git.LastFetchError,
            DiskFreeGb = Math.Round(disk.AvailableFreeSpace / 1e9, 1),
            DiskTotalGb = Math.Round(disk.TotalSize / 1e9, 1),
            MemTotalMb = meminfo.GetValueOrDefault("MemTotal"),
            MemAvailableMb = meminfo.GetValueOrDefault("MemAvailable"),
            SwapFreeMb = meminfo.GetValueOrDefault("SwapFree"),
        },
        Deployments = deployments,
        Branches = branches,
    }, json);
});

app.MapGet("/api/dbs", async (S3Service s3, bool? refresh, CancellationToken ct) =>
{
    try { return Results.Json(await s3.List(refresh == true, ct), json); }
    catch (Exception e) { return Results.Problem(e.Message); }
});

app.MapPost("/api/deployments", async (DeploymentRequest req, StateStore state, GitService git, Deployer deployer) =>
{
    var name = req.Name?.Trim().ToLowerInvariant() ?? "";
    if (!nameRegex.IsMatch(name)) return Results.BadRequest("name: 1-32 characters a-z, 0-9 and '-', not starting or ending with '-'");
    if (state.Get(name) != null) return Results.Conflict($"'{name}' already exists");
    var d = new Deployment { Name = name, CreatedAt = DateTimeOffset.UtcNow, Port = state.NextPort(config.FirstPort) };
    if (await Apply(d, req, git) is { } error) return Results.BadRequest(error);
    state.Add(d);
    deployer.Enqueue(name, JobKind.Deploy, "created");
    return Results.Json(state.Get(name), json);
});

app.MapPut("/api/deployments/{name}", async (string name, DeploymentRequest req, StateStore state, GitService git, Deployer deployer, HostingService hosting) =>
{
    var d = state.Get(name);
    if (d == null) return Results.NotFound();
    var before = (d.Branch, d.DbKey, d.ComparisonKey, d.Args, d.Protected);
    if (await Apply(d, req, git) is { } error) return Results.BadRequest(error);
    state.Update(name, x =>
    {
        (x.Branch, x.DbKey, x.ComparisonKey, x.Args, x.AutoDeploy, x.Protected, x.Notes) =
            (d.Branch, d.DbKey, d.ComparisonKey, d.Args, d.AutoDeploy, d.Protected, d.Notes);
        x.FailedCommit = null;
    });
    if (before.Protected != d.Protected) await hosting.WriteNginxMaps(state.All());
    if ((before.Branch, before.DbKey, before.ComparisonKey, before.Args) != (d.Branch, d.DbKey, d.ComparisonKey, d.Args))
        deployer.Enqueue(name, JobKind.Deploy, "settings changed");
    return Results.Json(state.Get(name), json);
});

app.MapPost("/api/deployments/{name}/{action}", async (string name, string action, StateStore state, Deployer deployer, HostingService hosting) =>
{
    var d = state.Get(name);
    if (d == null) return Results.NotFound();
    var unit = HostingService.UnitName(name);
    try
    {
        switch (action)
        {
            case "deploy":
                state.Update(name, x => x.FailedCommit = null);
                deployer.Enqueue(name, JobKind.Deploy, "manual redeploy");
                break;
            case "stop":
                await hosting.Systemd("disable", "--now", unit);
                state.Update(name, x => { x.Status = DeployStatus.Stopped; x.Error = null; });
                break;
            case "start":
                if (d.Release == null) { deployer.Enqueue(name, JobKind.Deploy, "start"); break; }
                await hosting.Systemd("enable", "--now", unit);
                state.Update(name, x => { x.Status = DeployStatus.Running; x.Error = null; });
                break;
            case "restart":
                await hosting.Systemd("restart", unit);
                break;
            default:
                return Results.NotFound();
        }
    }
    catch (Exception e) { return Results.Problem(e.Message); }
    return Results.Json(state.Get(name), json);
});

app.MapDelete("/api/deployments/{name}", (string name, StateStore state, Deployer deployer) =>
    state.Get(name) == null ? Results.NotFound() : Results.Json(deployer.Enqueue(name, JobKind.Delete, "delete")));

app.MapGet("/api/deployments/{name}/log", (string name, string? kind, int? lines, StateStore state, HostingService hosting) =>
{
    var d = state.Get(name);
    if (d == null) return Results.NotFound();
    var path = kind == "app" ? hosting.AppLog(name) : d.LastLog;
    return Results.Text(path == null ? "(no log yet)" : Deployer.Tail(path, Math.Clamp(lines ?? 400, 10, 5000)), "text/plain; charset=utf-8");
});

app.MapPost("/api/refresh", (Poller poller) => { poller.Trigger(); return Results.Accepted(); });

app.MapPost("/api/github-webhook", async (HttpRequest request, Poller poller) =>
{
    if (string.IsNullOrEmpty(config.GitHubWebhookSecret)) return Results.NotFound();
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.GitHubWebhookSecret), body.ToArray()));
    var actual = request.Headers["X-Hub-Signature-256"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected))) return Results.Unauthorized();
    poller.LastWebhook = DateTimeOffset.UtcNow;
    if (request.Headers["X-GitHub-Event"] == "push") poller.Trigger();
    return Results.Accepted();
});

app.Run();

// Validates and copies the editable fields of a create/update request onto the deployment; returns an error message or null.
async Task<string?> Apply(Deployment d, DeploymentRequest req, GitService git)
{
    var branch = req.Branch?.Trim() ?? d.Branch;
    if (git.Branch(branch) == null) await git.Refresh();
    if (git.Branch(branch) == null) return $"branch '{branch}' not found";
    string? Key(string? value, string? current) => value == null ? current : value.Trim() == "" ? null : value.Trim();
    var dbKey = Key(req.DbKey, d.DbKey);
    var comparisonKey = Key(req.ComparisonKey, d.ComparisonKey);
    foreach (var key in new[] { dbKey, comparisonKey })
        if (key != null && !keyRegex.IsMatch(key)) return $"invalid S3 key '{key}'";
    var args = req.Args ?? d.Args;
    if (args.Length > 500) return "arguments: at most 500 characters";
    try { HostingService.SplitArgs(args); } catch (ArgumentException e) { return e.Message; }
    var notes = req.Notes ?? d.Notes;
    if (notes.Length > 300) return "notes: at most 300 characters";
    (d.Branch, d.DbKey, d.ComparisonKey, d.Args, d.Notes) = (branch, dbKey, comparisonKey, args.Trim(), notes.Trim());
    d.AutoDeploy = req.AutoDeploy ?? d.AutoDeploy;
    d.Protected = req.Protected ?? d.Protected;
    return null;
}
