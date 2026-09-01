using System.Collections.Concurrent;
using llamactl.Contracts;

namespace llamactl.Web.Platform.NodeGateway;

public sealed class DownloadProgressStore
{
    internal const int Capacity = 256;
    private readonly ConcurrentDictionary<Guid, (Guid NodeId, DownloadProgress Progress)> progress = new();
    private readonly ConcurrentQueue<Guid> insertionOrder = new();

    public void Update(Guid nodeId, IEnumerable<DownloadProgress> updates)
    {
        foreach (var update in updates)
        {
            if (progress.TryAdd(update.Id, (nodeId, update))) insertionOrder.Enqueue(update.Id);
            else progress[update.Id] = (nodeId, update);
        }
        while (progress.Count > Capacity && insertionOrder.TryDequeue(out var oldest)) progress.TryRemove(oldest, out _);
    }

    public IReadOnlyList<DownloadProgress> Read(Guid nodeId) => progress.Values.Where(item => item.NodeId == nodeId).Select(item => item.Progress)
        .OrderByDescending(item => item.State == DownloadState.Running).ThenBy(item => item.Id).ToList();
}