using Microsoft.AspNetCore.SignalR;

namespace llamactl.Web.Platform.NodeGateway;

public interface IAgentPresetGateway
{
    Task<string?> ReadAsync(Guid nodeId, CancellationToken cancellationToken);
    Task<bool> WriteAsync(Guid nodeId, string content, CancellationToken cancellationToken);
}

internal sealed class AgentPresetGateway(
    AgentConnectionRegistry connections,
    IHubContext<AgentHub> hub) : IAgentPresetGateway
{
    public async Task<string?> ReadAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        if (!connections.TryGetConnectionId(nodeId, out var connectionId) || connectionId is null)
            return null;
        return await hub.Clients.Client(connectionId).InvokeAsync<string>("ReadPreset", cancellationToken);
    }

    public async Task<bool> WriteAsync(Guid nodeId, string content, CancellationToken cancellationToken)
    {
        if (!connections.TryGetConnectionId(nodeId, out var connectionId) || connectionId is null)
            return false;
        return await hub.Clients.Client(connectionId).InvokeAsync<bool>("WritePreset", content, cancellationToken);
    }
}