using System.Collections.Concurrent;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Guid> connections = new();
    private readonly ConcurrentDictionary<Guid, string> nodes = new();

    public void Add(string connectionId, Guid nodeId)
    {
        connections[connectionId] = nodeId;
        nodes[nodeId] = connectionId;
    }

    public bool Remove(string connectionId, out Guid nodeId)
    {
        if (!connections.TryRemove(connectionId, out nodeId))
            return false;
        nodes.TryRemove(new KeyValuePair<Guid, string>(nodeId, connectionId));
        return true;
    }

    public bool TryGetNodeId(string connectionId, out Guid nodeId) =>
        connections.TryGetValue(connectionId, out nodeId);

    public bool TryGetConnectionId(Guid nodeId, out string? connectionId) =>
        nodes.TryGetValue(nodeId, out connectionId);
}