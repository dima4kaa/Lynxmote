namespace ProxMonitor.Core;

public enum ResourceKind { Node, Vm, Container }

public enum PowerAction { Start, Stop, Reboot, Snapshot }

/// <summary>One node, VM or container at a moment in time. Shape follows Proxmox /cluster/resources.</summary>
public sealed record ResourceSnapshot(
    string Id,
    string Name,
    ResourceKind Kind,
    string Node,
    bool Running,
    double Cpu, // 0..1
    long MemUsed,
    long MemTotal,
    long DiskUsed,
    long DiskTotal,
    double? TempC, // needs SSH + lm-sensors on real nodes
    long NetIn, // bytes/s
    long NetOut,
    int? Connections, // needs SSH on real nodes
    TimeSpan Uptime);

/// <summary>Where snapshots come from: the demo cluster or a real Proxmox server.</summary>
public interface IResourceSource
{
    /// <summary>Stable id, used to remember which tab a resource belongs to.</summary>
    string Key { get; }
    string Title { get; }
    TimeSpan Interval { get; }
    Task<IReadOnlyList<ResourceSnapshot>> FetchAsync(CancellationToken ct);
    Task<string> ApplyAsync(string id, PowerAction action, CancellationToken ct);
}
