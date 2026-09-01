using System.Text.Json;
using llamactl.Contracts;

namespace llamactl.Tests.Contracts;

public sealed class ContractSerializationTests
{
    [Fact]
    public void Envelope_round_trips_node_description()
    {
        var messageId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;
        var envelope = new Envelope<NodeDescription>(
            messageId,
            null,
            nodeId,
            Protocol.SchemaVersion,
            sentAt,
            new NodeDescription(
                "node-01",
                "Ubuntu 24.04",
                "6.8.0",
                "Radeon 8060S",
                98_304,
                "7.2.1",
                "b8342",
                [new MountedFileSystem("/models", 2_000, 1_500, true)]));

        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<Envelope<NodeDescription>>(json);

        Assert.NotNull(restored);
        Assert.Equal(envelope.MessageId, restored.MessageId);
        Assert.Equal(envelope.CorrelationId, restored.CorrelationId);
        Assert.Equal(envelope.NodeId, restored.NodeId);
        Assert.Equal(Protocol.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(envelope.SentAt, restored.SentAt);
        Assert.Equal(envelope.Payload.Hostname, restored.Payload.Hostname);
        Assert.Equal(envelope.Payload.OperatingSystem, restored.Payload.OperatingSystem);
        Assert.Equal(envelope.Payload.KernelVersion, restored.Payload.KernelVersion);
        Assert.Equal(envelope.Payload.GpuName, restored.Payload.GpuName);
        Assert.Equal(envelope.Payload.VramTotalMiB, restored.Payload.VramTotalMiB);
        Assert.Equal(envelope.Payload.RocmVersion, restored.Payload.RocmVersion);
        Assert.Equal(envelope.Payload.LlamaCppVersion, restored.Payload.LlamaCppVersion);
        Assert.Equal(envelope.Payload.FileSystems, restored.Payload.FileSystems);
    }

    [Theory]
    [InlineData(48_000, true)]
    [InlineData(48_999, true)]
    [InlineData(47_999, false)]
    [InlineData(49_000, false)]
    public void Port_range_includes_only_configured_ports(int port, bool expected)
    {
        var range = new PortRange(48_000, 48_999);

        Assert.Equal(expected, range.Contains(port));
    }
}