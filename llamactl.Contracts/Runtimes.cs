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