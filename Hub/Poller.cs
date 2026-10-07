namespace PortableHub;

/// <summary>
/// Every PollSeconds (or right away after a GitHub push webhook): fetch the branch list and queue a redeploy for every
/// auto-deploy deployment whose branch moved. A commit that failed to deploy is skipped until the branch moves again.
/// </summary>
public sealed class Poller(HubConfig config, StateStore state, GitService git, Deployer deployer, CloudflareService cloudflare,
    ILogger<Poller> logger) : BackgroundService
{
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private bool _hubDnsChecked;
    private readonly HashSet<string> _dnsTried = new();

    /// <summary>Last correctly signed GitHub webhook delivery (any event, including GitHub's ping).</summary>
    public DateTimeOffset? LastWebhook { get; set; }

    public void Trigger()
    {
        try { _trigger.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Poll(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "poll failed");
            }
            await _trigger.WaitAsync(TimeSpan.FromSeconds(config.PollSeconds), stoppingToken);
        }
    }

    private async Task Poll(CancellationToken ct)
    {
        await git.Refresh(ct);
        if (cloudflare.Configured)
        {
            if (!_hubDnsChecked)
                _hubDnsChecked = await cloudflare.EnsureRecord(config.HubHost, null, ct) || cloudflare.Status == "ok";
            // deployments made before the token existed (or while Cloudflare was failing) get their record now; once per hub run
            foreach (var d in state.All().Where(d => !d.DnsManaged && d.Release != null && d.Activity == null && _dnsTried.Add(d.Name)))
                await deployer.EnsureRouting(d.Name, null, ct);
        }
        foreach (var d in state.All())
        {
            if (!d.AutoDeploy || d.Status is DeployStatus.Stopped || d.Activity != null || deployer.IsPending(d.Name)) continue;
            var tip = git.Branch(d.Branch)?.Sha;
            if (tip == null || tip == d.Commit || tip == d.FailedCommit || tip == d.SkippedCommit) continue;
            if (d.Commit != null && await git.ChangedFiles(d.Commit, tip, ct) is { } files && files.All(config.IsIgnoredPath))
            {
                // e.g. a push that only touched Analysis/ or docs: the running build is still current
                state.Update(d.Name, x => { x.SkippedCommit = tip; x.SkippedFiles = files.Count; });
                logger.LogInformation("{Name}: {Tip} only changes ignored paths ({Count} files), not redeploying", d.Name, tip[..7], files.Count);
                continue;
            }
            deployer.Enqueue(d.Name, JobKind.Deploy, d.Commit == null ? "first deploy" : $"{d.Branch} moved to {tip[..7]}");
        }
    }
}
