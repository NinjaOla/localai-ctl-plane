namespace llamactl.Contracts;

public static class Protocol
{
    public const int SchemaVersion = 1;
    public const string AgentHubPath = "/hubs/agent";
    public const string NodeIdHeader = "X-Llamactl-Node-Id";
    public const string BootstrapTokenHeader = "X-Llamactl-Bootstrap-Token";
}

public sealed record Envelope<T>(
    Guid MessageId,
    Guid? CorrelationId,
    Guid NodeId,
    int SchemaVersion,
    DateTimeOffset SentAt,
    T Payload);

public sealed record AgentCommand(string Name, object? Payload);

public sealed record AgentResult(bool Succeeded, string? Error, object? Payload);