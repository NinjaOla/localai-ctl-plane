using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentHeartbeatReceiver(
    IDbContextFactory<LlamactlDb> dbFactory,
    TimeProvider timeProvider)
{
    public async Task ReceiveAsync(
        Guid authenticatedNodeId,
        Envelope<AgentHeartbeat> envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.NodeId != authenticatedNodeId)
            throw new InvalidOperationException("Heartbeat node ID does not match the authenticated connection.");
        if (envelope.SchemaVersion != Protocol.SchemaVersion)
            throw new InvalidOperationException($"Unsupported protocol schema version {envelope.SchemaVersion}.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleAsync(
            candidate => candidate.Id == authenticatedNodeId,
            cancellationToken);
        node.LastSeen = timeProvider.GetUtcNow();
        node.Health = node.AnnouncementJson is null
            ? NodeHealth.PendingConfiguration
            : envelope.Payload.ValidationIssues.Any(issue => issue.Severity == ValidationSeverity.Error)
                ? NodeHealth.Degraded
                : NodeHealth.Healthy;
        node.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }
}