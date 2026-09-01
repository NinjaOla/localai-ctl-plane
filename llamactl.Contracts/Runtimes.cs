namespace llamactl.Contracts;

public enum RuntimeId
{
    LlamaCpp = 1
}

[Flags]
public enum RuntimeCapabilities
{
    None = 0,
    MultiModelRouting = 1 << 0,
    OnDemandLoad = 1 << 1,
    PerModelConfig = 1 << 2,
    SpeculativeDecode = 1 << 3,
    Multimodal = 1 << 4,
    SlotIntrospection = 1 << 5,
    PrometheusMetrics = 1 << 6,
    NativeBenchmark = 1 << 7,
    SelfManagedModels = 1 << 8,
    InPlaceUpgrade = 1 << 9
}

public enum ConfigFormat
{
    LlamaCppIni,
    Yaml,
    CliArgs,
    Modelfile
}

public enum ModelFormat
{
    Gguf,
    Safetensors,
    RuntimeNative
}

public sealed record RuntimeDescriptor(
    RuntimeId Id,
    string DisplayName,
    string? Version,
    string? BinPath,
    bool Installed,
    ConfigFormat ConfigFormat,
    IReadOnlySet<ModelFormat> ModelFormats,
    RuntimeCapabilities Capabilities,
    IReadOnlyDictionary<string, string> FlagSchema);

public enum InstanceKind
{
    Managed,
    Ephemeral
}

public sealed record InstanceSpec(
    string Name,
    RuntimeId Runtime,
    InstanceKind Kind,
    string? Profile,
    string? ModelRef,
    string? ConfigRef,
    int? Port,
    IReadOnlyDictionary<string, string?> Args,
    bool Persistent);

public enum DesiredInstanceState
{
    Stopped,
    Running
}

public enum ObservedInstanceState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Failed
}

public sealed record DesiredInstance(
    Guid Id,
    long Revision,
    InstanceSpec Spec,
    DesiredInstanceState State,
    int? AdoptProcessId);

public sealed record AgentDesiredState(
    long Version,
    NodeConfiguration? Configuration,
    IReadOnlyList<DesiredInstance> Instances);

public sealed record ObservedInstance(
    Guid Id,
    long Revision,
    ObservedInstanceState State,
    int? ProcessId,
    string? Error);

public sealed record ReconciliationReport(
    long DesiredStateVersion,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    IReadOnlyList<ObservedInstance> Instances);

public enum ProcessLogStream
{
    StandardOutput,
    StandardError
}

public sealed record ProcessLogLine(
    Guid InstanceId,
    long Sequence,
    DateTimeOffset Timestamp,
    ProcessLogStream Stream,
    string Text);