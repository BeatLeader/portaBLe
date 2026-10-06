using System.Text.Json.Serialization;

namespace PortableHub;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeployStatus { Queued, Building, Running, Failed, Stopped }

public sealed class Deployment
{
    public string Name { get; set; } = "";
    public string Branch { get; set; } = "";
    public int Port { get; set; }
    /// <summary>S3 key of Database.db; null = the branch default (wwwroot/current_db_name.txt).</summary>
    public string? DbKey { get; set; }
    /// <summary>S3 key of Comparison.db; null = none.</summary>
    public string? ComparisonKey { get; set; }
    /// <summary>Extra command-line arguments for the app.</summary>
    public string Args { get; set; } = "";
    public bool AutoDeploy { get; set; } = true;
    /// <summary>Require the hub's basic-auth login for the deployment's site too.</summary>
    public bool Protected { get; set; }
    public string Notes { get; set; } = "";

    public DeployStatus Status { get; set; } = DeployStatus.Queued;
    /// <summary>What the worker is doing right now (null when idle).</summary>
    public string? Activity { get; set; }
    public string? Error { get; set; }
    public string? Commit { get; set; }
    public string? CommitSubject { get; set; }
    /// <summary>Commit whose deploy failed; auto-deploy skips it until a newer push.</summary>
    public string? FailedCommit { get; set; }
    public string? ActiveDbKey { get; set; }
    public string? ActiveComparisonKey { get; set; }
    public string? Release { get; set; }
    public bool DnsManaged { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeployedAt { get; set; }
    public string? LastLog { get; set; }
}

public sealed record BranchInfo(string Name, string Sha, DateTimeOffset Date, string Author, string Subject);

public sealed record S3Db(string Key, long Size, DateTimeOffset Modified);

/// <summary>Body of create/update requests.</summary>
public sealed class DeploymentRequest
{
    public string? Name { get; set; }
    public string? Branch { get; set; }
    public string? DbKey { get; set; }
    public string? ComparisonKey { get; set; }
    public string? Args { get; set; }
    public bool? AutoDeploy { get; set; }
    public bool? Protected { get; set; }
    public string? Notes { get; set; }
}
