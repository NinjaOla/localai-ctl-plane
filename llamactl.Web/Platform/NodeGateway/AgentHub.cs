using llamactl.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentHub(
    AgentConnectionAuthenticator authenticator,
    AgentConnectionRegistry connections,
    AgentAnnouncementReceiver announcements,
    AgentHeartbeatReceiver heartbeats,
    DesiredStateStore desiredState,
    AgentLogReceiver logs,
    DownloadProgressStore downloads) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var nodeIdValue = httpContext?.Request.Headers[Protocol.NodeIdHeader].ToString();
        var bootstrapToken = httpContext?.Request.Headers[Protocol.BootstrapTokenHeader].ToString();

        if (!Guid.TryParse(nodeIdValue, out var nodeId)
            || string.IsNullOrWhiteSpace(bootstrapToken)
            || !await authenticator.AuthenticateAsync(nodeId, bootstrapToken, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        connections.Add(Context.ConnectionId, nodeId);
        await Groups.AddToGroupAsync(Context.ConnectionId, nodeId.ToString(), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task Announce(Envelope<AgentAnnouncement> envelope)
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId))
            throw new HubException("Agent connection is not authenticated.");

        try
        {
            await announcements.ReceiveAsync(nodeId, envelope, Context.ConnectionAborted);
        }
        catch (InvalidOperationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public async Task Heartbeat(Envelope<AgentHeartbeat> envelope)
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId))
            throw new HubException("Agent connection is not authenticated.");

        try
        {
            await heartbeats.ReceiveAsync(nodeId, envelope, Context.ConnectionAborted);
        }
        catch (InvalidOperationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public Task<AgentDesiredState> GetDesiredState()
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId))
            throw new HubException("Agent connection is not authenticated.");
        return desiredState.GetAsync(nodeId, Context.ConnectionAborted);
    }

    public async Task ReportReconciliation(ReconciliationReport report)
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId))
            throw new HubException("Agent connection is not authenticated.");
        await desiredState.ReportAsync(nodeId, report, Context.ConnectionAborted);
    }

    public async Task PublishLogs(IReadOnlyList<ProcessLogLine> lines)
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId))
            throw new HubException("Agent connection is not authenticated.");
        try
        {
            await logs.ReceiveAsync(nodeId, lines, Context.ConnectionAborted);
        }
        catch (InvalidOperationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public void PublishDownloadProgress(IReadOnlyList<DownloadProgress> updates)
    {
        if (!connections.TryGetNodeId(Context.ConnectionId, out var nodeId)) throw new HubException("Agent connection is not authenticated.");
        downloads.Update(nodeId, updates);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId, out _);
        await base.OnDisconnectedAsync(exception);
    }
}