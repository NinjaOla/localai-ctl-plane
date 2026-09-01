namespace llamactl.Contracts;

public static class Protocol
{
    public const int SchemaVersion = 1;
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