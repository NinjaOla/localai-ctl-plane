namespace llamactl.Contracts;

public sealed record NodePaths(
    string LlamaBin,
    string LlamaSource,
    string Rocm,
    string ModelsRoot,
    string HfHome,
    string FlatDir,
    string PresetFile,
    string EmptyCache,
    string SystemdDir,
    string ConfigRepo);

public sealed record PortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;
}

public sealed record NodeConfiguration(
    NodePaths Paths,
    long VramBudgetMiB,
    PortRange PortRange,
    int DefaultGpuLayers,
    bool JinjaEnabled);

public sealed record MountedFileSystem(
    string MountPoint,
    long TotalBytes,
    long FreeBytes,
    bool Writable);

public sealed record NodeDescription(
    string Hostname,
    string OperatingSystem,
    string KernelVersion,
    string? GpuName,
    long? VramTotalMiB,
    string? RocmVersion,
    string? LlamaCppVersion,
    IReadOnlyList<MountedFileSystem> FileSystems);

public sealed record PathProposal(
    string Name,
    string Path,
    string Reason);

public sealed record AgentAnnouncement(
    NodeDescription Description,
    IReadOnlyList<PathProposal> PathProposals,
    IReadOnlyList<RuntimeDescriptor> Runtimes);

public sealed record AgentHeartbeat(
    IReadOnlyList<ValidationIssue> ValidationIssues);

public enum NodeHealth
{
    PendingConfiguration,
    Healthy,
    Degraded,
    Unreachable
}

public sealed record ValidationIssue(
    string Code,
    string Message,
    ValidationSeverity Severity);

public enum ValidationSeverity
{
    Information,
    Warning,
    Error
}