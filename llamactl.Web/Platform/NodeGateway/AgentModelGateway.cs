using llamactl.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace llamactl.Web.Platform.NodeGateway;

public interface IAgentModelGateway
{
    Task<ModelInventory?> ScanAsync(Guid nodeId, CancellationToken cancellationToken);
    Task<ReconcileLibraryResult?> ReconcileAsync(Guid nodeId, bool dryRun, CancellationToken cancellationToken);
    Task<GgufInspection?> InspectAsync(Guid nodeId, InspectGgufRequest request, CancellationToken cancellationToken);
    Task<DeleteModelResult?> DeleteAsync(Guid nodeId, DeleteModelRequest request, CancellationToken cancellationToken);
    Task<Guid?> StartDownloadAsync(Guid nodeId, StartModelDownload request, CancellationToken cancellationToken);
    Task<bool> CancelDownloadAsync(Guid nodeId, Guid downloadId, CancellationToken cancellationToken);
}

internal sealed class AgentModelGateway(AgentConnectionRegistry connections, IHubContext<AgentHub> hub) : IAgentModelGateway
{
    public Task<ModelInventory?> ScanAsync(Guid nodeId, CancellationToken token) => InvokeReferenceAsync<ModelInventory>(nodeId, "ScanModels", token);
    public Task<ReconcileLibraryResult?> ReconcileAsync(Guid nodeId, bool dryRun, CancellationToken token) => InvokeReferenceAsync<ReconcileLibraryResult>(nodeId, "ReconcileFlatDir", token, dryRun);
    public Task<GgufInspection?> InspectAsync(Guid nodeId, InspectGgufRequest request, CancellationToken token) => InvokeReferenceAsync<GgufInspection>(nodeId, "InspectGguf", token, request);
    public Task<DeleteModelResult?> DeleteAsync(Guid nodeId, DeleteModelRequest request, CancellationToken token) => InvokeReferenceAsync<DeleteModelResult>(nodeId, "DeleteModel", token, request);
    public async Task<Guid?> StartDownloadAsync(Guid nodeId, StartModelDownload request, CancellationToken token) => await InvokeValueAsync<Guid>(nodeId, "StartDownload", token, request);
    public async Task<bool> CancelDownloadAsync(Guid nodeId, Guid downloadId, CancellationToken token) => await InvokeValueAsync<bool>(nodeId, "CancelDownload", token, downloadId) ?? false;

    private async Task<T?> InvokeValueAsync<T>(Guid nodeId, string method, CancellationToken token, object? argument = null) where T : struct
    {
        if (!connections.TryGetConnectionId(nodeId, out var connectionId) || connectionId is null) return null;
        return argument is null
            ? await hub.Clients.Client(connectionId).InvokeAsync<T>(method, token)
            : await hub.Clients.Client(connectionId).InvokeAsync<T>(method, argument, token);
    }

    private async Task<T?> InvokeReferenceAsync<T>(Guid nodeId, string method, CancellationToken token, object? argument = null) where T : class
    {
        if (!connections.TryGetConnectionId(nodeId, out var connectionId) || connectionId is null) return null;
        return argument is null
            ? await hub.Clients.Client(connectionId).InvokeAsync<T>(method, token)
            : await hub.Clients.Client(connectionId).InvokeAsync<T>(method, argument, token);
    }
}