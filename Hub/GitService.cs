using System.Globalization;

namespace PortableHub;

/// <summary>
/// Branch list from a blobless bare mirror (cheap to fetch every poll), and per-deployment shallow checkouts with submodules.
/// </summary>
public sealed class GitService(HubConfig config, ILogger<GitService> logger)
{
    private const string Git = "/usr/bin/git";
    private readonly SemaphoreSlim _mirrorLock = new(1, 1);
    private volatile IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();

    public IReadOnlyList<BranchInfo> Branches => _branches;
    public DateTimeOffset? LastFetch { get; private set; }
    public string? LastFetchError { get; private set; }

    public BranchInfo? Branch(string name) => _branches.FirstOrDefault(b => b.Name == name);

    public async Task<IReadOnlyList<BranchInfo>> Refresh(CancellationToken ct = default)
    {
        await _mirrorLock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(config.MirrorDir))
                await Shell.Check(Git, ["clone", "--bare", "--filter=blob:none", config.RepoUrl, config.MirrorDir], ct: ct);
            await Shell.Check(Git, ["-C", config.MirrorDir, "fetch", "--prune", "--filter=blob:none", config.RepoUrl,
                "+refs/heads/*:refs/heads/*"], timeout: TimeSpan.FromMinutes(5), ct: ct);
            var output = await Shell.Check(Git, ["-C", config.MirrorDir, "for-each-ref", "refs/heads", "--sort=-committerdate",
                "--format=%(refname:short)%09%(objectname)%09%(committerdate:iso-strict)%09%(authorname)%09%(subject)"], ct: ct);
            _branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\t'))
                .Where(f => f.Length >= 5)
                .Select(f => new BranchInfo(f[0], f[1], DateTimeOffset.Parse(f[2], CultureInfo.InvariantCulture), f[3], f[4]))
                .ToList();
            LastFetch = DateTimeOffset.UtcNow;
            LastFetchError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastFetchError = e.Message;
            logger.LogWarning("git fetch failed: {Message}", e.Message);
        }
        finally
        {
            _mirrorLock.Release();
        }
        return _branches;
    }

    /// <summary>Checks out the tip of <paramref name="branch"/> (with submodules) in <paramref name="srcDir"/>; returns (sha, subject).</summary>
    public async Task<(string Sha, string Subject)> Checkout(string srcDir, string branch, TextWriter log, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(15);
        if (!Directory.Exists(Path.Combine(srcDir, ".git")))
        {
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
            await Shell.Check(Git, ["clone", "--depth", "1", "--branch", branch, "--recurse-submodules", "--shallow-submodules",
                config.RepoUrl, srcDir], log: log, timeout: timeout, ct: ct);
        }
        else
        {
            await Shell.Check(Git, ["-C", srcDir, "fetch", "--depth", "1", config.RepoUrl, $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
                log: log, timeout: timeout, ct: ct);
            await Shell.Check(Git, ["-C", srcDir, "reset", "--hard", $"origin/{branch}"], log: log, ct: ct);
            // untracked files go, ignored build caches (bin/obj) stay
            await Shell.Check(Git, ["-C", srcDir, "clean", "-ffd"], log: log, ct: ct);
            await Shell.Check(Git, ["-C", srcDir, "submodule", "sync", "--recursive"], log: log, ct: ct);
            await Shell.Check(Git, ["-C", srcDir, "submodule", "update", "--init", "--recursive", "--force", "--depth", "1"],
                log: log, timeout: timeout, ct: ct);
        }
        var head = (await Shell.Check(Git, ["-C", srcDir, "log", "-1", "--format=%H%x09%s"], ct: ct)).Trim().Split('\t', 2);
        log.WriteLine($"checked out {branch} @ {head[0]}");
        return (head[0], head.Length > 1 ? head[1] : "");
    }
}
