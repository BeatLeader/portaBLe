using System.Threading.Channels;

namespace PortableHub;

public enum JobKind { Deploy, Delete }

public sealed record Job(string Name, JobKind Kind, string Reason);

/// <summary>
/// Runs deploy/delete jobs one at a time. Layout per deployment (DataDir/deployments/NAME):
/// src/ (shallow checkout), releases/TIMESTAMP-SHA/ (dotnet publish output), current -> releases/..., data/ (S3 databases,
/// hard-linked into the release's wwwroot), logs/ (deploy-*.log, app.log).
/// </summary>
public sealed class Deployer(HubConfig config, StateStore state, GitService git, S3Service s3, CloudflareService cloudflare,
    HostingService hosting, IHttpClientFactory httpFactory, ILogger<Deployer> logger) : BackgroundService
{
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly HashSet<(string, JobKind)> _pending = new();

    public bool IsPending(string name)
    {
        lock (_pending) return _pending.Contains((name, JobKind.Deploy)) || _pending.Contains((name, JobKind.Delete));
    }

    public bool Enqueue(string name, JobKind kind, string reason)
    {
        lock (_pending)
        {
            if (!_pending.Add((name, kind))) return false;
        }
        state.Update(name, d => d.Activity = kind == JobKind.Deploy ? $"queued: {reason}" : "queued for deletion");
        _queue.Writer.TryWrite(new Job(name, kind, reason));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_pending) _pending.Remove((job.Name, job.Kind));
            try
            {
                if (job.Kind == JobKind.Deploy) await Deploy(job.Name, job.Reason, stoppingToken);
                else await Delete(job.Name, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "job {Kind} {Name} failed", job.Kind, job.Name);
                state.Update(job.Name, d => { d.Activity = null; d.Error = e.Message; });
            }
        }
    }

    private async Task Deploy(string name, string reason, CancellationToken ct)
    {
        var d = state.Get(name);
        if (d == null) return;
        var dir = hosting.DeploymentDir(name);
        string src = Path.Combine(dir, "src"), releases = Path.Combine(dir, "releases"), data = Path.Combine(dir, "data"), logs = Path.Combine(dir, "logs");
        foreach (var p in new[] { releases, data, logs }) Directory.CreateDirectory(p);
        var logPath = Path.Combine(logs, $"deploy-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        await using var file = new StreamWriter(logPath) { AutoFlush = true };
        var log = TextWriter.Synchronized(file);
        void Step(string activity)
        {
            log.WriteLine($"== {activity} ({DateTime.UtcNow:HH:mm:ss})");
            state.Update(name, x => x.Activity = activity);
        }

        state.Update(name, x => { x.Status = DeployStatus.Building; x.LastLog = logPath; });
        log.WriteLine($"deploy {name} ({d.Branch}): {reason}");
        string? sha = null, release = null;
        var previous = d.Release != null ? Path.Combine(releases, d.Release) : null;
        var switched = false;
        try
        {
            Step("fetching " + d.Branch);
            await git.Refresh(ct);
            var tip = git.Branch(d.Branch);
            string subject;
            if (tip != null && CachedBuild(tip.Sha) is { } cached)
            {
                // another deployment already built this commit: reuse it (hard links, no extra space)
                (sha, subject) = (tip.Sha, tip.Subject);
                log.WriteLine($"reusing the build of {sha[..7]} ({cached})");
                release = Path.Combine(releases, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{sha[..7]}");
                await Shell.Check("/usr/bin/cp", ["-al", cached, release], ct: ct);
            }
            else
            {
                (sha, subject) = await git.Checkout(src, d.Branch, log, ct);
                Step("building " + sha[..7]);
                release = Path.Combine(releases, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{sha[..7]}");
                await Shell.Check(config.Dotnet, ["publish", Path.Combine(src, config.ProjectFile), "-c", "Release", "-o", release,
                    // portaBLe references RatingAPI, itself a web app: both ship appsettings*.json, so allow the clash and restore portaBLe's own
                    "-p:ErrorOnDuplicatePublishOutputFiles=false", "-nologo", "-v:q"], cwd: src, log: log, timeout: TimeSpan.FromMinutes(25), ct: ct);
                foreach (var f in Directory.GetFiles(src, "appsettings*.json")) File.Copy(f, Path.Combine(release, Path.GetFileName(f)), overwrite: true);
                await CacheBuild(sha, release, log, ct);
            }
            var dbKey = string.IsNullOrWhiteSpace(d.DbKey) ? DefaultDbKey(release) : d.DbKey.Trim();
            if (string.IsNullOrWhiteSpace(dbKey))
                throw new InvalidOperationException($"no database: pick one or add {config.DbKeyFile} to the branch");

            Step("database " + dbKey);
            await EnsureDb(data, "Database.db", dbKey, log, ct);
            if (!string.IsNullOrWhiteSpace(d.ComparisonKey)) await EnsureDb(data, "Comparison.db", d.ComparisonKey.Trim(), log, ct);
            else RemoveDb(data, "Comparison.db");
            var wwwroot = Path.Combine(release, "wwwroot");
            Directory.CreateDirectory(wwwroot);
            foreach (var db in new[] { "Database.db", "Comparison.db" })
                if (File.Exists(Path.Combine(data, db)))
                    await Shell.Check("/usr/bin/ln", ["-f", Path.Combine(data, db), Path.Combine(wwwroot, db)], ct: ct);

            Step("starting");
            await SwitchCurrent(dir, release);
            switched = true;
            var unit = HostingService.UnitName(name);
            await hosting.WriteUnit(state.Get(name)!);
            await hosting.Systemd("enable", unit);
            RotateAppLog(name);
            await hosting.Systemd("restart", unit);
            if (!await WaitHealthy(name, d.Port, log, ct))
            {
                log.WriteLine("--- app.log (tail) ---");
                log.WriteLine(Tail(hosting.AppLog(name), 40));
                var rolledBack = previous != null && Directory.Exists(previous);
                if (rolledBack)
                {
                    await SwitchCurrent(dir, previous!);
                    await hosting.Systemd("restart", unit);
                    switched = false;
                }
                throw new InvalidOperationException($"the app did not answer within {config.HealthTimeoutSeconds}s" +
                    (rolledBack ? "; rolled back to the previous release" : ""));
            }

            state.Update(name, x =>
            {
                x.Status = DeployStatus.Running;
                x.Activity = null;
                x.Error = null;
                x.Commit = sha;
                x.CommitSubject = subject;
                x.FailedCommit = null;
                x.SkippedCommit = null;
                x.SkippedFiles = 0;
                x.ActiveDbKey = dbKey;
                x.ActiveComparisonKey = string.IsNullOrWhiteSpace(d.ComparisonKey) ? null : d.ComparisonKey.Trim();
                x.Release = Path.GetFileName(release);
                x.DeployedAt = DateTimeOffset.UtcNow;
            });
            CleanupReleases(releases, release);
            await EnsureRouting(name, log, ct);
            log.WriteLine($"deployed {sha} to https://{config.HostFor(name)}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.WriteLine("FAILED: " + e.Message);
            if (release != null && !switched) TryDelete(release);
            var serving = (await hosting.State(name)).ActiveState == "active";
            state.Update(name, x =>
            {
                x.Status = serving && x.Release != null ? DeployStatus.Running : DeployStatus.Failed;
                x.Activity = null;
                x.Error = e.Message;
                x.FailedCommit = sha;
            });
        }
    }

    private async Task Delete(string name, CancellationToken ct)
    {
        var d = state.Get(name);
        if (d == null) return;
        state.Update(name, x => x.Activity = "deleting");
        await hosting.RemoveUnit(name);
        state.Remove(name);
        await hosting.WriteNginxMaps(state.All());
        if (d.DnsManaged) await cloudflare.DeleteRecord(config.HostFor(name), null, ct);
        TryDelete(hosting.DeploymentDir(name));
        logger.LogInformation("deleted {Name}", name);
    }

    /// <summary>nginx host map + Cloudflare record for one deployment.</summary>
    public async Task EnsureRouting(string name, TextWriter? log, CancellationToken ct)
    {
        await hosting.WriteNginxMaps(state.All());
        if (!cloudflare.Configured)
        {
            log?.WriteLine("DNS: Cloudflare is not configured; create the record by hand or add the token");
            return;
        }
        var managed = await cloudflare.EnsureRecord(config.HostFor(name), log, ct);
        state.Update(name, x => x.DnsManaged = x.DnsManaged || managed);
    }

    /// <summary>The branch's default key, read from the published release (publish copies wwwroot).</summary>
    private string? DefaultDbKey(string release)
    {
        var path = Path.Combine(release, config.DbKeyFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private string? CachedBuild(string sha)
    {
        var dir = Path.Combine(config.BuildsDir, sha);
        return File.Exists(Path.Combine(dir, ".complete")) ? dir : null;
    }

    /// <summary>Keeps a hard-linked copy of a fresh publish (before any database is linked in) for other deployments of the commit.</summary>
    private async Task CacheBuild(string sha, string release, TextWriter log, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(config.BuildsDir);
            var dir = Path.Combine(config.BuildsDir, sha);
            var tmp = dir + ".tmp";
            TryDelete(tmp);
            if (!Directory.Exists(dir))
            {
                await Shell.Check("/usr/bin/cp", ["-al", release, tmp], ct: ct);
                await File.WriteAllTextAsync(Path.Combine(tmp, ".complete"), DateTimeOffset.UtcNow.ToString("u"), ct);
                Directory.Move(tmp, dir);
                log.WriteLine($"cached the build of {sha[..7]} for other deployments");
            }
            var inUse = state.All().Select(x => x.Commit).Where(c => c != null).ToHashSet();
            inUse.Add(sha);
            foreach (var old in Directory.GetDirectories(config.BuildsDir)
                         .Where(p => !p.EndsWith(".tmp") && !inUse.Contains(Path.GetFileName(p)))
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Skip(config.KeepBuilds))
                TryDelete(old);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.WriteLine($"build cache: {e.Message}");   // a cache problem never fails the deploy
        }
    }

    private async Task EnsureDb(string data, string file, string key, TextWriter log, CancellationToken ct)
    {
        var path = Path.Combine(data, file);
        var keyFile = path + ".key";
        if (File.Exists(path) && File.Exists(keyFile) && File.ReadAllText(keyFile).Trim() == key)
        {
            log.WriteLine($"{file}: {key} already present");
            return;
        }
        var size = (await s3.List(ct: ct)).FirstOrDefault(o => o.Key == key)?.Size
            ?? throw new InvalidOperationException($"s3 key '{key}' not found in the bucket");
        var free = new DriveInfo(data).AvailableFreeSpace;
        if (free < size + (1L << 30))
            throw new InvalidOperationException($"not enough disk space for {key}: {size >> 20} MB needed, {free >> 20} MB free (+1 GB reserve)");
        await s3.Download(key, path, log, ct);
        await File.WriteAllTextAsync(keyFile, key, ct);
    }

    private static void RemoveDb(string data, string file)
    {
        File.Delete(Path.Combine(data, file));
        File.Delete(Path.Combine(data, file + ".key"));
    }

    private static async Task SwitchCurrent(string dir, string release)
    {
        var tmp = Path.Combine(dir, "current.tmp");
        await Shell.Check("/usr/bin/ln", ["-sfn", release, tmp]);
        await Shell.Check("/usr/bin/mv", ["-Tf", tmp, Path.Combine(dir, "current")]); // atomic rename over the old link
    }

    private async Task<bool> WaitHealthy(string name, int port, TextWriter log, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("health");
        var deadline = DateTime.UtcNow.AddSeconds(config.HealthTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/", ct);
                if ((int)response.StatusCode < 500)
                {
                    log.WriteLine($"health: {(int)response.StatusCode} from http://127.0.0.1:{port}/");
                    return true;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            var unit = await hosting.State(name);
            if (unit.ActiveState is "failed" || (unit.ActiveState is "inactive" && unit.SubState is "dead")) return false;
        }
        return false;
    }

    private void CleanupReleases(string releases, string current)
    {
        foreach (var old in Directory.GetDirectories(releases).Where(r => r != current).OrderByDescending(r => r).Skip(Math.Max(0, config.KeepReleases - 1)))
            TryDelete(old);
    }

    private void RotateAppLog(string name)
    {
        var log = hosting.AppLog(name);
        if (File.Exists(log) && new FileInfo(log).Length > (20L << 20)) File.Move(log, log + ".1", overwrite: true);
    }

    public static string Tail(string path, int lines)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - 512 * 1024);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd().Split('\n');
        return string.Join('\n', text.TakeLast(lines));
    }

    private void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) { logger.LogWarning("cannot delete {Path}: {Message}", path, e.Message); }
    }
}
