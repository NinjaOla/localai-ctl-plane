using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class InstanceLogBuffer(TimeProvider timeProvider)
{
    internal const int Capacity = 2_000;
    private readonly ConcurrentDictionary<Guid, Sequence> sequences = new();
    private readonly Channel<ProcessLogLine> channel = Channel.CreateBounded<ProcessLogLine>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Write(Guid instanceId, ProcessLogStream stream, string text)
    {
        var sequence = sequences.GetOrAdd(instanceId, static _ => new()).Next();
        channel.Writer.TryWrite(new(instanceId, sequence, timeProvider.GetUtcNow(), stream, text));
    }

    public async IAsyncEnumerable<IReadOnlyList<ProcessLogLine>> ReadBatchesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            var batch = new List<ProcessLogLine>(64);
            while (batch.Count < 64 && channel.Reader.TryRead(out var line))
                batch.Add(line);
            if (batch.Count > 0)
                yield return batch;
        }
    }

    private sealed class Sequence
    {
        private long value;
        public long Next() => Interlocked.Increment(ref value);
    }
}