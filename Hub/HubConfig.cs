namespace PortableHub;

/// <summary>/etc/portable-hub/hub.json (path overridable with PORTABLE_HUB_CONFIG).</summary>
public sealed class HubConfig
{
    public string RepoUrl { get; set; } = "https://github.com/BeatLeader/portaBLe.git";
    public string RepoWebUrl { get; set; } = "https://github.com/BeatLeader/portaBLe";
    /// <summary>Project to publish, relative to the repository root.</summary>
    public string ProjectFile { get; set; } = "portaBLe.csproj";
    /// <summary>File in the repository naming the branch's default S3 database key.</summary>
    public string DbKeyFile { get; set; } = "wwwroot/current_db_name.txt";

    public string Zone { get; set; } = "beatleader.pro";
    public string HubHost { get; set; } = "portable.beatleader.pro";
    /// <summary>{name} is replaced by the deployment name. Keep it a first-level subdomain so the zone's edge certificate covers it.</summary>
    public string HostPattern { get; set; } = "portable-{name}.beatleader.pro";
    /// <summary>Address the DNS records point to (proxied through Cloudflare).</summary>
    public string PublicIp { get; set; } = "";
    public string CloudflareTokenFile { get; set; } = "/etc/portable-hub/cloudflare.token";
    public string? GitHubWebhookSecret { get; set; }

    public string DataDir { get; set; } = "/srv/portable";
    /// <summary>Where the hub writes hosts.map / auth.map (the only nginx input it controls).</summary>
    public string NginxDir { get; set; } = "/etc/portable-hub/maps";
    public string Dotnet { get; set; } = "/usr/bin/dotnet";
    public int FirstPort { get; set; } = 5101;
    public int PollSeconds { get; set; } = 60;
    public int KeepReleases { get; set; } = 2;
    public int HealthTimeoutSeconds { get; set; } = 180;
    /// <summary>systemd MemoryMax for each deployment.</summary>
    public string MemoryMax { get; set; } = "1500M";

    public S3Config S3 { get; set; } = new();

    public string HostFor(string name) => HostPattern.Replace("{name}", name);
    public string DeploymentsDir => Path.Combine(DataDir, "deployments");
    public string StateFile => Path.Combine(DataDir, "hub", "state.json");
    public string MirrorDir => Path.Combine(DataDir, "mirror.git");
}

public sealed class S3Config
{
    public string Bucket { get; set; } = "portabledbs";
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}
