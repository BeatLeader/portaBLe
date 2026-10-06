using System.Text.Json;

namespace PortableHub;

/// <summary>Deployments, persisted as JSON. All access goes through the lock; readers get deep copies.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<Deployment> _deployments;

    public StateStore(HubConfig config)
    {
        _path = config.StateFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _deployments = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<Deployment>>(File.ReadAllText(_path), Json) ?? new()
            : new();
        // a hub restart drops the job queue and interrupts any running job (the poller re-queues auto-deploys)
        foreach (var d in _deployments)
        {
            if (d.Status is DeployStatus.Building or DeployStatus.Queued)
            {
                d.Status = d.Release != null ? DeployStatus.Running : DeployStatus.Failed;
                d.Error = "interrupted by a hub restart";
            }
            d.Activity = null;
        }
        Save();
    }

    public List<Deployment> All()
    {
        lock (_lock) return Clone(_deployments);
    }

    public Deployment? Get(string name)
    {
        lock (_lock) return _deployments.FirstOrDefault(d => d.Name == name) is { } d ? Clone(d) : null;
    }

    public void Add(Deployment d)
    {
        lock (_lock)
        {
            if (_deployments.Any(x => x.Name == d.Name)) throw new InvalidOperationException($"deployment '{d.Name}' already exists");
            _deployments.Add(Clone(d));
            Save();
        }
    }

    /// <summary>Applies <paramref name="change"/> to the stored deployment and saves; returns a copy (null if it no longer exists).</summary>
    public Deployment? Update(string name, Action<Deployment> change)
    {
        lock (_lock)
        {
            var d = _deployments.FirstOrDefault(x => x.Name == name);
            if (d == null) return null;
            change(d);
            Save();
            return Clone(d);
        }
    }

    public void Remove(string name)
    {
        lock (_lock)
        {
            _deployments.RemoveAll(d => d.Name == name);
            Save();
        }
    }

    public int NextPort(int first)
    {
        lock (_lock)
        {
            var used = _deployments.Select(d => d.Port).ToHashSet();
            var port = first;
            while (used.Contains(port)) port++;
            return port;
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_deployments, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
