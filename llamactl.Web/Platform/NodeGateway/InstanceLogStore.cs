using System.Collections.Concurrent;
using llamactl.Contracts;

namespace llamactl.Web.Platform.NodeGateway;

public sealed class InstanceLogStore
{
    internal const int Capacity = 2_000;
    private readonly ConcurrentDictionary<Guid, LogBuffer> buffers = new();

    public void Append(IReadOnlyList<ProcessLogLine> lines)
    {
        foreach (var group in lines.GroupBy(line => line.InstanceId))
            buffers.GetOrAdd(group.Key, static _ => new()).Append(group);
    }

    public IReadOnlyList<ProcessLogLine> Read(Guid instanceId, long afterSequence = 0) =>
        buffers.TryGetValue(instanceId, out var buffer) ? buffer.Read(afterSequence) : [];

    private sealed class LogBuffer
    {
        private readonly Queue<ProcessLogLine> lines = new(Capacity);
        private readonly Lock gate = new();
        private long highestSequence;

        public void Append(IEnumerable<ProcessLogLine> incoming)
        {
            lock (gate)
            {
                foreach (var line in incoming.OrderBy(line => line.Sequence))
                {
                    if (line.Sequence <= highestSequence)
                        continue;
                    lines.Enqueue(line);
                    highestSequence = line.Sequence;
                    while (lines.Count > Capacity)
                        lines.Dequeue();
                }
            }
        }

        public IReadOnlyList<ProcessLogLine> Read(long afterSequence)
        {
            lock (gate)
                return lines.Where(line => line.Sequence > afterSequence).ToList();
        }
    }
}