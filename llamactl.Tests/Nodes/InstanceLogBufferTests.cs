using llamactl.Agent;
using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;

namespace llamactl.Tests.Nodes;

public sealed class InstanceLogBufferTests
{
    [Fact]
    public async Task Agent_buffer_sequences_per_instance_and_drops_oldest_at_capacity()
    {
        var instanceId = Guid.NewGuid();
        var buffer = new InstanceLogBuffer(TimeProvider.System);
        for (var index = 1; index <= InstanceLogBuffer.Capacity + 5; index++)
            buffer.Write(instanceId, ProcessLogStream.StandardOutput, $"line-{index}");

        var received = new List<ProcessLogLine>();
        await using var batches = buffer.ReadBatchesAsync(CancellationToken.None).GetAsyncEnumerator();
        while (received.Count < InstanceLogBuffer.Capacity && await batches.MoveNextAsync())
            received.AddRange(batches.Current);

        Assert.Equal(InstanceLogBuffer.Capacity, received.Count);
        Assert.Equal(6, received[0].Sequence);
        Assert.Equal(InstanceLogBuffer.Capacity + 5, received[^1].Sequence);
    }

    [Fact]
    public void Web_store_is_bounded_and_suppresses_duplicate_sequences()
    {
        var instanceId = Guid.NewGuid();
        var store = new InstanceLogStore();
        var lines = Enumerable.Range(1, InstanceLogStore.Capacity + 5)
            .Select(index => new ProcessLogLine(
                instanceId, index, DateTimeOffset.UtcNow, ProcessLogStream.StandardOutput, $"line-{index}"))
            .ToList();

        store.Append(lines);
        store.Append([lines[^1]]);
        var retained = store.Read(instanceId);

        Assert.Equal(InstanceLogStore.Capacity, retained.Count);
        Assert.Equal(6, retained[0].Sequence);
        Assert.Equal(InstanceLogStore.Capacity + 5, retained[^1].Sequence);
    }
}