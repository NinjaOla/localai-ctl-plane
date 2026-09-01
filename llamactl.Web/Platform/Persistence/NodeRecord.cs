using llamactl.Contracts;

namespace llamactl.Web.Platform.Persistence;

public sealed class NodeRecord
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public required string BootstrapTokenHash { get; init; }
    public NodeHealth Health { get; set; } = NodeHealth.PendingConfiguration;
    public DateTimeOffset? LastSeen { get; set; }
    public string? GpuName { get; set; }
    public long? VramTotalMiB { get; set; }
    public string? LlamaCppVersion { get; set; }
    public string? RocmVersion { get; set; }
    public string? AnnouncementJson { get; set; }
    public string? ConfigurationJson { get; set; }
    public string? ValidationIssuesJson { get; set; }
    public long DesiredStateVersion { get; set; }
    public long Version { get; set; }
}