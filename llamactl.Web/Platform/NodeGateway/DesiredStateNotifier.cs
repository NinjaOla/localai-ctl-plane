using Microsoft.AspNetCore.SignalR;

namespace llamactl.Web.Platform.NodeGateway;

public interface IDesiredStateNotifier
{
    Task NotifyAsync(Guid nodeId, long version, CancellationToken cancellationToken);
}

internal sealed class DesiredStateNotifier(IHubContext<AgentHub> hub) : IDesiredStateNotifier
{
    public Task NotifyAsync(Guid nodeId, long version, CancellationToken cancellationToken) =>
        hub.Clients.Group(nodeId.ToString()).SendAsync(
            "DesiredStateChanged",
            version,
            cancellationToken);
}